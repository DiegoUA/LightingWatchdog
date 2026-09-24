using System.Net.Sockets;
using System.Text;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService;

public static class SyslogNotifier
{
    public static void SendAlert(SyslogConfig config, string processName, int pid, int count, int threshold, string message)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.ServerIp)) return;

        try
        {
            using var client = new UdpClient();
            // PRI 28 = Facility 3 (daemon) * 8 + Severity 4 (Warning)
            string timestamp = DateTime.UtcNow.ToString("MMM dd HH:mm:ss");
            string hostName = Environment.MachineName;
            string payload = $"<28>{timestamp} {hostName} NetworkWatchdog[{pid}]: [{processName}] {message} (Connections: {count}/{threshold})";
            byte[] bytes = Encoding.ASCII.GetBytes(payload);
            client.Send(bytes, bytes.Length, config.ServerIp, config.Port);
        }
        catch
        {
            // Suppress non-blocking network transmission drops
        }
    }
}