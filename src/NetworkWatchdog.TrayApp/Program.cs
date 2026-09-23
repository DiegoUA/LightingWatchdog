using System;
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

        public TrayApplicationContext()
        {
            _csvHealthPath = @"C:\Users\maksi\OneDrive\Projects\LightingWatchdog\bin\Release\logs\export\HealthTrend_v2.csv";
            _csvRestartPath = @"C:\Users\maksi\OneDrive\Projects\LightingWatchdog\bin\Release\logs\export\RestartEvents_v2.csv";

            _trayIcon = new NotifyIcon
            {
                Icon = CreateSolidIcon(Color.Gray),
                Text = "NetworkWatchdog: Universal Monitor Initializing...",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };

            var silentItem = new ToolStripMenuItem("Silent Mode", null, (s, e) =>
            {
                _isSilentMode = !_isSilentMode;
                ((ToolStripMenuItem)s!).Checked = _isSilentMode;
                _trayIcon.ShowBalloonTip(2000, "NetworkWatchdog", 
                    _isSilentMode ? "Silent mode enabled. Pop-ups suppressed." : "Alert notifications enabled.", 
                    ToolTipIcon.Info);
            });

            _trayIcon.ContextMenuStrip.Items.Add(silentItem);
            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Exit());

            _timer = new System.Windows.Forms.Timer { Interval = 3000 };
            _timer.Tick += (s, e) => { CheckHealthStatus(); CheckForRestarts(); };
            _timer.Start();
        }

        private void CheckHealthStatus()
        {
            try
            {
                if (!File.Exists(_csvHealthPath)) return;

                string? lastLine = File.ReadLines(_csvHealthPath).LastOrDefault();
                if (string.IsNullOrWhiteSpace(lastLine) || lastLine.StartsWith("Timestamp")) return;

                var parts = lastLine.Split(',');
                if (parts.Length >= 4 && int.TryParse(parts[3], out int activeConnections))
                {
                    string serviceName = parts[1];
                    int pid = int.Parse(parts[2]);

                    if (activeConnections < 500)
                    {
                        _trayIcon.Icon = CreateSolidIcon(Color.Green);
                        _trayIcon.Text = $"{serviceName} (PID {pid})\nHealthy: {activeConnections} sockets";
                    }
                    else if (activeConnections < 1000)
                    {
                        _trayIcon.Icon = CreateSolidIcon(Color.Orange);
                        _trayIcon.Text = $"{serviceName} (PID {pid})\nElevated: {activeConnections} sockets";
                    }
                    else
                    {
                        _trayIcon.Icon = CreateSolidIcon(Color.Red);
                        _trayIcon.Text = $"{serviceName} (PID {pid})\nCRITICAL: {activeConnections} sockets";
                    }
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
            Application.Exit();
        }
    }
}