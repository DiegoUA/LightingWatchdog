namespace NetworkWatchdogService.Models;

public class WatchdogConfig
{
    public int WatchdogIntervalSeconds { get; set; } = 30;
    public int CooldownSeconds { get; set; } = 120;
    public bool EnableQuarantine { get; set; } = true;
    public int QuarantineWindowMinutes { get; set; } = 60;
    public int QuarantineRestartLimit { get; set; } = 3;
    
    public List<MonitoredService> MonitoredServices { get; set; } = new();
}

public class MonitoredService
{
    public string ServiceName { get; set; } = string.Empty;
    public List<string> ProcessTree { get; set; } = new();
    public int MaxTcpConnections { get; set; }
    public bool EnableRestart { get; set; } = true;
}