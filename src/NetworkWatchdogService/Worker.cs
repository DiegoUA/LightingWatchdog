using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Options;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly WatchdogConfig _config;
    
    // Tracks active TCP connection counts per PID populated by ETW / network tracking
    private readonly ConcurrentDictionary<int, int> _activeConnections = new();

    public Worker(ILogger<Worker> logger, IOptions<WatchdogConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NetworkWatchdog Service started at: {time}", DateTimeOffset.Now);

        // Start ETW session on a separate background thread to avoid blocking the watchdog loop
        _ = Task.Run(() => StartEtwSession(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Watchdog evaluation cycle running...");

            EvaluateServiceHealth();

            await Task.Delay(_config.WatchdogIntervalSeconds * 1000, stoppingToken);
        }
    }

    private void StartEtwSession(CancellationToken stoppingToken)
    {
        try
        {
            using var session = new TraceEventSession("LightingWatchdogSession");
            
            // ETW Kernel traces require Administrator privileges
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            session.Source.Kernel.TcpIpConnect += data => IncrementConnection(data.ProcessID);
            session.Source.Kernel.TcpIpAccept += data => IncrementConnection(data.ProcessID);
            session.Source.Kernel.TcpIpDisconnect += data => DecrementConnection(data.ProcessID);
            session.Source.Kernel.TcpIpFail += data => DecrementConnection(data.ProcessID);

            stoppingToken.Register(() => 
            {
                _logger.LogInformation("Stopping ETW Session...");
                session.Dispose();
            });

            _logger.LogInformation("ETW Kernel Provider started. Listening for TCP events...");
            
            // This method blocks the thread and processes incoming kernel events
            session.Source.Process(); 
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogCritical("ETW access denied. The service MUST be run as Administrator to monitor kernel events.");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ETW session.");
        }
    }

    private void IncrementConnection(int pid)
    {
        _activeConnections.AddOrUpdate(pid, 1, (_, count) => count + 1);
    }

    private void DecrementConnection(int pid)
    {
        _activeConnections.AddOrUpdate(pid, 0, (_, count) => Math.Max(0, count - 1));
    }

    private void EvaluateServiceHealth()
    {
        foreach (var target in _config.MonitoredServices)
        {
            int totalConnectionsForService = 0;
            List<int> targetPids = new();

            // Find all running PIDs matching the names in this service's ProcessTree
            foreach (var processName in target.ProcessTree)
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    targetPids.Add(proc.Id);

                    // Read connection count from tracking dictionary if present
                    if (_activeConnections.TryGetValue(proc.Id, out int connections))
                    {
                        totalConnectionsForService += connections;
                    }
                }
            }

            _logger.LogInformation("[{service}] Active PIDs: {pidCount} | Total TCP Connections: {tcpCount} / {max}", 
                target.ServiceName, targetPids.Count, totalConnectionsForService, target.MaxTcpConnections);

            if (totalConnectionsForService >= target.MaxTcpConnections && target.MaxTcpConnections > 0)
            {
                _logger.LogWarning(">>> LEAK DETECTED: {service} exceeded threshold ({count}/{max}) <<<", 
                    target.ServiceName, totalConnectionsForService, target.MaxTcpConnections);
                
                // Process tree kill and recovery logic will execute here in the next phase
            }
        }
    }
}