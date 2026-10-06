using System.Globalization;
using System.Net.Sockets;
using System.Text;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService;

public enum SyslogSeverity
{
    Emergency = 0, Alert = 1, Critical = 2, Error = 3, Warning = 4, Notice = 5, Informational = 6, Debug = 7
}

/// <summary>
/// The single RFC 3164 / UDP syslog implementation. Previously this class was never called
/// (Worker.cs carried its own inline copy), so there were two diverging implementations;
/// Worker now delegates here. Fire-and-forget by design: a syslog server that is down or
/// unreachable must never block, slow down or break detection/restart logic.
/// </summary>
public static class SyslogNotifier
{
    // Facility 16 = local0, the conventional choice for an application-specific daemon.
    private const int Facility = 16;

    private static readonly object Gate = new();
    private static UdpClient? _client;
    private static string _clientKey = string.Empty;

    /// <summary>Sends one line. Never throws. No-op when Syslog is disabled.</summary>
    public static void Send(SyslogConfig config, SyslogSeverity severity, string message, string tag = "NetworkWatchdogService")
    {
        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(config.ServerIp)) return;

        try
        {
            // RFC 3164 timestamps are local time, "Mmm dd hh:mm:ss", with the day SPACE-padded
            // ("Oct  4"), not zero-padded as the old DateTime.UtcNow.ToString("MMM dd ...") did.
            DateTime now = DateTime.Now;
            string timestamp = now.ToString("MMM", CultureInfo.InvariantCulture) + " " +
                               now.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2, ' ') + " " +
                               now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            int priority = Facility * 8 + (int)severity;
            string line = $"<{priority}>{timestamp} {Environment.MachineName} {tag}: {message}";
            if (line.Length > 1000) line = line.Substring(0, 1000);          // RFC 3164: 1024 bytes max

            byte[] bytes = Encoding.ASCII.GetBytes(line);
            UdpClient client = GetClient(config);
            client.Send(bytes, bytes.Length);
        }
        catch
        {
            // Suppress non-blocking network transmission drops.
            ResetClient();
        }
    }

    /// <summary>Backward-compatible overload of the original alert API (Warning severity).</summary>
    public static void SendAlert(SyslogConfig config, string processName, int pid, int count, int threshold, string message)
        => Send(config, SyslogSeverity.Warning,
                $"[{processName}] {message} (PID {pid}, Connections: {count}/{threshold})");

    private static UdpClient GetClient(SyslogConfig config)
    {
        string key = $"{config.ServerIp}:{config.Port}";
        lock (Gate)
        {
            if (_client != null && _clientKey == key) return _client;
            _client?.Dispose();
            var client = new UdpClient();
            client.Connect(config.ServerIp, config.Port);
            _client = client;
            _clientKey = key;
            return client;
        }
    }

    private static void ResetClient()
    {
        lock (Gate)
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
            _clientKey = string.Empty;
        }
    }
}
