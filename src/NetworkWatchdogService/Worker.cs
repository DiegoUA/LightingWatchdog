using System.Collections.Concurrent;
using System.Diagnostics;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Watchdog evaluation cycle running...");

            EvaluateServiceHealth();

            await Task.Delay(_config.WatchdogIntervalSeconds * 1000, stoppingToken);
        }
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
                
                // Process tree kill and recovery logic will execute here
            }
        }
    }
}