using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace NetworkWatchdog.TrayApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly Timer _timer;
        private readonly string _csvPath;

        public TrayApplicationContext()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _csvPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "src", "NetworkWatchdogService", "bin", "Release", "logs", "export", "HealthTrend_v2.csv"));

            _trayIcon = new NotifyIcon
            {
                Icon = CreateSolidIcon(Color.Gray),
                Text = "NetworkWatchdog: Initializing...",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };

            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Exit());

            _timer = new Timer { Interval = 3000 };
            _timer.Tick += (s, e) => CheckHealthStatus();
            _timer.Start();
        }

        private void CheckHealthStatus()
        {
            try
            {
                if (!File.Exists(_csvPath))
                {
                    _trayIcon.Icon = CreateSolidIcon(Color.Gray);
                    _trayIcon.Text = "NetworkWatchdog: Waiting for telemetry...";
                    return;
                }

                string lastLine = File.ReadLines(_csvPath).LastOrDefault();
                if (string.IsNullOrWhiteSpace(lastLine) || lastLine.StartsWith("Timestamp"))
                {
                    return;
                }

                var parts = lastLine.Split(',');
                if (parts.Length >= 4 && int.TryParse(parts[3], out int activeConnections))
                {
                    string serviceName = parts[1];
                    int pid = int.Parse(parts[2]);

                    if (activeConnections < 500)
                    {
                        _trayIcon.Icon = CreateSolidIcon(Color.Green);
                        _trayIcon.Text = $"{serviceName} (PID {pid})\nStatus: Healthy\nConnections: {activeConnections}";
                    }
                    else if (activeConnections < 1000)
                    {
                        _trayIcon.Icon = CreateSolidIcon(Color.Orange);
                        _trayIcon.Text = $"{serviceName} (PID {pid})\nStatus: Elevated Sockets\nConnections: {activeConnections}";
                    }
                    else
                    {
                        _trayIcon.Icon = CreateSolidIcon(Color.Red);
                        _trayIcon.Text = $"{serviceName} (PID {pid})\nStatus: LEAK THRESHOLD EXCEEDED!\nConnections: {activeConnections}";
                    }
                }
            }
            catch
            {
                // Suppress file lock contention during CSV writes
            }
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