namespace NetworkWatchdogService.Models;

public class WatchdogConfig
{
    // Defaults below are the fallback used only if the "WatchdogConfig" section
    // is ever missing from appsettings.json entirely - the actual running values
    // come from the JSON file. Aligned to 15s/1000 to match what's validated and
    // currently deployed, so the fallback-if-config-missing case behaves the same
    // as the configured case instead of silently reverting to older values.
    public int WatchdogIntervalSeconds { get; set; } = 15;
    public int CooldownSeconds { get; set; } = 120;
    public bool EnableQuarantine { get; set; } = true;
    public int QuarantineWindowMinutes { get; set; } = 60;
    public int QuarantineRestartLimit { get; set; } = 3;
    public bool MonitorAllProcesses { get; set; } = true;
    public int GlobalMaxTcpConnections { get; set; } = 1000;
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

    // Was defaulting to 0 (the C# int default) when left unset in appsettings.json.
    // A 0 threshold means "every single connection is a breach" for that service -
    // not a safe failure mode. Defaulted to 1000 to match the global default, so an
    // accidentally-omitted value degrades to "same as everything else" rather than
    // "restart this constantly."
    public int MaxTcpConnections { get; set; } = 1000;
    public bool EnableRestart { get; set; } = true;
}

public class TelemetryPacket
{
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    public int GlobalMaxTcpConnections { get; set; }
    public List<string> Whitelist { get; set; } = new();
    public List<ProcessTelemetryItem> Processes { get; set; } = new();
    public List<string> RecentRestarts { get; set; } = new();

    // Added so the dashboard (and anyone polling the telemetry pipe directly,
    // as we did to diagnose the earlier detection bug) can confirm which build
    // is actually running without digging through the Event Log.
    public string BuildMarker { get; set; } = string.Empty;
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

    // Sent by the TrayApp's "Save Configuration" button. Was previously not
    // defined on this (service-side) class at all, so the button's request
    // silently did nothing - NewGlobalThreshold already applies live on every
    // slider change, but had no persisted-to-disk counterpart, so a service
    // restart reverted to whatever appsettings.json had.
    public bool? SaveRequested { get; set; }
}
