using System.Collections.Concurrent;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Options;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly WatchdogConfig _config;
    private TraceEventSession? _etwSession;

    private readonly ConcurrentDictionary<int, int> _pidConnectionCounts = new();
    private readonly ConcurrentDictionary<int, bool> _baselineInitialized = new();
    private readonly Dictionary<string, DateTime> _lastRestartTimes = new();

    public Worker(ILogger<Worker> logger, IOptions<WatchdogConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting AFD Kernel Tracing Session...");
        StartEtwSession(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. Evaluate Explicitly Monitored Services
            foreach (var target in _config.MonitoredServices)
            {
                int totalConnectionsForService = 0;
                List<int> targetPids = new();

                foreach (var processName in target.ProcessTree)
                {
                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        targetPids.Add(proc.Id);

                        if (!_baselineInitialized.ContainsKey(proc.Id))
                        {
                            int baseline = GetBaselineSocketCount(proc.Id);
                            _pidConnectionCounts.AddOrUpdate(proc.Id, baseline, (_, current) => current + baseline);
                            _baselineInitialized.TryAdd(proc.Id, true);
                            _logger.LogInformation("[PID {pid}] Baseline initialized with {baseline} pre-existing handles for {process}.", proc.Id, baseline, processName);
                        }

                        int currentConnections = _pidConnectionCounts.GetOrAdd(proc.Id, 0);
                        totalConnectionsForService += currentConnections;

                        TelemetryExporter.RecordHealthSnapshot(processName, proc.Id, currentConnections);
                    }
                }

                _logger.LogInformation("[{service}] Total TCP Connections: {tcpCount} / {max}", 
                    target.ServiceName, totalConnectionsForService, target.MaxTcpConnections);

                if (totalConnectionsForService >= target.MaxTcpConnections && target.MaxTcpConnections > 0)
                {
                    _logger.LogWarning(">>> LEAK DETECTED: {service} exceeded threshold ({count}/{max}) <<<", 
                        target.ServiceName, totalConnectionsForService, target.MaxTcpConnections);

                    HandleServiceRecovery(target, targetPids);
                }
            }

            // 2. Universal Process Monitoring (If Enabled)
            if (_config.MonitorAllProcesses)
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        int pid = proc.Id;
                        if (pid <= 4) continue; // Skip System idle/kernel

                        string procName = proc.ProcessName;
                        bool isExplicitlyMonitored = _config.MonitoredServices.Any(s => s.ProcessTree.Contains(procName, StringComparer.OrdinalIgnoreCase));

                        if (!isExplicitlyMonitored)
                        {
                            if (!_baselineInitialized.ContainsKey(pid))
                            {
                                int baseline = GetBaselineSocketCount(pid);
                                _pidConnectionCounts.AddOrUpdate(pid, baseline, (_, current) => current + baseline);
                                _baselineInitialized.TryAdd(pid, true);
                            }

                            int currentConnections = _pidConnectionCounts.GetOrAdd(pid, 0);
                            TelemetryExporter.RecordHealthSnapshot(procName, pid, currentConnections);

                            if (currentConnections >= _config.GlobalMaxTcpConnections && _config.GlobalMaxTcpConnections > 0)
                            {
                                _logger.LogWarning(">>> GLOBAL LEAK DETECTED: Process {proc} (PID {pid}) exceeded global threshold ({count}/{max}) <<<",
                                    procName, pid, currentConnections, _config.GlobalMaxTcpConnections);

                                ExecuteCommand("taskkill", $"/F /T /PID {pid}");
                                TelemetryExporter.RecordRestart(procName, pid, $"Exceeded global {_config.GlobalMaxTcpConnections} socket threshold");
                                
                                _pidConnectionCounts.TryRemove(pid, out _);
                                _baselineInitialized.TryRemove(pid, out _);
                            }
                        }
                    }
                    catch
                    {
                        // Handle race conditions where process exits during iteration
                    }
                }
            }

            await Task.Delay(_config.WatchdogIntervalSeconds * 1000, stoppingToken);
        }
    }

    private int GetBaselineSocketCount(int processId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"(Get-NetTCPConnection -OwningProcess {processId} -ErrorAction SilentlyContinue).Count\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            string output = proc.StandardOutput.ReadToEnd().Trim();
            if (int.TryParse(output, out int count))
            {
                return count;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get baseline socket count for PID {pid}", processId);
            return 0;
        }
    }

    private void StartEtwSession(CancellationToken stoppingToken)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            _logger.LogCritical("ETW Tracing requires Administrator privileges.");
            return;
        }

        if (TraceEventSession.GetActiveSessionNames().Contains("LightingWatchdogSession"))
        {
            using var oldSession = new TraceEventSession("LightingWatchdogSession");
            oldSession.Stop();
        }

        _etwSession = new TraceEventSession("LightingWatchdogSession");

        stoppingToken.Register(() => 
        {
            _etwSession?.Stop();
            _etwSession?.Dispose();
        });

        _etwSession.Source.Dynamic.All += HandleAfdEvent;
        _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");

        Task.Run(() =>
        {
            try
            {
                _etwSession.Source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ETW session processing encountered an error.");
            }
        }, stoppingToken);
    }

    private void HandleAfdEvent(TraceEvent data)
    {
        if (data.ProcessID == 0) return;

        // Automatically track PIDs if universal monitoring is enabled, or if already initialized
        if (!_pidConnectionCounts.ContainsKey(data.ProcessID) && _config.MonitorAllProcesses)
        {
            _pidConnectionCounts.TryAdd(data.ProcessID, 0);
            _baselineInitialized.TryAdd(data.ProcessID, true);
        }

        if (!_pidConnectionCounts.ContainsKey(data.ProcessID)) return;

        if (data.EventName.Contains("AfdConnect") || data.EventName.Contains("AfdAccept"))
        {
            _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (pid, count) => count + 1);
        }
        else if (data.EventName.Contains("AfdClose"))
        {
            _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (pid, count) => Math.Max(0, count - 1));
        }
    }

    private void HandleServiceRecovery(MonitoredService target, List<int> pids)
    {
        if (!target.EnableRestart) return;

        if (_lastRestartTimes.TryGetValue(target.ServiceName, out var lastRestart))
        {
            if ((DateTime.UtcNow - lastRestart).TotalMinutes < _config.CooldownSeconds)
            {
                _logger.LogInformation("Service {service} is in cooldown period. Skipping restart.", target.ServiceName);
                return;
            }
        }

        TelemetryExporter.RecordRestart(target.ServiceName, pids.FirstOrDefault(), $"Exceeded {target.MaxTcpConnections} socket threshold");

        _logger.LogWarning("Initiating recovery sequence for {service}...", target.ServiceName);

        foreach (var pid in pids)
        {
            ExecuteCommand("taskkill", $"/F /T /PID {pid}");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var sc = new ServiceController(target.ServiceName);
                if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.Paused)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
                sc.Start();
                _logger.LogInformation("Successfully restarted Windows Service: {service}", target.ServiceName);
            }
            catch (Exception)
            {
                _logger.LogInformation("Windows Service control skipped or failed for {service}. Relying on process tree termination.", target.ServiceName);
            }
        }
        else
        {
            _logger.LogInformation("Windows Service control is unavailable on this platform for {service}. Relying on process tree termination.", target.ServiceName);
        }

        _lastRestartTimes[target.ServiceName] = DateTime.UtcNow;

        foreach (var pid in pids)
        {
            _pidConnectionCounts.TryRemove(pid, out _);
            _baselineInitialized.TryRemove(pid, out _);
        }
    }

    private void ExecuteCommand(string filename, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command failed: {filename} {arguments}", filename, arguments);
        }
    }
}