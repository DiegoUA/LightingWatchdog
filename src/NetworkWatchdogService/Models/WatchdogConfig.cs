namespace NetworkWatchdogService.Models;

public class WatchdogConfig
{
    public int WatchdogIntervalSeconds { get; set; } = 30;
    public int CooldownSeconds { get; set; } = 120;
    public bool EnableQuarantine { get; set; } = true;
    public int QuarantineWindowMinutes { get; set; } = 60;
    public int QuarantineRestartLimit { get; set; } = 3;
    public bool MonitorAllProcesses { get; set; } = true;
    public int GlobalMaxTcpConnections { get; set; } = 1500;
    public List<string> ProcessWhitelist { get; set; } = new() { "chrome", "firefox", "msedge", "steam", "Discord" };
    public SyslogConfig Syslog { get; set; } = new();
    public List<MonitoredService> MonitoredServices { get; set; } = new();
}

public class SyslogConfig
{
    public bool Enabled { get; set; } = false;
    public string ServerIp { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 514;
}

public class MonitoredService
{
    public string ServiceName { get; set; } = string.Empty;
    public List<string> ProcessTree { get; set; } = new();
    public int MaxTcpConnections { get; set; }
    public bool EnableRestart { get; set; } = true;
}

public class TelemetryPacket
{
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
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
    public string Status { get; set; } = "Healthy";
}

public class ConfigUpdateCommand
{
    public int? NewGlobalThreshold { get; set; }
    public string? AddWhitelist { get; set; }
    public string? RemoveWhitelist { get; set; }
}