using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private readonly string _csvHealthPath;
        private readonly string _csvRestartPath;
        private bool _isSilentMode = false;
        private string _lastRestartEvent = string.Empty;
        private DashboardForm? _dashboardForm;

        public TrayApplicationContext()
        {
            _csvHealthPath = @"C:\Users\maksi\OneDrive\Projects\LightingWatchdog\bin\Release\logs\export\HealthTrend_v2.csv";
            _csvRestartPath = @"C:\Users\maksi\OneDrive\Projects\LightingWatchdog\bin\Release\logs\export\RestartEvents_v2.csv";

            _trayIcon = new NotifyIcon
            {
                Icon = CreateSolidIcon(Color.Gray),
                Text = "NetworkWatchdog: Initializing...",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };

            _trayIcon.DoubleClick += (s, e) => ShowDashboard();

            RebuildContextMenu(new List<ProcessTelemetry>());

            _timer = new System.Windows.Forms.Timer { Interval = 3000 };
            _timer.Tick += (s, e) => { CheckHealthStatus(); CheckForRestarts(); };
            _timer.Start();
        }

        private void RebuildContextMenu(List<ProcessTelemetry> elevatedProcesses)
        {
            var menu = new ContextMenuStrip();

            var openDashItem = new ToolStripMenuItem("Open Dashboard", null, (s, e) => ShowDashboard())
            {
                Font = new Font(Control.DefaultFont, FontStyle.Bold)
            };
            menu.Items.Add(openDashItem);

            var silentItem = new ToolStripMenuItem("Silent Mode", null, (s, e) =>
            {
                _isSilentMode = !_isSilentMode;
                ((ToolStripMenuItem)s!).Checked = _isSilentMode;
                _trayIcon.ShowBalloonTip(2000, "NetworkWatchdog",
                    _isSilentMode ? "Silent mode enabled. Pop-ups suppressed." : "Alert notifications enabled.",
                    ToolTipIcon.Info);
            })
            {
                Checked = _isSilentMode
            };
            menu.Items.Add(silentItem);

            if (elevatedProcesses.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                var killMenu = new ToolStripMenuItem("Terminate Process Tree (Manual)");
                foreach (var proc in elevatedProcesses)
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
        }

        private void KillProcessTree(int pid, string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                _trayIcon.ShowBalloonTip(3000, "Process Terminated", $"Killed {name} (PID {pid}) and its process tree.", ToolTipIcon.Warning);
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
                _dashboardForm = new DashboardForm(_csvHealthPath, _csvRestartPath, KillProcessTree);
            }

            _dashboardForm.Show();
            _dashboardForm.BringToFront();
            _dashboardForm.WindowState = FormWindowState.Normal;
        }

        private void CheckHealthStatus()
        {
            try
            {
                if (!File.Exists(_csvHealthPath)) return;

                var lines = File.ReadLines(_csvHealthPath).TakeLast(100).ToList();
                if (lines.Count <= 1) return;

                var validLines = lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Timestamp")).ToList();
                if (validLines.Count == 0) return;

                string latestTimestamp = validLines.Last().Split(',')[0];

                var latestBatch = validLines
                    .Select(line => line.Split(','))
                    .Where(parts => parts.Length >= 4 && parts[0] == latestTimestamp)
                    .Select(parts => new ProcessTelemetry
                    {
                        Timestamp = parts[0],
                        ServiceName = parts[1],
                        Pid = int.TryParse(parts[2], out int p) ? p : 0,
                        Connections = int.TryParse(parts[3], out int c) ? c : 0
                    })
                    .ToList();

                if (latestBatch.Count == 0) return;

                var elevated = latestBatch.Where(x => x.Connections >= 500).OrderByDescending(x => x.Connections).ToList();
                RebuildContextMenu(elevated);

                var worstProcess = latestBatch.OrderByDescending(x => x.Connections).First();

                Color statusColor = worstProcess.Connections < 500 ? Color.Green : (worstProcess.Connections < 1000 ? Color.Orange : Color.Red);
                string statusLabel = worstProcess.Connections < 500 ? "Healthy" : (worstProcess.Connections < 1000 ? "Elevated Sockets" : "CRITICAL LEAK");

                _trayIcon.Icon = CreateSolidIcon(statusColor);
                string topSummary = $"{worstProcess.ServiceName} (PID {worstProcess.Pid}): {worstProcess.Connections} sockets";
                _trayIcon.Text = $"NetworkWatchdog [{statusLabel}]\nTop: {topSummary}\nTracked: {latestBatch.Count}";

                if (_dashboardForm != null && !_dashboardForm.IsDisposed && _dashboardForm.Visible)
                {
                    _dashboardForm.RefreshData(latestBatch);
                }
            }
            catch { }
        }

        private void CheckForRestarts()
        {
            try
            {
                if (!File.Exists(_csvRestartPath)) return;

                string? lastLine = File.ReadLines(_csvRestartPath).LastOrDefault();
                if (string.IsNullOrWhiteSpace(lastLine) || lastLine.StartsWith("Timestamp")) return;

                if (lastLine != _lastRestartEvent)
                {
                    _lastRestartEvent = lastLine;
                    if (!_isSilentMode)
                    {
                        _trayIcon.ShowBalloonTip(4000, "⚠️ Socket Leak Mitigated!",
                            $"Rogue process breach detected and neutralized:\n{lastLine}",
                            ToolTipIcon.Warning);
                    }
                }
            }
            catch { }
        }

        private Icon CreateSolidIcon(Color color)
        {
            using var bitmap = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(bitmap);
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 0, 0, 16, 16);
            return Icon.FromHandle(bitmap.GetHicon());
        }

        private void Exit()
        {
            _timer.Stop();
            _trayIcon.Visible = false;
            _dashboardForm?.Close();
            Application.Exit();
        }
    }

    public class ProcessTelemetry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public int Pid { get; set; }
        public int Connections { get; set; }
    }

    public class DashboardForm : Form
    {
        private readonly string _csvHealthPath;
        private readonly string _csvRestartPath;
        private readonly Action<int, string> _killAction;
        private readonly TabControl _tabs;
        private readonly DataGridView _gridProcesses;
        private readonly DataGridView _gridRestarts;

        public DashboardForm(string csvHealthPath, string csvRestartPath, Action<int, string> killAction)
        {
            _csvHealthPath = csvHealthPath;
            _csvRestartPath = csvRestartPath;
            _killAction = killAction;

            Text = "NetworkWatchdog Dashboard";
            Size = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;

            _tabs = new TabControl { Dock = DockStyle.Fill };

            var tabProcesses = new TabPage("Live Monitored Processes");
            _gridProcesses = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false
            };
            _gridProcesses.Columns.Add("ServiceName", "Process / Service");
            _gridProcesses.Columns.Add("Pid", "PID");
            _gridProcesses.Columns.Add("Connections", "Sockets / Connections");
            _gridProcesses.Columns.Add("Status", "Status");

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45 };
            var btnKill = new Button { Text = "Terminate Process Tree", Width = 180, Height = 32, Top = 6, Left = 10 };
            btnKill.Click += (s, e) =>
            {
                if (_gridProcesses.SelectedRows.Count > 0)
                {
                    var row = _gridProcesses.SelectedRows[0];
                    string name = row.Cells["ServiceName"].Value?.ToString() ?? string.Empty;
                    int pid = int.TryParse(row.Cells["Pid"].Value?.ToString(), out int p) ? p : 0;
                    if (pid > 0) _killAction(pid, name);
                }
            };
            btnPanel.Controls.Add(btnKill);

            tabProcesses.Controls.Add(_gridProcesses);
            tabProcesses.Controls.Add(btnPanel);

            var tabRestarts = new TabPage("Historical Mitigations");
            _gridRestarts = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true
            };
            _gridRestarts.Columns.Add("Timestamp", "Timestamp");
            _gridRestarts.Columns.Add("ServiceName", "Service / Process");
            _gridRestarts.Columns.Add("Pid", "PID");
            _gridRestarts.Columns.Add("Reason", "Reason");

            tabRestarts.Controls.Add(_gridRestarts);

            _tabs.TabPages.Add(tabProcesses);
            _tabs.TabPages.Add(tabRestarts);
            Controls.Add(_tabs);

            LoadHistoricalRestarts();
        }

        public void RefreshData(List<ProcessTelemetry> batch)
        {
            _gridProcesses.Rows.Clear();
            foreach (var item in batch.OrderByDescending(x => x.Connections))
            {
                string status = item.Connections < 500 ? "Healthy" : (item.Connections < 1000 ? "Elevated" : "LEAK");
                _gridProcesses.Rows.Add(item.ServiceName, item.Pid, item.Connections, status);
            }
            LoadHistoricalRestarts();
        }

        private void LoadHistoricalRestarts()
        {
            try
            {
                if (!File.Exists(_csvRestartPath)) return;
                _gridRestarts.Rows.Clear();
                var lines = File.ReadLines(_csvRestartPath).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(50);
                foreach (var line in lines)
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        _gridRestarts.Rows.Add(parts[0], parts[1], parts[2], parts[3]);
                    }
                }
            }
            catch { }
        }
    }
}