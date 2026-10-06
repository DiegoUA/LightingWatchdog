using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetworkWatchdog.TrayApp
{
    /// <summary>
    /// Minimal structured error log. Replaces the silent `catch { }` blocks that
    /// hid the Historical Archive failure. Per-user (%LocalAppData%), no admin
    /// rights needed. Best-effort: never throws back into the caller.
    /// </summary>
    internal static class TrayLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetworkWatchdog", "tray.log");
        private static readonly object Gate = new();

        public static void Log(string context, Exception? ex = null)
        {
            try
            {
                lock (Gate)
                {
                    string? dir = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // Crude size cap so a persistent failure can't fill the disk.
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                        File.Move(LogPath, LogPath + ".1", overwrite: true);

                    File.AppendAllText(LogPath,
                        $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z [{context}]" + (ex != null ? $" {ex}" : "") + Environment.NewLine);
                }
            }
            catch { /* logging must never throw */ }
        }
    }

    internal enum HealthBand { Green, Amber, Red }

    /// <summary>
    /// Colour banding derived from the ACTIVE restart threshold instead of fixed
    /// socket counts. The old logic hardcoded 500 / 1000, so a process that the
    /// service restarts at ~1001 could never be shown as red (it sat at 990 in
    /// amber, then vanished on restart). Bands now scale with whatever limit the
    /// user has selected (slider / appsettings / per-service override).
    /// </summary>
    internal static class HealthBands
    {
        // Share of the restart threshold at which each band STARTS.
        // Defaults at a 1000 limit: green < 500, amber 500-799, red >= 800.
        public const double AmberStartFraction = 0.50;
        public const double RedStartFraction = 0.80;
        public const int FallbackThreshold = 1000;

        public static int EffectiveThreshold(ProcessTelemetryItem p, TelemetryPacket pk)
        {
            if (p.Threshold > 0) return p.Threshold;                  // per-service override (newer services)
            if (pk.GlobalMaxTcpConnections > 0) return pk.GlobalMaxTcpConnections;
            return FallbackThreshold;
        }

        public static HealthBand Classify(int connections, int threshold)
        {
            if (threshold <= 0) threshold = FallbackThreshold;
            double ratio = (double)connections / threshold;
            if (ratio >= RedStartFraction) return HealthBand.Red;
            if (ratio >= AmberStartFraction) return HealthBand.Amber;
            return HealthBand.Green;
        }

        public static string Label(HealthBand b) => b switch
        {
            HealthBand.Red => "CRITICAL LEAK",
            HealthBand.Amber => "Elevated",
            _ => "Healthy"
        };

        public static Color RowColor(HealthBand b) => b switch
        {
            HealthBand.Red => Color.FromArgb(255, 205, 205),
            HealthBand.Amber => Color.FromArgb(255, 232, 190),
            _ => Color.FromArgb(214, 240, 214)
        };
    }

    /// <summary>Shared IPC helpers (bounded reads, server identity check, history query).</summary>
    internal static class Ipc
    {
        public const int MaxLineChars = 64 * 1024;
        public const int MaxHistoryLineChars = 1024 * 1024;

        // A hostile/buggy pipe server could stream data with no newline;
        // StreamReader.ReadLine() has no ceiling.
        public static string? ReadBoundedLine(StreamReader reader, int maxChars)
        {
            var sb = new StringBuilder();
            int ch;
            while ((ch = reader.Read()) != -1)
            {
                char c = (char)ch;
                if (c == '\n') break;
                if (c == '\r') continue;
                if (sb.Length >= maxChars) throw new InvalidDataException($"IPC line exceeded {maxChars} characters.");
                sb.Append(c);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // UNVERIFIED NATIVE INTEROP (not compiled/run in the authoring environment):
        // confirm on a real machine. Fails CLOSED. The service installs as
        // NetworkWatchdogService.exe (see installer), so the process name matches.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr hNamedPipe, out uint serverProcessId);

        public static bool VerifyServerIdentity(NamedPipeClientStream pipe)
        {
            try
            {
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid)) return false;
                using var p = Process.GetProcessById((int)serverPid);
                return string.Equals(p.ProcessName, "NetworkWatchdogService", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                TrayLogger.Log("Ipc.VerifyServerIdentity", ex);
                return false;
            }
        }

        public enum ControlResult { Ok, NotAuthorized, Unsupported, Failed }

        /// <summary>Evidence cases stored by the service (metadata only; telemetry pipe, any user).</summary>
        public static (List<EvidenceCaseInfo>? Cases, string? Error) QueryEvidenceList()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "NetworkWatchdogTelemetry", PipeDirection.InOut);
                pipe.Connect(1500);
                if (!VerifyServerIdentity(pipe)) return (null, "Pipe server identity verification failed.");

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine("LIST_EVIDENCE");

                string? response = ReadBoundedLine(reader, MaxHistoryLineChars);
                if (string.IsNullOrWhiteSpace(response))
                    return (null, "The service returned no evidence list (it may be an older build without evidence support).");

                var packet = JsonSerializer.Deserialize<EvidenceListPacket>(response);
                return (packet?.Cases ?? new List<EvidenceCaseInfo>(), null);
            }
            catch (Exception ex)
            {
                TrayLogger.Log("Ipc.QueryEvidenceList", ex);
                return (null, $"Evidence list query failed: {ex.Message}");
            }
        }

        /// <summary>One command on the Administrators-only control pipe; returns the service's reply.</summary>
        private static (ControlResult Result, string Message) SendControl(string command, bool allowImpersonation)
        {
            try
            {
                // Export makes the SERVICE write the destination file while impersonating this user,
                // so Windows enforces this user's own permissions on that path.
                using var pipe = allowImpersonation
                    ? new NamedPipeClientStream(".", "NetworkWatchdogControl", PipeDirection.InOut, PipeOptions.None,
                                                System.Security.Principal.TokenImpersonationLevel.Impersonation)
                    : new NamedPipeClientStream(".", "NetworkWatchdogControl", PipeDirection.InOut);
                pipe.Connect(3000);
                if (!VerifyServerIdentity(pipe)) return (ControlResult.Failed, "Pipe server identity verification failed.");

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine(command);

                string? reply = ReadBoundedLine(reader, MaxLineChars);
                if (string.IsNullOrWhiteSpace(reply))
                    return (ControlResult.Unsupported, "The service did not answer (it may be an older build without evidence support).");
                if (reply.StartsWith("OK", StringComparison.Ordinal)) return (ControlResult.Ok, reply);
                if (reply.StartsWith("ERR:", StringComparison.Ordinal)) return (ControlResult.Failed, reply.Substring(4));
                return (ControlResult.Failed, reply);
            }
            catch (UnauthorizedAccessException)
            {
                return (ControlResult.NotAuthorized, "Administrator rights are required.");
            }
            catch (TimeoutException)
            {
                return (ControlResult.Failed, "The service did not respond. Is the NetworkWatchdogService running?");
            }
            catch (Exception ex)
            {
                TrayLogger.Log("Ipc.SendControl", ex);
                return (ControlResult.Failed, ex.Message);
            }
        }

        /// <summary>Asks the service to capture evidence for a live process now (non-destructive).</summary>
        public static (ControlResult Result, string Message) CaptureEvidence(int pid)
            => SendControl("CAPTURE_EVIDENCE:" + JsonSerializer.Serialize(new CaptureEvidenceCommand { Pid = pid }), false);

        /// <summary>Copies a stored evidence case to a destination chosen by the user.</summary>
        public static (ControlResult Result, string Message) ExportEvidence(string caseId, string destinationPath)
            => SendControl("EXPORT_EVIDENCE:" + JsonSerializer.Serialize(new ExportEvidenceCommand { CaseId = caseId, DestinationPath = destinationPath }), true);

        /// <summary>
        /// Asks the service (running as SYSTEM) for the persisted mitigation
        /// history over the telemetry pipe. Needed because the service locks
        /// %ProgramData%\NetworkWatchdogService to Administrators/SYSTEM, so the
        /// unprivileged tray app cannot read MitigationHistory.csv itself.
        /// Returns (null, reason) when unavailable.
        /// </summary>
        public static (List<string>? Entries, string? Error) QueryHistory()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "NetworkWatchdogTelemetry", PipeDirection.InOut);
                pipe.Connect(1500);
                if (!VerifyServerIdentity(pipe)) return (null, "Pipe server identity verification failed.");

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine("GET_HISTORY");

                string? response = ReadBoundedLine(reader, MaxHistoryLineChars);
                if (string.IsNullOrWhiteSpace(response))
                    return (null, "The service returned no history (it may be an older build without GET_HISTORY support).");

                var packet = JsonSerializer.Deserialize<HistoryPacket>(response);
                return (packet?.Entries ?? new List<string>(), null);
            }
            catch (Exception ex)
            {
                TrayLogger.Log("Ipc.QueryHistory", ex);
                return (null, $"IPC history query failed: {ex.Message}");
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // Unhandled exceptions used to vanish with no trace.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                TrayLogger.Log("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                TrayLogger.Log("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _updateTimer;
        private bool _isSilentMode = false;
        private string _lastRestartEvent = string.Empty;
        private bool _restartBaselineSet;
        private bool _evidenceAutoCapture;
        private DashboardForm? _dashboardForm;

        private readonly Icon _iconGreen;
        private readonly Icon _iconOrange;
        private readonly Icon _iconRed;
        private readonly Icon _iconGray;
        private readonly Font _boldFont;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayApplicationContext()
        {
            _iconGreen = GenerateCachedIcon(Color.Green);
            _iconOrange = GenerateCachedIcon(Color.Orange);
            _iconRed = GenerateCachedIcon(Color.Red);
            _iconGray = GenerateCachedIcon(Color.Gray);
            _boldFont = new Font(Control.DefaultFont, FontStyle.Bold);

            _trayIcon = new NotifyIcon
            {
                Icon = _iconGray,
                Text = "NetworkWatchdog: Initializing IPC...",
                Visible = true
            };

            _trayIcon.DoubleClick += (s, e) => ShowDashboard();
            RebuildContextMenu(new List<ProcessTelemetryItem>());

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (s, e) => PollTelemetry();
            _timer.Start();

            _updateTimer = new System.Windows.Forms.Timer { Interval = 86400000 };
            _updateTimer.Tick += async (s, e) =>
            {
                try { await GitHubAutoUpdater.CheckForUpdatesAsync(); } catch (Exception ex) { TrayLogger.Log("UpdateTimer", ex); }
            };
            _updateTimer.Start();

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await GitHubAutoUpdater.CheckForUpdatesAsync();
            });
        }

        private Icon GenerateCachedIcon(Color color)
        {
            using var bitmap = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bitmap);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 0, 0, 16, 16);
            
            IntPtr hIcon = bitmap.GetHicon();
            using var tempIcon = Icon.FromHandle(hIcon);
            var finalIcon = (Icon)tempIcon.Clone();
            DestroyIcon(hIcon);
            return finalIcon;
        }

        private void RebuildContextMenu(List<ProcessTelemetryItem> elevated)
        {
            var oldMenu = _trayIcon.ContextMenuStrip;
            
            var menu = new ContextMenuStrip();
            var openDash = new ToolStripMenuItem("Open Dashboard", null, (s, e) => ShowDashboard())
            {
                Font = _boldFont
            };
            menu.Items.Add(openDash);

            var silent = new ToolStripMenuItem("Silent Mode", null, (s, e) =>
            {
                _isSilentMode = !_isSilentMode;
                ((ToolStripMenuItem)s!).Checked = _isSilentMode;
            }) { Checked = _isSilentMode };
            menu.Items.Add(silent);

            var checkUpdates = new ToolStripMenuItem("Check for Updates", null, async (s, e) => await GitHubAutoUpdater.CheckForUpdatesAsync(manualCheck: true));
            menu.Items.Add(checkUpdates);

            // Opt-in switch for automatic evidence capture (OFF by default). The state shown here is
            // whatever the service reports; toggling needs administrator rights and is persisted.
            var autoEvidence = new ToolStripMenuItem("Capture evidence automatically before restarts", null, (s, e) =>
            {
                bool want = !_evidenceAutoCapture;
                if (SendIpcConfigUpdate(new ConfigUpdateCommand { AutoCaptureEvidence = want, SaveRequested = true }))
                    _evidenceAutoCapture = want;
            }) { Checked = _evidenceAutoCapture };
            menu.Items.Add(autoEvidence);

            var collectEvidence = new ToolStripMenuItem("Collect Evidence (one click)...", null, (s, e) =>
            {
                ShowDashboard();
                if (_dashboardForm != null) _ = _dashboardForm.CollectEvidenceOneClickAsync();
            });
            menu.Items.Add(collectEvidence);

            if (elevated.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                var killMenu = new ToolStripMenuItem("Terminate Process Tree (Manual)");
                foreach (var proc in elevated)
                {
                    killMenu.DropDownItems.Add($"{proc.ServiceName} (PID {proc.Pid} - {proc.Connections} sockets)", null, (s, e) =>
                    {
                        KillProcessTree(proc.Pid, proc.ServiceName);
                    });
                }
                menu.Items.Add(killMenu);
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());
            
            _trayIcon.ContextMenuStrip = menu;

            if (oldMenu != null)
            {
                Task.Delay(500).ContinueWith(_ => oldMenu.Dispose());
            }
        }

        private void KillProcessTree(int pid, string name)
        {
            try
            {
                // Native kill (no taskkill.exe shell-out) + identity re-check: the PID
                // may have been recycled since the row was drawn. NOTE: this compares
                // process NAME only; a StartTime check needs that field added to the
                // telemetry DTO on both sides.
                using var proc = Process.GetProcessById(pid);
                string currentName = proc.ProcessName;
                string expectedClean = Path.GetFileNameWithoutExtension(name);

                if (!string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(currentName, expectedClean, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        $"PID {pid} is now running a different process ({currentName}) than the one shown ({name}). Refusing to terminate it.",
                        "Process Identity Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
                _trayIcon.ShowBalloonTip(3000, "Process Terminated", $"Terminated {name} (PID {pid}).", ToolTipIcon.Warning);
            }
            catch (ArgumentException)
            {
                // Already exited - nothing to do.
            }
            catch (Exception ex)
            {
                TrayLogger.Log($"KillProcessTree(pid={pid}, name={name})", ex);
                MessageBox.Show($"Failed to kill PID {pid}: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowDashboard()
        {
            if (_dashboardForm == null || _dashboardForm.IsDisposed)
            {
                _dashboardForm = new DashboardForm(KillProcessTree, SendIpcConfigUpdate);
            }
            _dashboardForm.Show();
            _dashboardForm.BringToFront();
            _dashboardForm.WindowState = FormWindowState.Normal;
        }

        private void PollTelemetry()
        {
            TelemetryPacket? packet = QueryNamedPipe();

            if (packet != null)
            {
                // Connected. An EMPTY process list now means "nothing above the
                // service's display threshold" (healthy), not "disconnected" - the
                // old code showed grey "Connecting..." in that case.
                UpdateUiFromTelemetry(packet);
            }
            else
            {
                _trayIcon.Icon = _iconGray;
                SetTrayText("NetworkWatchdog [Connecting...]");
            }
        }

        private TelemetryPacket? QueryNamedPipe()
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "NetworkWatchdogTelemetry", PipeDirection.InOut);
                pipeClient.Connect(1000);

                if (!Ipc.VerifyServerIdentity(pipeClient))
                {
                    TrayLogger.Log("QueryNamedPipe: server identity verification failed; treating as disconnected.");
                    return null;
                }

                using var reader = new StreamReader(pipeClient, Encoding.UTF8);
                using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };

                writer.WriteLine("GET_TELEMETRY");
                string? response = Ipc.ReadBoundedLine(reader, Ipc.MaxLineChars);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    return JsonSerializer.Deserialize<TelemetryPacket>(response);
                }
            }
            catch (TimeoutException) { /* service not running / pipe not yet created: normal, stay quiet */ }
            catch (Exception ex)
            {
                TrayLogger.Log("QueryNamedPipe", ex);
            }
            return null;
        }

        public static bool SendIpcConfigUpdate(ConfigUpdateCommand cmd)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "NetworkWatchdogControl", PipeDirection.InOut);
                pipeClient.Connect(300);

                if (!Ipc.VerifyServerIdentity(pipeClient))
                {
                    TrayLogger.Log("SendIpcConfigUpdate: server identity verification failed; refusing to send.");
                    return false;
                }

                using var reader = new StreamReader(pipeClient, Encoding.UTF8);
                using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };

                string json = JsonSerializer.Serialize(cmd);
                writer.WriteLine($"UPDATE_CONFIG:{json}");
                return Ipc.ReadBoundedLine(reader, Ipc.MaxLineChars) == "OK";
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Administrator privileges are required to modify watchdog configurations.\r\n\r\nPlease exit and restart the NetworkWatchdog Dashboard as Administrator.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            catch (Exception ex)
            {
                TrayLogger.Log("SendIpcConfigUpdate", ex);
                return false;
            }
        }

        // NotifyIcon.Text throws ArgumentOutOfRangeException above 127 characters;
        // a long process name could otherwise crash the poll timer.
        private void SetTrayText(string text)
        {
            _trayIcon.Text = text.Length <= 127 ? text : text.Substring(0, 124) + "...";
        }

        private void UpdateUiFromTelemetry(TelemetryPacket packet)
        {
            _evidenceAutoCapture = packet.EvidenceAutoCapture;

            // Every process is judged against ITS OWN active threshold (per-service
            // override when the service reports one, otherwise the global limit the
            // user currently has selected), so colours track the real restart point.
            var scored = packet.Processes
                .Select(p =>
                {
                    int threshold = HealthBands.EffectiveThreshold(p, packet);
                    return new
                    {
                        Item = p,
                        Threshold = threshold,
                        Band = HealthBands.Classify(p.Connections, threshold),
                        Ratio = (double)p.Connections / Math.Max(1, threshold)
                    };
                })
                .ToList();

            var elevated = scored.Where(x => x.Band != HealthBand.Green)
                                 .OrderByDescending(x => x.Ratio)
                                 .Select(x => x.Item)
                                 .ToList();

            if (_trayIcon.ContextMenuStrip == null || !_trayIcon.ContextMenuStrip.Visible)
            {
                RebuildContextMenu(elevated);
            }

            if (scored.Count == 0)
            {
                _trayIcon.Icon = _iconGreen;
                SetTrayText($"NetworkWatchdog [Healthy]\nNo process above display threshold\nLimit: {Math.Max(packet.GlobalMaxTcpConnections, 0)} sockets");
            }
            else
            {
                var worst = scored.OrderByDescending(x => x.Ratio).ThenByDescending(x => x.Item.Connections).First();
                _trayIcon.Icon = worst.Band switch
                {
                    HealthBand.Red => _iconRed,
                    HealthBand.Amber => _iconOrange,
                    _ => _iconGreen
                };
                int pct = (int)Math.Round(worst.Ratio * 100);
                SetTrayText($"NetworkWatchdog [{HealthBands.Label(worst.Band)}]\nTop: {worst.Item.ServiceName} (PID {worst.Item.Pid}): {worst.Item.Connections}/{worst.Threshold} ({pct}%)");
            }

            // The service's RecentRestarts queue is ordered OLDEST -> NEWEST (see the Session
            // Logs tab). Reading element [0] meant "latest restart" was really the oldest one,
            // so after the first mitigation of a session the value never changed again and no
            // further balloon alerts / auto-evidence triggers ever fired. Use the last element,
            // and treat the first packet after launch as a baseline so a stale entry doesn't
            // pop a balloon every time the tray app starts.
            string latestRestart = packet.RecentRestarts.Count > 0
                ? packet.RecentRestarts[packet.RecentRestarts.Count - 1]
                : string.Empty;

            if (!_restartBaselineSet)
            {
                _restartBaselineSet = true;
                _lastRestartEvent = latestRestart;
            }
            else if (latestRestart.Length > 0 && latestRestart != _lastRestartEvent)
            {
                _lastRestartEvent = latestRestart;
                if (!_isSilentMode)
                {
                    _trayIcon.ShowBalloonTip(4000, "⚠️ Socket Leak Mitigated!", $"Rogue sockets cleared:\n{latestRestart}", ToolTipIcon.Warning);
                }

                // Evidence is now captured by the SERVICE before the process is killed; the dashboard
                // only has to refresh its list of stored cases.
                if (_dashboardForm != null && !_dashboardForm.IsDisposed)
                {
                    _dashboardForm.OnMitigationDetected();
                }
            }

            if (_dashboardForm != null && !_dashboardForm.IsDisposed && _dashboardForm.Visible)
            {
                _dashboardForm.RefreshData(packet);
            }
        }

        private void Exit()
        {
            _timer.Stop();
            _updateTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _dashboardForm?.Close();
            Application.Exit();
        }
    }

    public static class GitHubAutoUpdater
    {
        private const string RepoOwner = "DiegoUA";
        private const string RepoName = "LightingWatchdog";

        public static Version GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 8, 0, 0);
        }

        public static async Task CheckForUpdatesAsync(bool manualCheck = false)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NetworkWatchdog-AutoUpdater/1.0");
                
                string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                string json = await client.GetStringAsync(url);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string latestTag = root.GetProperty("tag_name").GetString() ?? "";

                string cleanTag = latestTag.TrimStart('v', 'V');
                if (Version.TryParse(cleanTag, out var remoteVersion))
                {
                    var localVersion = GetCurrentVersion();
                    var normalizedRemote = new Version(remoteVersion.Major, remoteVersion.Minor, Math.Max(0, remoteVersion.Build));
                    var normalizedLocal = new Version(localVersion.Major, localVersion.Minor, Math.Max(0, localVersion.Build));

                    if (normalizedRemote > normalizedLocal)
                    {
                        string downloadUrl = "";
                        foreach (var asset in root.GetProperty("assets").EnumerateArray())
                        {
                            string name = asset.GetProperty("name").GetString() ?? "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            var result = MessageBox.Show(
                                $"A new version of NetworkWatchdog ({latestTag}) is available on GitHub.\n\n" +
                                $"Would you like to download and install it now? This will briefly restart the background service.", 
                                "NetworkWatchdog Update Available", 
                                MessageBoxButtons.YesNo, 
                                MessageBoxIcon.Information);
                                
                            if (result == DialogResult.Yes)
                            {
                                await DownloadAndInstallAsync(client, downloadUrl);
                            }
                        }
                    }
                    else if (manualCheck)
                    {
                        MessageBox.Show($"You are running the latest version of NetworkWatchdog ({localVersion.Major}.{localVersion.Minor}.{Math.Max(0, localVersion.Build)}).", "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else if (manualCheck)
                {
                    MessageBox.Show($"Could not parse release tag format: {latestTag}", "Version Parse Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                TrayLogger.Log("GitHubAutoUpdater.CheckForUpdatesAsync", ex);
                if (manualCheck)
                {
                    MessageBox.Show($"Failed to check GitHub for updates:\n{ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static async Task DownloadAndInstallAsync(HttpClient client, string downloadUrl)
        {
            try
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "NetworkWatchdog_Installer.exe");
                
                byte[] fileBytes = await client.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempFile, fileBytes);

                var psi = new ProcessStartInfo
                {
                    FileName = tempFile,
                    Arguments = "/SILENT",
                    UseShellExecute = true
                };
                Process.Start(psi);
                
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Auto-Update failed during download/execution:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public static class EvidenceCollector
    {
        [DllImport("DbgHelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            SafeHandle hFile,
            uint dumpType,
            IntPtr expParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        private const uint MiniDumpWithFullMemory = 0x00000002;

        public static async Task GenerateEvidenceBundleAsync(int targetPid, string targetProcessName, string zipDestinationPath)
        {
            await Task.Run(() =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"NW_Evidence_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    // 1. Dynamic Port Info
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "netsh",
                            Arguments = "int ipv4 show dynamicport tcp",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        string output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                        p?.WaitForExit();
                        File.WriteAllText(Path.Combine(tempDir, "01_System_DynamicPortInfo.txt"), output);
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(tempDir, "01_System_DynamicPortInfo.txt"), $"Error collecting dynamic port info: {ex.Message}");
                    }

                    // 2. Authoritative CIM Bound Sockets via PowerShell (Zero NuGet dependency)
                    try
                    {
                        string csvPath = Path.Combine(tempDir, "02_LiveSockets_CIM.csv");
                        var psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        psi.ArgumentList.Add("-NoProfile");
                        psi.ArgumentList.Add("-ExecutionPolicy");
                        psi.ArgumentList.Add("Bypass");
                        psi.ArgumentList.Add("-Command");
                        psi.ArgumentList.Add($"Get-NetTCPConnection | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess | Export-Csv -Path '{csvPath}' -NoTypeInformation");

                        using var p = Process.Start(psi);
                        p?.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(tempDir, "02_LiveSockets_CIM.csv"), $"Error querying CIM: {ex.Message}");
                    }

                    // 3. Active Process Snapshot
                    try
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("PID,ProcessName,HandleCount,Threads,WorkingSetMB");
                        foreach (var proc in Process.GetProcesses())
                        {
                            try
                            {
                                sb.AppendLine($"{proc.Id},{proc.ProcessName},{proc.HandleCount},{proc.Threads.Count},{proc.WorkingSet64 / (1024 * 1024)}");
                            }
                            catch { /* process exited / access denied while sampling: skip this row */ }
                            finally { proc.Dispose(); }
                        }
                        File.WriteAllText(Path.Combine(tempDir, "03_ActiveProcessSnapshot.csv"), sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(tempDir, "03_ActiveProcessSnapshot.csv"), $"Error generating process snapshot: {ex.Message}");
                    }

                    // 4. Mitigation Audit Trail
                    // The service locks its ProgramData folder to Administrators/SYSTEM, so
                    // ask it over IPC instead of copying the file (which silently produced
                    // an empty audit trail for non-admin users).
                    var (histEntries, histError) = Ipc.QueryHistory();
                    if (histEntries != null)
                    {
                        File.WriteAllLines(Path.Combine(tempDir, "04_MitigationAuditTrail.csv"), histEntries);
                    }
                    else
                    {
                        File.WriteAllText(Path.Combine(tempDir, "04_MitigationAuditTrail.txt"),
                            $"Mitigation history unavailable: {histError}");
                    }

                    // 5. Memory Dump (DbgHelp.dll)
                    if (targetPid > 4)
                    {
                        try
                        {
                            using var targetProcess = Process.GetProcessById(targetPid);
                            string dmpPath = Path.Combine(tempDir, "05_ProcessMemoryDump.dmp");
                            using var fs = new FileStream(dmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                            MiniDumpWriteDump(targetProcess.Handle, (uint)targetPid, fs.SafeFileHandle, MiniDumpWithFullMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        }
                        catch (Exception ex)
                        {
                            File.WriteAllText(Path.Combine(tempDir, "05_ProcessMemoryDump_Error.txt"), $"Error dumping PID {targetPid}: {ex.Message}");
                        }
                    }

                    // 6. Watchdog Event Log
                    try
                    {
                        var log = new EventLog("Application");
                        var sb = new StringBuilder();
                        foreach (EventLogEntry entry in log.Entries)
                        {
                            if (entry.Source.Contains("NetworkWatchdog") || entry.Message.Contains("NetworkWatchdog"))
                            {
                                sb.AppendLine($"[{entry.TimeGenerated}] [{entry.EntryType}] {entry.Message}");
                            }
                        }
                        File.WriteAllText(Path.Combine(tempDir, "06_Watchdog_EventLog.txt"), sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(tempDir, "06_Watchdog_EventLog.txt"), $"Error reading event log: {ex.Message}");
                    }

                    // Compress to Target ZIP
                    if (File.Exists(zipDestinationPath)) File.Delete(zipDestinationPath);
                    ZipFile.CreateFromDirectory(tempDir, zipDestinationPath);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });
        }
    }

    public class DashboardForm : Form
    {
        private readonly Action<int, string> _killAction;
        private readonly Func<ConfigUpdateCommand, bool> _sendConfigAction;
        private readonly TabControl _tabs;
        private readonly DataGridView _gridProcesses;
        private readonly DataGridView _gridRestartsSession;
        private readonly DataGridView _gridRestartsHistorical;
        private readonly ListBox _listWhitelist;
        private readonly TrackBar _sliderThreshold;
        private readonly NumericUpDown _numThreshold;
        private readonly CheckBox _chkAutoCaptureEvidence;
        private readonly DataGridView _gridEvidence;
        private readonly Label _lblEvidenceStatus;
        private bool _suppressCheckEvent;
        private bool _evidenceBusy;
        private bool _evidenceListLoading;

        // Direct-file FALLBACK only. The service locks this folder to
        // Administrators/SYSTEM, so an unprivileged dashboard normally cannot read it;
        // the primary path is the GET_HISTORY IPC command (see LoadHistoricalLog).
        private readonly string _historyCsvPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "MitigationHistory.csv");

        private readonly Label _lblBuildMarker;
        private readonly Label _lblHistoryStatus;
        private readonly Button _btnReloadHistory;
        private bool _historyLoading;
        private string _lastHistoryTrigger = string.Empty;
        private bool _historyTriggerBaselineSet;

        private TelemetryPacket? _latestPacket;

        /// <summary>
        /// Mirrors the service's own whitelist-entry normalisation so both sides agree on
        /// what a valid entry is (trim, 260-char cap, no path separators/quotes/control chars).
        /// </summary>
        private static bool TryNormalizeWhitelistEntry(string? raw, out string entry)
        {
            entry = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string trimmed = raw.Trim();
            if (trimmed.Length > 260) return false;
            foreach (char c in trimmed)
                if (char.IsControl(c) || c == '/' || c == '\\' || c == '"') return false;

            entry = Path.GetFileNameWithoutExtension(trimmed);
            return !string.IsNullOrWhiteSpace(entry);
        }

        public DashboardForm(Action<int, string> killAction, Func<ConfigUpdateCommand, bool> sendConfigAction)
        {
            _killAction = killAction;
            _sendConfigAction = sendConfigAction;

            Text = "NetworkWatchdog Telemetry & Control Dashboard";
            Size = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;

            _tabs = new TabControl { Dock = DockStyle.Fill };

            var tabProcesses = new TabPage("Live Processes (IPC)");
            _gridProcesses = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
            _gridProcesses.Columns.Add("ServiceName", "Process");
            _gridProcesses.Columns.Add("Pid", "PID");
            _gridProcesses.Columns.Add("Connections", "Sockets / Handles");
            _gridProcesses.Columns.Add("Status", "Status");

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
            var btnKill = new Button { Text = "Terminate Process Tree", Width = 170, Height = 32, Top = 6, Left = 10 };
            btnKill.Click += (s, e) =>
            {
                if (_gridProcesses.SelectedRows.Count > 0)
                {
                    var row = _gridProcesses.SelectedRows[0];
                    string name = row.Cells["ServiceName"].Value?.ToString() ?? "";
                    int pid = int.TryParse(row.Cells["Pid"].Value?.ToString(), out int p) ? p : 0;
                    if (pid > 0) _killAction(pid, name);
                }
            };

            var btnEvidence = new Button { Text = "Collect Evidence (1 click)", Width = 210, Height = 32, Top = 6, Left = 190 };
            btnEvidence.Click += async (s, e) => await CollectEvidenceOneClickAsync();

            // Shows which service build is answering, so a stale deployment is obvious
            // at a glance (the service has always sent BuildMarker; the tray used to drop it).
            _lblBuildMarker = new Label { Text = "", AutoSize = true, Top = 15, Left = 420, ForeColor = Color.Gray };

            btnPanel.Controls.Add(btnKill);
            btnPanel.Controls.Add(btnEvidence);
            btnPanel.Controls.Add(_lblBuildMarker);
            tabProcesses.Controls.Add(_gridProcesses);
            tabProcesses.Controls.Add(btnPanel);

            var tabRestartsSession = new TabPage("Session Logs");
            _gridRestartsSession = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true };
            _gridRestartsSession.Columns.Add("Timestamp", "Timestamp");
            _gridRestartsSession.Columns.Add("ServiceName", "Process");
            _gridRestartsSession.Columns.Add("Pid", "PID");
            _gridRestartsSession.Columns.Add("Reason", "Reason");
            tabRestartsSession.Controls.Add(_gridRestartsSession);

            var tabRestartsHistorical = new TabPage("Historical Archive");
            _gridRestartsHistorical = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true };
            _gridRestartsHistorical.Columns.Add("Timestamp", "Timestamp");
            _gridRestartsHistorical.Columns.Add("ServiceName", "Process");
            _gridRestartsHistorical.Columns.Add("Pid", "PID");
            _gridRestartsHistorical.Columns.Add("Reason", "Reason");

            var pnlHistoricalControls = new Panel { Dock = DockStyle.Bottom, Height = 45 };
            _btnReloadHistory = new Button { Text = "Reload Historical Log", Width = 180, Height = 32, Top = 6, Left = 10 };
            _btnReloadHistory.Click += (s, e) => LoadHistoricalLog();
            // Visible outcome for every load attempt. Previously failures were swallowed
            // by a bare catch, so an empty tab and a dead Reload button looked identical.
            _lblHistoryStatus = new Label { Text = "", AutoSize = false, Top = 6, Left = 200, Width = 560, Height = 32, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
            pnlHistoricalControls.Controls.Add(_btnReloadHistory);
            pnlHistoricalControls.Controls.Add(_lblHistoryStatus);

            tabRestartsHistorical.Controls.Add(_gridRestartsHistorical);
            tabRestartsHistorical.Controls.Add(pnlHistoricalControls);

            // ---- Evidence Cases tab: R&D evidence captured by the service BEFORE a leaking process is killed ----
            var tabEvidence = new TabPage("Evidence Cases");
            var pnlEvidenceControls = new Panel { Dock = DockStyle.Top, Height = 78 };
            var btnCollectNow = new Button { Text = "Collect && Export Evidence (1 click)", Width = 250, Height = 32, Top = 6, Left = 10 };
            btnCollectNow.Click += async (s, e) => await CollectEvidenceOneClickAsync();
            var btnExportCase = new Button { Text = "Export Selected Case (.zip)", Width = 210, Height = 32, Top = 6, Left = 270 };
            btnExportCase.Click += async (s, e) => await ExportSelectedCaseAsync();
            var btnRefreshCases = new Button { Text = "Refresh List", Width = 120, Height = 32, Top = 6, Left = 490 };
            btnRefreshCases.Click += (s, e) => RefreshEvidenceList();
            _lblEvidenceStatus = new Label { Text = "", AutoSize = false, Top = 42, Left = 10, Width = 760, Height = 32, ForeColor = Color.DimGray };
            pnlEvidenceControls.Controls.AddRange(new Control[] { btnCollectNow, btnExportCase, btnRefreshCases, _lblEvidenceStatus });

            _gridEvidence = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false
            };
            _gridEvidence.Columns.Add("Captured", "Captured (local time)");
            _gridEvidence.Columns.Add("Process", "Process");
            _gridEvidence.Columns.Add("Pid", "PID");
            _gridEvidence.Columns.Add("Trigger", "Trigger");
            _gridEvidence.Columns.Add("Size", "Size (MB)");
            _gridEvidence.Columns.Add("Dump", "Dump");
            _gridEvidence.Columns.Add("CaseId", "Case");
            tabEvidence.Controls.Add(_gridEvidence);
            tabEvidence.Controls.Add(pnlEvidenceControls);

            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedTab == tabEvidence) RefreshEvidenceList();
            };

            var tabSettings = new TabPage("Settings & Whitelist");
            var pnlSettings = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15) };

            var lblThresh = new Label { Text = "Global Max TCP/UDP Connections Threshold:", Top = 15, Left = 15, Width = 300 };
            _sliderThreshold = new TrackBar { Top = 40, Left = 15, Width = 300, Minimum = 200, Maximum = 10000, TickFrequency = 250, SmallChange = 50, LargeChange = 250 };
            _numThreshold = new NumericUpDown { Top = 40, Left = 330, Width = 80, Minimum = 200, Maximum = 10000 };
            var btnSaveConfig = new Button { Text = "Save Configuration", Top = 38, Left = 430, Width = 150 };

            _sliderThreshold.MouseUp += (s, e) =>
            {
                if (_numThreshold.Value != _sliderThreshold.Value)
                    _numThreshold.Value = _sliderThreshold.Value;
                _sendConfigAction(new ConfigUpdateCommand { NewGlobalThreshold = _sliderThreshold.Value });
            };

            _numThreshold.ValueChanged += (s, e) =>
            {
                if (_sliderThreshold.Value != (int)_numThreshold.Value)
                    _sliderThreshold.Value = (int)_numThreshold.Value;
            };

            btnSaveConfig.Click += (s, e) =>
            {
                if (_sendConfigAction(new ConfigUpdateCommand { SaveRequested = true }))
                {
                    MessageBox.Show("Configuration successfully saved to disk.\n\nThresholds and Whitelist will persist across system reboots.", "Configuration Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            _chkAutoCaptureEvidence = new CheckBox
            {
                Text = "Capture R&&D evidence automatically BEFORE a leaking process is restarted (off by default)",
                Top = 80,
                Left = 15,
                Width = 520,
                Checked = false
            };
            _chkAutoCaptureEvidence.CheckedChanged += (s, e) =>
            {
                if (_suppressCheckEvent) return;
                _sendConfigAction(new ConfigUpdateCommand { AutoCaptureEvidence = _chkAutoCaptureEvidence.Checked, SaveRequested = true });
            };

            var lblWhite = new Label { Text = "Excluded / Whitelisted Processes (Bypass Auto-Kill):", Top = 115, Left = 15, Width = 350 };
            _listWhitelist = new ListBox { Top = 140, Left = 15, Width = 280, Height = 170 };

            var txtNewItem = new TextBox { Top = 320, Left = 15, Width = 180 };
            var btnAdd = new Button { Text = "Add", Top = 318, Left = 205, Width = 90 };
            btnAdd.Click += (s, e) =>
            {
                if (!TryNormalizeWhitelistEntry(txtNewItem.Text, out string proc))
                {
                    MessageBox.Show(
                        "Enter a valid process name (no path separators, quotes or control characters; 260 characters max).",
                        "Invalid Entry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_sendConfigAction(new ConfigUpdateCommand { AddWhitelist = proc }))
                {
                    _listWhitelist.Items.Add(proc);
                    txtNewItem.Clear();
                }
            };

            var btnRemove = new Button { Text = "Remove Selected", Top = 350, Left = 15, Width = 150 };
            btnRemove.Click += (s, e) =>
            {
                if (_listWhitelist.SelectedItem != null)
                {
                    string selected = _listWhitelist.SelectedItem.ToString() ?? "";
                    if (_sendConfigAction(new ConfigUpdateCommand { RemoveWhitelist = selected }))
                    {
                        _listWhitelist.Items.Remove(selected);
                    }
                }
            };

            pnlSettings.Controls.AddRange(new Control[] { lblThresh, _sliderThreshold, _numThreshold, btnSaveConfig, _chkAutoCaptureEvidence, lblWhite, _listWhitelist, txtNewItem, btnAdd, btnRemove });
            tabSettings.Controls.Add(pnlSettings);

            _tabs.TabPages.AddRange(new[] { tabProcesses, tabRestartsSession, tabRestartsHistorical, tabEvidence, tabSettings });
            Controls.Add(_tabs);

            LoadHistoricalLog();
        }

        // ======================== Evidence (service-side, captured BEFORE the kill) ========================
        private void SetEvidenceStatus(string text, Color color)
        {
            if (IsDisposed || _lblEvidenceStatus.IsDisposed) return;
            _lblEvidenceStatus.ForeColor = color;
            _lblEvidenceStatus.Text = text;
        }

        /// <summary>Called by the tray when a new mitigation appears (the service has just stored a case).</summary>
        public void OnMitigationDetected()
        {
            if (!IsDisposed && Visible) RefreshEvidenceList();
        }

        private async void RefreshEvidenceList()
        {
            if (_evidenceListLoading) return;
            _evidenceListLoading = true;
            try
            {
                var (cases, error) = await Task.Run(Ipc.QueryEvidenceList);
                if (IsDisposed) return;

                if (cases == null)
                {
                    SetEvidenceStatus(error ?? "Evidence list unavailable.", Color.Firebrick);
                    return;
                }

                _gridEvidence.Rows.Clear();
                foreach (var c in cases)
                {
                    string when = DateTime.TryParse(c.TimestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t)
                        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : c.TimestampUtc;
                    _gridEvidence.Rows.Add(when, c.ProcessName, c.Pid, c.Trigger, Math.Round(c.SizeBytes / (1024.0 * 1024.0), 1), c.DumpMode, c.CaseId);
                }

                SetEvidenceStatus(cases.Count == 0
                    ? "No evidence cases yet. They are created automatically right before a leaking process is restarted, or on demand with the button above."
                    : $"{cases.Count} evidence case(s) stored by the service. Select one and press Export to save it as a .zip.", Color.DimGray);
            }
            catch (Exception ex)
            {
                TrayLogger.Log("RefreshEvidenceList", ex);
                SetEvidenceStatus($"Failed to list evidence: {ex.Message}", Color.Firebrick);
            }
            finally
            {
                _evidenceListLoading = false;
            }
        }

        private static bool RelaunchAsAdministrator()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;   // the user declined the UAC prompt
            }
            catch (Exception ex)
            {
                TrayLogger.Log("RelaunchAsAdministrator", ex);
                return false;
            }
        }

        /// <summary>Returns true if the dashboard is relaunching elevated (caller should stop).</summary>
        private bool OfferElevation()
        {
            var answer = MessageBox.Show(
                "Collecting and exporting evidence needs administrator rights, because the watchdog service keeps evidence " +
                "(including memory dumps) in a folder that only Administrators can read.\n\n" +
                "Restart the dashboard as Administrator now? Windows will ask you to confirm.",
                "Administrator Rights Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes && RelaunchAsAdministrator())
            {
                Application.Exit();
                return true;
            }
            return false;
        }

        /// <summary>
        /// One click: the SERVICE captures evidence for the chosen process (selected row, otherwise the one
        /// closest to its limit) and the finished .zip is saved where the user chooses. Falls back to the
        /// limited local collector only when the service cannot do it.
        /// </summary>
        public async Task CollectEvidenceOneClickAsync()
        {
            if (_evidenceBusy) return;

            int targetPid = 0;
            string targetName = "Unknown";
            if (_gridProcesses.SelectedRows.Count > 0)
            {
                var row = _gridProcesses.SelectedRows[0];
                targetName = row.Cells["ServiceName"].Value?.ToString() ?? "Target";
                targetPid = int.TryParse(row.Cells["Pid"].Value?.ToString(), out int p) ? p : 0;
            }
            else if (_latestPacket != null && _latestPacket.Processes.Count > 0)
            {
                var packet = _latestPacket;
                var top = packet.Processes
                    .OrderByDescending(x => (double)x.Connections / Math.Max(1, HealthBands.EffectiveThreshold(x, packet)))
                    .First();
                targetPid = top.Pid;
                targetName = top.ServiceName;
            }

            if (targetPid <= 4)
            {
                MessageBox.Show("No process is available to investigate yet. Select a process on the Live Processes tab (or wait until one appears there) and try again.",
                    "No Process Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var consent = MessageBox.Show(
                $"This captures evidence for '{targetName}' (PID {targetPid}): socket tables, process and system data, event logs and a MEMORY DUMP of the process.\n\n" +
                "Process memory can contain passwords, tokens and personal data. Only share the resulting file with people you trust.\n\nContinue?",
                "Sensitive Data Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (consent != DialogResult.Yes) return;

            using var sfd = new SaveFileDialog
            {
                Filter = "ZIP Archive (*.zip)|*.zip",
                FileName = $"NetworkWatchdog_Evidence_{targetName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                Title = "Save Evidence Bundle As",
                OverwritePrompt = true
            };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            _evidenceBusy = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                SetEvidenceStatus($"Capturing evidence for {targetName} (PID {targetPid}) - this can take up to a few minutes...", Color.DimGray);
                var capture = await Task.Run(() => Ipc.CaptureEvidence(targetPid));

                if (capture.Result == Ipc.ControlResult.NotAuthorized)
                {
                    if (OfferElevation()) return;
                    await OfferLocalFallbackAsync(targetPid, targetName, sfd.FileName);
                    return;
                }
                if (capture.Result == Ipc.ControlResult.Unsupported)
                {
                    await OfferLocalFallbackAsync(targetPid, targetName, sfd.FileName, "The installed service is an older build without evidence support.");
                    return;
                }
                if (capture.Result != Ipc.ControlResult.Ok || !capture.Message.StartsWith("OK:", StringComparison.Ordinal))
                {
                    MessageBox.Show($"The service could not capture evidence:\n{capture.Message}", "Capture Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetEvidenceStatus($"Capture failed: {capture.Message}", Color.Firebrick);
                    return;
                }

                string caseId = capture.Message.Substring(3);
                SetEvidenceStatus("Evidence captured. Saving the .zip...", Color.DimGray);
                var export = await Task.Run(() => Ipc.ExportEvidence(caseId, sfd.FileName));
                if (export.Result != Ipc.ControlResult.Ok)
                {
                    MessageBox.Show($"The evidence was captured (case {caseId}) but could not be saved:\n{export.Message}\n\nYou can export it later from the Evidence Cases tab.",
                        "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SetEvidenceStatus($"Export failed: {export.Message}", Color.Firebrick);
                }
                else
                {
                    MessageBox.Show($"Evidence bundle saved:\n{sfd.FileName}\n\nSend this single .zip to the vendor's R&D team. It contains a README_FOR_RD.txt explaining every file.",
                        "Evidence Collected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetEvidenceStatus($"Saved {sfd.FileName}", Color.DimGray);
                }
                RefreshEvidenceList();
            }
            catch (Exception ex)
            {
                TrayLogger.Log("CollectEvidenceOneClickAsync", ex);
                MessageBox.Show($"Failed to collect evidence:\n{ex.Message}", "Collection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _evidenceBusy = false;
                Cursor = Cursors.Default;
            }
        }

        private async Task ExportSelectedCaseAsync()
        {
            if (_evidenceBusy) return;
            if (_gridEvidence.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select an evidence case in the list first.", "No Case Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string caseId = _gridEvidence.SelectedRows[0].Cells["CaseId"].Value?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(caseId)) return;

            using var sfd = new SaveFileDialog
            {
                Filter = "ZIP Archive (*.zip)|*.zip",
                FileName = caseId + ".zip",
                Title = "Save Evidence Case As",
                OverwritePrompt = true
            };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            _evidenceBusy = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                var export = await Task.Run(() => Ipc.ExportEvidence(caseId, sfd.FileName));
                if (export.Result == Ipc.ControlResult.NotAuthorized)
                {
                    OfferElevation();
                }
                else if (export.Result != Ipc.ControlResult.Ok)
                {
                    MessageBox.Show($"Could not export the case:\n{export.Message}", "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    MessageBox.Show($"Evidence case saved:\n{sfd.FileName}", "Evidence Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            finally
            {
                _evidenceBusy = false;
                Cursor = Cursors.Default;
            }
        }

        /// <summary>The limited, user-context collector (v3.8.0), kept ONLY as a fallback when the service cannot capture.</summary>
        private async Task OfferLocalFallbackAsync(int targetPid, string targetName, string zipPath, string? reason = null)
        {
            var answer = MessageBox.Show(
                (reason != null ? reason + "\n\n" : string.Empty) +
                "Create a LIMITED local bundle instead? It is collected in your own account: it cannot capture protected or SYSTEM processes " +
                "and it is collected AFTER the fact for automatic restarts.",
                "Limited Local Bundle", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            try
            {
                await EvidenceCollector.GenerateEvidenceBundleAsync(targetPid, targetName, zipPath);
                MessageBox.Show($"Limited evidence bundle created:\n{zipPath}", "Evidence Collected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                TrayLogger.Log("OfferLocalFallbackAsync", ex);
                MessageBox.Show($"Failed to generate the local bundle:\n{ex.Message}", "Collection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetHistoryStatus(string text, Color color)
        {
            if (IsDisposed || _lblHistoryStatus.IsDisposed) return;
            _lblHistoryStatus.ForeColor = color;
            _lblHistoryStatus.Text = text;
        }

        /// <summary>
        /// Reads on a background thread (the pipe call can block for ~1.5 s), tries the
        /// service first and falls back to the file. Never fails silently.
        /// </summary>
        private (List<string>? Lines, string Source, string? Error) ReadHistorySources()
        {
            // 1) Preferred: ask the service over IPC. It runs as SYSTEM, so it can read the
            //    Administrators/SYSTEM-only %ProgramData%\NetworkWatchdogService folder.
            var (entries, ipcError) = Ipc.QueryHistory();
            if (entries != null) return (entries, "service (IPC)", null);

            // 2) Fallback: read the file directly (works only if the tray app is elevated
            //    or the folder ACL has been relaxed). Open directly instead of File.Exists:
            //    File.Exists returns false on an access-denied folder, which looked like
            //    "no history yet" and hid the real problem.
            try
            {
                using var fs = new FileStream(_historyCsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var lines = new List<string>();
                string? line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
                return (lines, "file", null);
            }
            catch (UnauthorizedAccessException)
            {
                return (null, "", $"Access denied reading {_historyCsvPath} (the service restricts that folder to Administrators/SYSTEM). IPC fallback: {ipcError}");
            }
            catch (FileNotFoundException)
            {
                return (null, "", $"No MitigationHistory.csv exists yet - the service has not written any history. IPC: {ipcError}");
            }
            catch (DirectoryNotFoundException)
            {
                return (null, "", $"The history folder does not exist yet - the service has not written any history. IPC: {ipcError}");
            }
            catch (Exception ex)
            {
                TrayLogger.Log("ReadHistorySources(file)", ex);
                return (null, "", $"Could not read history file: {ex.Message}. IPC: {ipcError}");
            }
        }

        private async void LoadHistoricalLog()
        {
            if (_historyLoading) return;
            _historyLoading = true;
            _btnReloadHistory.Enabled = false;
            SetHistoryStatus("Loading historical archive...", Color.Gray);

            try
            {
                var result = await Task.Run(ReadHistorySources);
                if (IsDisposed) return;

                if (result.Lines == null)
                {
                    // Keep whatever is already displayed; just explain why it could not refresh.
                    SetHistoryStatus(result.Error ?? "Historical archive unavailable.", Color.Firebrick);
                    TrayLogger.Log("LoadHistoricalLog: " + result.Error);
                    return;
                }

                int histScroll = _gridRestartsHistorical.FirstDisplayedScrollingRowIndex;
                _gridRestartsHistorical.SuspendLayout();
                _gridRestartsHistorical.Rows.Clear();

                int rows = 0;
                foreach (var line in result.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',', 4);   // keep any commas inside the reason text
                    if (parts.Length >= 4)
                    {
                        _gridRestartsHistorical.Rows.Add(parts[0], parts[1], parts[2], parts[3]);
                        rows++;
                    }
                }
                _gridRestartsHistorical.ResumeLayout();

                if (histScroll >= 0 && _gridRestartsHistorical.Rows.Count > 0)
                {
                    _gridRestartsHistorical.FirstDisplayedScrollingRowIndex = Math.Min(histScroll, _gridRestartsHistorical.Rows.Count - 1);
                }

                SetHistoryStatus(
                    rows == 0
                        ? $"Connected via {result.Source}, but the archive contains no records yet."
                        : $"{rows} record(s) loaded via {result.Source} at {DateTime.Now:HH:mm:ss}.",
                    Color.DimGray);
            }
            catch (Exception ex)
            {
                TrayLogger.Log("LoadHistoricalLog", ex);
                SetHistoryStatus($"Failed to load history: {ex.Message}", Color.Firebrick);
            }
            finally
            {
                _historyLoading = false;
                if (!IsDisposed && !_btnReloadHistory.IsDisposed) _btnReloadHistory.Enabled = true;
            }
        }

        public void RefreshData(TelemetryPacket packet)
        {
            _latestPacket = packet;

            int procScroll = _gridProcesses.FirstDisplayedScrollingRowIndex;
            var procSelected = _gridProcesses.SelectedRows.Count > 0 ? _gridProcesses.SelectedRows[0].Cells["Pid"].Value?.ToString() : null;

            _gridProcesses.Rows.Clear();
            foreach (var item in packet.Processes.OrderByDescending(p => (double)p.Connections / Math.Max(1, HealthBands.EffectiveThreshold(p, packet))))
            {
                // Status text and colour come from the same threshold-relative banding as the
                // tray icon, so the grid, tooltip and icon can never disagree.
                var band = HealthBands.Classify(item.Connections, HealthBands.EffectiveThreshold(item, packet));
                int rowIndex = _gridProcesses.Rows.Add(item.ServiceName, item.Pid, item.Connections, HealthBands.Label(band));
                _gridProcesses.Rows[rowIndex].Cells["Status"].Style.BackColor = HealthBands.RowColor(band);
                _gridProcesses.Rows[rowIndex].Cells["Status"].Style.SelectionBackColor = HealthBands.RowColor(band);
                _gridProcesses.Rows[rowIndex].Cells["Status"].Style.SelectionForeColor = Color.Black;
                if (procSelected != null && item.Pid.ToString() == procSelected)
                {
                    _gridProcesses.Rows[rowIndex].Selected = true;
                }
            }

            if (procScroll >= 0 && _gridProcesses.Rows.Count > 0)
            {
                _gridProcesses.FirstDisplayedScrollingRowIndex = Math.Min(procScroll, _gridProcesses.Rows.Count - 1);
            }

            int restScroll = _gridRestartsSession.FirstDisplayedScrollingRowIndex;
            _gridRestartsSession.Rows.Clear();
            foreach (var log in packet.RecentRestarts)
            {
                var parts = log.Split(',');
                if (parts.Length >= 4)
                {
                    _gridRestartsSession.Rows.Add(parts[0], parts[1], parts[2], parts[3]);
                }
            }

            if (restScroll >= 0 && _gridRestartsSession.Rows.Count > 0)
            {
                _gridRestartsSession.FirstDisplayedScrollingRowIndex = Math.Min(restScroll, _gridRestartsSession.Rows.Count - 1);
            }

            // A new mitigation just happened: refresh the persistent archive too, so the
            // two tabs stay consistent without pressing Reload.
            string newestRestart = packet.RecentRestarts.Count > 0
                ? packet.RecentRestarts[packet.RecentRestarts.Count - 1]   // queue is oldest -> newest
                : string.Empty;
            if (!_historyTriggerBaselineSet)
            {
                _historyTriggerBaselineSet = true;
                _lastHistoryTrigger = newestRestart;
            }
            else if (newestRestart.Length > 0 && newestRestart != _lastHistoryTrigger)
            {
                _lastHistoryTrigger = newestRestart;
                LoadHistoricalLog();
            }

            _lblBuildMarker.Text = string.IsNullOrEmpty(packet.BuildMarker) ? "" : $"Service build: {packet.BuildMarker}";

            // The service is the source of truth for the capture switch (it persists it).
            if (_chkAutoCaptureEvidence.Checked != packet.EvidenceAutoCapture)
            {
                _suppressCheckEvent = true;
                _chkAutoCaptureEvidence.Checked = packet.EvidenceAutoCapture;
                _suppressCheckEvent = false;
            }

            if (_sliderThreshold.Value != packet.GlobalMaxTcpConnections && packet.GlobalMaxTcpConnections >= _sliderThreshold.Minimum && packet.GlobalMaxTcpConnections <= _sliderThreshold.Maximum)
            {
                _sliderThreshold.Value = packet.GlobalMaxTcpConnections;
                _numThreshold.Value = packet.GlobalMaxTcpConnections;
            }

            if (_listWhitelist.Items.Count != packet.Whitelist.Count)
            {
                _listWhitelist.Items.Clear();
                foreach (var w in packet.Whitelist)
                {
                    _listWhitelist.Items.Add(w);
                }
            }
        }
    }

    public class TelemetryPacket
    {
        public string Timestamp { get; set; } = string.Empty;
        public int GlobalMaxTcpConnections { get; set; }
        public List<string> Whitelist { get; set; } = new();
        public List<ProcessTelemetryItem> Processes { get; set; } = new();
        public List<string> RecentRestarts { get; set; } = new();

        // Sent by the service for a long time; this class simply lacked the property, so
        // System.Text.Json silently dropped it on every poll.
        public string BuildMarker { get; set; } = string.Empty;

        // Whether the service currently captures evidence automatically before a mitigation.
        public bool EvidenceAutoCapture { get; set; }
    }

    public class ProcessTelemetryItem
    {
        public string ServiceName { get; set; } = string.Empty;
        public int Pid { get; set; }
        public int Connections { get; set; }
        public string Status { get; set; } = string.Empty;

        // The restart threshold that applies to THIS process (per-service override or the
        // global limit). Optional: 0 / absent from older service builds, in which case the
        // tray falls back to the packet's GlobalMaxTcpConnections.
        public int Threshold { get; set; }
    }

    public class HistoryPacket
    {
        public List<string> Entries { get; set; } = new();
    }

    public class ConfigUpdateCommand
    {
        public int? NewGlobalThreshold { get; set; }
        public string? AddWhitelist { get; set; }
        public string? RemoveWhitelist { get; set; }
        public bool? SaveRequested { get; set; }
        public bool? AutoCaptureEvidence { get; set; }
    }

    public class EvidenceCaseInfo
    {
        public string CaseId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public int Pid { get; set; }
        public string Trigger { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string DumpMode { get; set; } = string.Empty;
    }

    public class EvidenceListPacket
    {
        public List<EvidenceCaseInfo> Cases { get; set; } = new();
    }

    public class CaptureEvidenceCommand
    {
        public int Pid { get; set; }
    }

    public class ExportEvidenceCommand
    {
        public string CaseId { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
    }
}