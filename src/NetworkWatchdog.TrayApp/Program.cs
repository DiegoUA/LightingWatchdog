using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
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

            // Safe startup check delay without raw thread invocations
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
                // Delay disposal to ensure message pump finishes processing current context menu operations safely
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
        private const string CurrentVersion = "v3.7.0"; 

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
                string latestVersion = root.GetProperty("tag_name").GetString() ?? "";
                
                if (string.Compare(latestVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase) > 0)
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
                            $"A new version of NetworkWatchdog ({latestVersion}) is available on GitHub.\n\n" +
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
                    MessageBox.Show("You are running the latest version of NetworkWatchdog.", "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    public class DashboardForm : Form
    {
        private readonly Action<int, string> _killAction;
        private readonly Func<ConfigUpdateCommand, bool> _sendConfigAction;
        private readonly TabControl _tabs;
        private readonly DataGridView _gridProcesses;
        private readonly DataGridView _gridRestarts;
        private readonly ListBox _listWhitelist;
        private readonly TrackBar _sliderThreshold;
        private readonly NumericUpDown _numThreshold;

        public DashboardForm(Action<int, string> killAction, Func<ConfigUpdateCommand, bool> sendConfigAction)
        {
            _killAction = killAction;
            _sendConfigAction = sendConfigAction;

            Text = "NetworkWatchdog Telemetry & Control Dashboard";
            Size = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;

            _tabs = new TabControl { Dock = DockStyle.Fill };

            var tabProcesses = new TabPage("Live Processes (IPC)");
            _gridProcesses = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
            _gridProcesses.Columns.Add("ServiceName", "Process");
            _gridProcesses.Columns.Add("Pid", "PID");
            _gridProcesses.Columns.Add("Connections", "Sockets / Handles");
            _gridProcesses.Columns.Add("Status", "Status");

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
            var btnKill = new Button { Text = "Terminate Process Tree", Width = 180, Height = 32, Top = 6, Left = 10 };
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
            btnPanel.Controls.Add(btnKill);
            tabProcesses.Controls.Add(_gridProcesses);
            tabProcesses.Controls.Add(btnPanel);

            var tabRestarts = new TabPage("Mitigation Logs");
            _gridRestarts = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ReadOnly = true };
            _gridRestarts.Columns.Add("Timestamp", "Timestamp");
            _gridRestarts.Columns.Add("ServiceName", "Process");
            _gridRestarts.Columns.Add("Pid", "PID");
            _gridRestarts.Columns.Add("Reason", "Reason");
            tabRestarts.Controls.Add(_gridRestarts);

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

            var lblWhite = new Label { Text = "Excluded / Whitelisted Processes (Bypass Auto-Kill):", Top = 90, Left = 15, Width = 350 };
            _listWhitelist = new ListBox { Top = 115, Left = 15, Width = 280, Height = 180 };

            var txtNewItem = new TextBox { Top = 305, Left = 15, Width = 180 };
            var btnAdd = new Button { Text = "Add", Top = 303, Left = 205, Width = 90 };
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

            var btnRemove = new Button { Text = "Remove Selected", Top = 335, Left = 15, Width = 150 };
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

            pnlSettings.Controls.AddRange(new Control[] { lblThresh, _sliderThreshold, _numThreshold, btnSaveConfig, lblWhite, _listWhitelist, txtNewItem, btnAdd, btnRemove });
            tabSettings.Controls.Add(pnlSettings);

            _tabs.TabPages.AddRange(new[] { tabProcesses, tabRestarts, tabSettings });
            Controls.Add(_tabs);
        }

        public void RefreshData(TelemetryPacket packet)
        {
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

            int restScroll = _gridRestarts.FirstDisplayedScrollingRowIndex;
            _gridRestarts.Rows.Clear();
            foreach (var log in packet.RecentRestarts)
            {
                var parts = log.Split(',');
                if (parts.Length >= 4)
                {
                    _gridRestarts.Rows.Add(parts[0], parts[1], parts[2], parts[3]);
                }
            }

            if (restScroll >= 0 && _gridRestarts.Rows.Count > 0)
            {
                _gridRestarts.FirstDisplayedScrollingRowIndex = Math.Min(restScroll, _gridRestarts.Rows.Count - 1);
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