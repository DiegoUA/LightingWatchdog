using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogFatalError(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogFatalError(e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }

        public static void LogFatalError(Exception? ex, string source = "Unknown")
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray_crash.log");
                File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] [{source}] FATAL: {ex?.ToString()}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private const int GR_GDIOBJECTS = 0;
        private const int GR_USEROBJECTS = 1;

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly NotifyIcon _trayIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly string _csvHealthPath;
        private bool _isSilentMode = false;
        private string _lastRestartEvent = string.Empty;
        private DashboardForm? _dashboardForm;

        // Cached GDI resources to prevent handle exhaustion
        private readonly Icon _iconGreen;
        private readonly Icon _iconOrange;
        private readonly Icon _iconRed;
        private readonly Icon _iconGray;
        private readonly Font _boldFont;

        public TrayApplicationContext()
        {
            _csvHealthPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "bin", "Release", "logs", "export", "HealthTrend_v2.csv");

            // Generate GDI icons exactly once
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
            oldMenu?.Dispose();
        }

        private void CheckHandleLimits()
        {
            try
            {
                IntPtr hProcess = Process.GetCurrentProcess().Handle;
                uint gdiHandles = GetGuiResources(hProcess, GR_GDIOBJECTS);
                uint userHandles = GetGuiResources(hProcess, GR_USEROBJECTS);

                if (gdiHandles > 200 || userHandles > 200)
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray_crash.log");
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] [WARN] Elevated Win32 Handles: GDI={gdiHandles}, USER={userHandles}. Threshold=200.{Environment.NewLine}");
                }
            }
            catch { }
        }

        private void KillProcessTree(int pid, string name)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("/F");
                psi.ArgumentList.Add("/T");
                psi.ArgumentList.Add("/PID");
                psi.ArgumentList.Add(pid.ToString());

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
            CheckHandleLimits();

            TelemetryPacket? packet = QueryNamedPipe();

            if (packet != null && packet.Processes.Count > 0)
            {
                UpdateUiFromTelemetry(packet);
            }
            else
            {
                FallbackFilePolling();
            }
        }

        private TelemetryPacket? QueryNamedPipe()
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "NetworkWatchdogPipe", PipeDirection.InOut);
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
                using var pipeClient = new NamedPipeClientStream(".", "NetworkWatchdogPipe", PipeDirection.InOut);
                pipeClient.Connect(500);

                using var reader = new StreamReader(pipeClient, Encoding.UTF8);
                using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };

                string json = JsonSerializer.Serialize(cmd);
                writer.WriteLine($"UPDATE_CONFIG:{json}");
                return reader.ReadLine() == "OK";
            }
            catch
            {
                return false;
            }
        }

        private void UpdateUiFromTelemetry(TelemetryPacket packet)
        {
            var elevated = packet.Processes.Where(p => p.Connections >= 500).OrderByDescending(p => p.Connections).ToList();
            RebuildContextMenu(elevated);

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

        private void FallbackFilePolling()
        {
            try
            {
                if (!File.Exists(_csvHealthPath)) return;
                var lines = File.ReadLines(_csvHealthPath).TakeLast(50).ToList();
                if (lines.Count <= 1) return;

                var valid = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Timestamp")).ToList();
                if (valid.Count == 0) return;

                string lastLine = valid.Last();
                var parts = lastLine.Split(',');
                if (parts.Length >= 4 && int.TryParse(parts[3], out int conn))
                {
                    _trayIcon.Icon = conn < 500 ? _iconGreen : (conn < 1000 ? _iconOrange : _iconRed);
                    _trayIcon.Text = $"NetworkWatchdog [File Fallback]\n{parts[1]} (PID {parts[2]}): {conn} sockets";
                }
            }
            catch { }
        }

        private void Exit()
        {
            _timer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _iconGreen.Dispose();
            _iconOrange.Dispose();
            _iconRed.Dispose();
            _iconGray.Dispose();
            _boldFont.Dispose();
            _dashboardForm?.Close();
            Application.Exit();
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
        private readonly Label _lblSliderValue;

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

            var lblThresh = new Label { Text = "Global Max TCP Connections Threshold (200 - 10000):", Top = 15, Left = 15, Width = 350 };
            _sliderThreshold = new TrackBar { Top = 40, Left = 15, Width = 400, Minimum = 200, Maximum = 10000, TickFrequency = 500, SmallChange = 50, LargeChange = 250 };
            _lblSliderValue = new Label { Text = "1000", Top = 45, Left = 425, Width = 60 };
            _sliderThreshold.ValueChanged += (s, e) =>
            {
                int val = Math.Clamp(_sliderThreshold.Value, 200, 10000);
                _lblSliderValue.Text = val.ToString();
                _sendConfigAction(new ConfigUpdateCommand { NewGlobalThreshold = val });
            };

            var lblWhite = new Label { Text = "Excluded / Whitelisted Processes (Bypass Auto-Kill):", Top = 90, Left = 15, Width = 350 };
            _listWhitelist = new ListBox { Top = 115, Left = 15, Width = 280, Height = 180 };

            var txtNewItem = new TextBox { Top = 305, Left = 15, Width = 180 };
            var btnAdd = new Button { Text = "Add", Top = 303, Left = 205, Width = 90 };
            btnAdd.Click += (s, e) =>
            {
                string proc = txtNewItem.Text.Trim();
                if (!string.IsNullOrEmpty(proc) && System.Text.RegularExpressions.Regex.IsMatch(proc, @"^[a-zA-Z0-9_\-\.]+$"))
                {
                    _sendConfigAction(new ConfigUpdateCommand { AddWhitelist = proc });
                    _listWhitelist.Items.Add(proc);
                    txtNewItem.Clear();
                }
            };

            var btnRemove = new Button { Text = "Remove Selected", Top = 335, Left = 15, Width = 150 };
            btnRemove.Click += (s, e) =>
            {
                if (_listWhitelist.SelectedItem != null)
                {
                    string selected = _listWhitelist.SelectedItem.ToString() ?? "";
                    _sendConfigAction(new ConfigUpdateCommand { RemoveWhitelist = selected });
                    _listWhitelist.Items.Remove(selected);
                }
            };

            pnlSettings.Controls.AddRange(new Control[] { lblThresh, _sliderThreshold, _lblSliderValue, lblWhite, _listWhitelist, txtNewItem, btnAdd, btnRemove });
            tabSettings.Controls.Add(pnlSettings);

            _tabs.TabPages.AddRange(new[] { tabProcesses, tabRestarts, tabSettings });
            Controls.Add(_tabs);
        }

        public void RefreshData(TelemetryPacket packet)
        {
            _gridProcesses.Rows.Clear();
            foreach (var item in packet.Processes.OrderByDescending(p => p.Connections))
            {
                _gridProcesses.Rows.Add(item.ServiceName, item.Pid, item.Connections, item.Status);
            }

            _gridRestarts.Rows.Clear();
            foreach (var log in packet.RecentRestarts)
            {
                var parts = log.Split(',');
                if (parts.Length >= 4)
                {
                    _gridRestarts.Rows.Add(parts[0], parts[1], parts[2], parts[3]);
                }
            }

            if (_sliderThreshold.Value != packet.GlobalMaxTcpConnections && packet.GlobalMaxTcpConnections >= _sliderThreshold.Minimum && packet.GlobalMaxTcpConnections <= _sliderThreshold.Maximum)
            {
                _sliderThreshold.Value = packet.GlobalMaxTcpConnections;
                _lblSliderValue.Text = packet.GlobalMaxTcpConnections.ToString();
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
    }
}