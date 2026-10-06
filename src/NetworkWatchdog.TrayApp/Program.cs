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
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
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
                try { await GitHubAutoUpdater.CheckForUpdatesAsync(); } catch { }
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
                var psi = new ProcessStartInfo { FileName = "taskkill", Arguments = $"/F /T /PID {pid}", UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                _trayIcon.ShowBalloonTip(3000, "Process Terminated", $"Terminated {name} (PID {pid}).", ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
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

            if (packet != null && packet.Processes.Count > 0)
            {
                UpdateUiFromTelemetry(packet);
            }
            else
            {
                _trayIcon.Icon = _iconGray;
                _trayIcon.Text = "NetworkWatchdog [Connecting...]";
            }
        }

        private TelemetryPacket? QueryNamedPipe()
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "NetworkWatchdogTelemetry", PipeDirection.InOut);
                pipeClient.Connect(1000);

                using var reader = new StreamReader(pipeClient, Encoding.UTF8);
                using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };

                writer.WriteLine("GET_TELEMETRY");
                string? response = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(response))
                {
                    return JsonSerializer.Deserialize<TelemetryPacket>(response);
                }
            }
            catch { }
            return null;
        }

        public static bool SendIpcConfigUpdate(ConfigUpdateCommand cmd)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "NetworkWatchdogControl", PipeDirection.InOut);
                pipeClient.Connect(300);

                using var reader = new StreamReader(pipeClient, Encoding.UTF8);
                using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };

                string json = JsonSerializer.Serialize(cmd);
                writer.WriteLine($"UPDATE_CONFIG:{json}");
                return reader.ReadLine() == "OK";
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Administrator privileges are required to modify watchdog configurations.\r\n\r\nPlease exit and restart the NetworkWatchdog Dashboard as Administrator.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateUiFromTelemetry(TelemetryPacket packet)
        {
            var elevated = packet.Processes.Where(p => p.Connections >= 500).OrderByDescending(p => p.Connections).ToList();
            
            if (_trayIcon.ContextMenuStrip == null || !_trayIcon.ContextMenuStrip.Visible)
            {
                RebuildContextMenu(elevated);
            }

            var worst = packet.Processes.OrderByDescending(p => p.Connections).First();
            Icon targetIcon = worst.Connections < 500 ? _iconGreen : (worst.Connections < 1000 ? _iconOrange : _iconRed);
            string status = worst.Connections < 500 ? "Healthy" : (worst.Connections < 1000 ? "Elevated" : "CRITICAL LEAK");

            _trayIcon.Icon = targetIcon;
            _trayIcon.Text = $"NetworkWatchdog [{status}]\nTop: {worst.ServiceName} (PID {worst.Pid}): {worst.Connections} sockets\nIPC Streaming Active";

            if (packet.RecentRestarts.Count > 0)
            {
                string latestRestart = packet.RecentRestarts[0];
                if (latestRestart != _lastRestartEvent)
                {
                    _lastRestartEvent = latestRestart;
                    if (!_isSilentMode)
                    {
                        _trayIcon.ShowBalloonTip(4000, "⚠️ Socket Leak Mitigated!", $"Rogue sockets cleared:\n{latestRestart}", ToolTipIcon.Warning);
                    }

                    if (_dashboardForm != null && !_dashboardForm.IsDisposed)
                    {
                        _ = _dashboardForm.AutoCollectTopProcessDumpAsync();
                    }
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
                            catch { }
                            finally { proc.Dispose(); }
                        }
                        File.WriteAllText(Path.Combine(tempDir, "03_ActiveProcessSnapshot.csv"), sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(tempDir, "03_ActiveProcessSnapshot.csv"), $"Error generating process snapshot: {ex.Message}");
                    }

                    // 4. Mitigation Audit Trail
                    string histPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetworkWatchdogService", "MitigationHistory.csv");
                    if (File.Exists(histPath))
                    {
                        try { File.Copy(histPath, Path.Combine(tempDir, "04_MitigationAuditTrail.csv"), true); } catch { }
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
        private readonly CheckBox _chkAutoTargetTop;

        private readonly string _historyCsvPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "MitigationHistory.csv");

        private TelemetryPacket? _latestPacket;

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

            var btnEvidence = new Button { Text = "Export Evidence Bundle (.zip)", Width = 210, Height = 32, Top = 6, Left = 190 };
            btnEvidence.Click += async (s, e) => await ExportEvidenceBundleUserInitiated();

            btnPanel.Controls.Add(btnKill);
            btnPanel.Controls.Add(btnEvidence);
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
            var btnRefreshHist = new Button { Text = "Reload Historical Log", Width = 180, Height = 32, Top = 6, Left = 10 };
            btnRefreshHist.Click += (s, e) => LoadHistoricalLog();
            pnlHistoricalControls.Controls.Add(btnRefreshHist);

            tabRestartsHistorical.Controls.Add(_gridRestartsHistorical);
            tabRestartsHistorical.Controls.Add(pnlHistoricalControls);

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

            _chkAutoTargetTop = new CheckBox
            {
                Text = "Auto-Target Top Socket Consuming Process for Dumps",
                Top = 80,
                Left = 15,
                Width = 400,
                Checked = false
            };

            var lblWhite = new Label { Text = "Excluded / Whitelisted Processes (Bypass Auto-Kill):", Top = 115, Left = 15, Width = 350 };
            _listWhitelist = new ListBox { Top = 140, Left = 15, Width = 280, Height = 170 };

            var txtNewItem = new TextBox { Top = 320, Left = 15, Width = 180 };
            var btnAdd = new Button { Text = "Add", Top = 318, Left = 205, Width = 90 };
            btnAdd.Click += (s, e) =>
            {
                string proc = txtNewItem.Text.Trim();
                if (!string.IsNullOrEmpty(proc))
                {
                    if (_sendConfigAction(new ConfigUpdateCommand { AddWhitelist = proc }))
                    {
                        _listWhitelist.Items.Add(proc);
                        txtNewItem.Clear();
                    }
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

            pnlSettings.Controls.AddRange(new Control[] { lblThresh, _sliderThreshold, _numThreshold, btnSaveConfig, _chkAutoTargetTop, lblWhite, _listWhitelist, txtNewItem, btnAdd, btnRemove });
            tabSettings.Controls.Add(pnlSettings);

            _tabs.TabPages.AddRange(new[] { tabProcesses, tabRestartsSession, tabRestartsHistorical, tabSettings });
            Controls.Add(_tabs);

            LoadHistoricalLog();
        }

        private async Task ExportEvidenceBundleUserInitiated()
        {
            int targetPid = 0;
            string targetName = "Unknown";

            if (_chkAutoTargetTop.Checked && _latestPacket != null && _latestPacket.Processes.Count > 0)
            {
                var top = _latestPacket.Processes.OrderByDescending(p => p.Connections).First();
                targetPid = top.Pid;
                targetName = top.ServiceName;
            }
            else if (_gridProcesses.SelectedRows.Count > 0)
            {
                var row = _gridProcesses.SelectedRows[0];
                targetName = row.Cells["ServiceName"].Value?.ToString() ?? "Target";
                targetPid = int.TryParse(row.Cells["Pid"].Value?.ToString(), out int p) ? p : 0;
            }

            if (targetPid <= 4 && !_chkAutoTargetTop.Checked)
            {
                MessageBox.Show("Please select an active process from the grid, or check 'Auto-Target Top Socket Consuming Process for Dumps' in Settings.", "No Process Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Filter = "ZIP Archive (*.zip)|*.zip",
                FileName = $"NetworkWatchdog_Evidence_{targetName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                Title = "Save Diagnostic Evidence Bundle As"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    await EvidenceCollector.GenerateEvidenceBundleAsync(targetPid, targetName, sfd.FileName);
                    MessageBox.Show($"Evidence bundle created successfully:\n{sfd.FileName}", "Evidence Collected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to generate evidence bundle:\n{ex.Message}", "Collection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }

        public async Task AutoCollectTopProcessDumpAsync()
        {
            if (!_chkAutoTargetTop.Checked || _latestPacket == null || _latestPacket.Processes.Count == 0) return;

            var top = _latestPacket.Processes.OrderByDescending(p => p.Connections).First();
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string zipPath = Path.Combine(appDir, $"AutoEvidence_{top.ServiceName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            try
            {
                await EvidenceCollector.GenerateEvidenceBundleAsync(top.Pid, top.ServiceName, zipPath);
            }
            catch { }
        }

        private void LoadHistoricalLog()
        {
            try
            {
                if (!File.Exists(_historyCsvPath)) return;

                int histScroll = _gridRestartsHistorical.FirstDisplayedScrollingRowIndex;
                _gridRestartsHistorical.Rows.Clear();

                var lines = File.ReadAllLines(_historyCsvPath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        _gridRestartsHistorical.Rows.Add(parts[0], parts[1], parts[2], parts[3]);
                    }
                }

                if (histScroll >= 0 && _gridRestartsHistorical.Rows.Count > 0)
                {
                    _gridRestartsHistorical.FirstDisplayedScrollingRowIndex = Math.Min(histScroll, _gridRestartsHistorical.Rows.Count - 1);
                }
            }
            catch { }
        }

        public void RefreshData(TelemetryPacket packet)
        {
            _latestPacket = packet;

            int procScroll = _gridProcesses.FirstDisplayedScrollingRowIndex;
            var procSelected = _gridProcesses.SelectedRows.Count > 0 ? _gridProcesses.SelectedRows[0].Cells["Pid"].Value?.ToString() : null;

            _gridProcesses.Rows.Clear();
            foreach (var item in packet.Processes.OrderByDescending(p => p.Connections))
            {
                int rowIndex = _gridProcesses.Rows.Add(item.ServiceName, item.Pid, item.Connections, item.Status);
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
    }

    public class ProcessTelemetryItem
    {
        public string ServiceName { get; set; } = string.Empty;
        public int Pid { get; set; }
        public int Connections { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class ConfigUpdateCommand
    {
        public int? NewGlobalThreshold { get; set; }
        public string? AddWhitelist { get; set; }
        public string? RemoveWhitelist { get; set; }
        public bool? SaveRequested { get; set; }
    }
}