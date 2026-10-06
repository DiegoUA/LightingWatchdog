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
    public EvidenceConfig Evidence { get; set; } = new();
    public List<MonitoredService> MonitoredServices { get; set; } = new();
}

public class SyslogConfig
{
    public bool Enabled { get; set; } = false;
    public string ServerIp { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 514;
}

/// <summary>
/// Pre-mitigation R&amp;D evidence capture. The service (running as SYSTEM) captures the
/// evidence BEFORE it terminates a leaking process - afterwards the process, its sockets and
/// its memory no longer exist, so there would be nothing left to investigate.
/// </summary>
public class EvidenceConfig
{
    /// <summary>Master switch for all evidence capture (automatic and manual).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Capture automatically right before a leaking process is restarted.
    /// OFF BY DEFAULT: automatic captures use disk space (a Full dump can be hundreds of MB), so the
    /// user opts in from the dashboard or the tray menu. Manual one-click capture works regardless.</summary>
    public bool CaptureBeforeMitigation { get; set; } = false;

    /// <summary>"Full" = full process memory + handle table (what vendor R&amp;D teams ask for),
    /// "Standard" = handle table + thread info + referenced memory (much smaller),
    /// "None" = everything except a memory dump.</summary>
    public string DumpMode { get; set; } = "Full";

    /// <summary>How many evidence cases are kept on disk; the oldest are deleted first.</summary>
    public int MaxCases { get; set; } = 5;

    /// <summary>Minimum time between AUTOMATIC captures for the same process name, so a
    /// process that leaks every ~35 minutes does not fill the disk with dumps. Manual
    /// captures are never rate-limited.</summary>
    public int MinIntervalMinutes { get; set; } = 360;

    /// <summary>Upper bound on how long evidence capture may delay a mitigation. When it is
    /// exceeded the process is restarted anyway.</summary>
    public int CaptureTimeoutSeconds { get; set; } = 90;
}

public class MonitoredService
{
    public string ServiceName { get; set; } = string.Empty;

    // Processes that share this entry's threshold / restart policy AND are restarted together
    // with the leaking process (the legacy v2.x watchdog killed the whole ProcessTree; this
    // restores that behaviour). Other blockers are discovered dynamically (dependent services,
    // Restart Manager file locks) - see Worker.DiscoverRelatedProcesses.
    public List<string> ProcessTree { get; set; } = new();

    // OPTIONAL. When omitted (null) this service uses the live global threshold, i.e. whatever the
    // user has selected with the dashboard slider, so one slider drives everything. Set a number
    // only to PIN a service to its own limit regardless of the slider.
    // (History: the C# default used to be 0, meaning "every connection is a breach"; it was then
    // 1000, which silently shadowed the slider for any configured service. null is the safe default.)
    public int? MaxTcpConnections { get; set; }
    public bool EnableRestart { get; set; } = true;
}

public class TelemetryPacket
{
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    public int GlobalMaxTcpConnections { get; set; }
    public List<string> Whitelist { get; set; } = new();
    public List<ProcessTelemetryItem> Processes { get; set; } = new();
    public List<string> RecentRestarts { get; set; } = new();

    // Lets the dashboard (and anyone polling the telemetry pipe directly) confirm which
    // build is actually running without digging through the Event Log.
    public string BuildMarker { get; set; } = string.Empty;

    // Current state of the "capture evidence before mitigation" switch.
    public bool EvidenceAutoCapture { get; set; }
}

public class ProcessTelemetryItem
{
    public string ServiceName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public int Connections { get; set; }
    public string Status { get; set; } = "Healthy";

    // The restart threshold that applies to this process (per-service override or the
    // global limit). Lets clients colour-code against the REAL limit. 0 from older senders.
    public int Threshold { get; set; }
}

public class HistoryPacket
{
    public List<string> Entries { get; set; } = new();
}

public class EvidenceCaseInfo
{
    public string CaseId { get; set; } = string.Empty;
    public string TimestampUtc { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DumpMode { get; set; } = string.Empty;
}

public class EvidenceListPacket
{
    public List<EvidenceCaseInfo> Cases { get; set; } = new();
}

public class CaptureEvidenceCommand
{
    public int Pid { get; set; }
}

public class ExportEvidenceCommand
{
    public string CaseId { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
}

public class ConfigUpdateCommand
{
    public int? NewGlobalThreshold { get; set; }
    public string? AddWhitelist { get; set; }
    public string? RemoveWhitelist { get; set; }

    // Sent by the TrayApp's "Save Configuration" button: persists the current threshold and
    // evidence switch (runtime-overrides.json) and re-saves the whitelist.
    public bool? SaveRequested { get; set; }

    // Tray checkbox: capture evidence automatically before a leaking process is restarted.
    public bool? AutoCaptureEvidence { get; set; }
}
