using System.Diagnostics;
using Microsoft.Extensions.Options;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly WatchdogConfig _config;

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
            EvaluateServiceHealth();

            await Task.Delay(_config.WatchdogIntervalSeconds * 1000, stoppingToken);
        }
    }

    private void EvaluateServiceHealth()
    {
        if (_config.MonitoredServices == null || !_config.MonitoredServices.Any())
        {
            _logger.LogWarning("No monitored services found in configuration.");
            return;
        }

        foreach (var target in _config.MonitoredServices)
        {
            int totalConnectionsForService = 0;
            List<int> targetPids = new();

            // Find all running PIDs and query the native IP Helper API for their socket counts
            foreach (var processName in target.ProcessTree)
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    targetPids.Add(proc.Id);
                    totalConnectionsForService += NativeMethods.GetTotalTcpConnectionCount(proc.Id);
                }
            }

            _logger.LogInformation("[{service}] Active PIDs: [{pids}] | Total TCP Connections: {tcpCount} / {max}", 
                target.ServiceName, string.Join(", ", targetPids), totalConnectionsForService, target.MaxTcpConnections);

            if (totalConnectionsForService >= target.MaxTcpConnections && target.MaxTcpConnections > 0 && target.EnableRestart)
            {
                _logger.LogWarning(">>> LEAK DETECTED: {service} exceeded threshold ({count}/{max}) <<<", 
                    target.ServiceName, totalConnectionsForService, target.MaxTcpConnections);
                
                ExecuteProcessTreeKill(target.ProcessTree, target.ServiceName);
            }
        }
    }

    private void ExecuteProcessTreeKill(List<string> processTree, string serviceName)
    {
        _logger.LogWarning("Initiating aggressive process tree termination for {service}...", serviceName);

        foreach (var procName in processTree)
        {
            var processes = Process.GetProcessesByName(procName);
            foreach (var proc in processes)
            {
                try
                {
                    _logger.LogInformation("Killing process {procName} (PID: {pid})...", procName, proc.Id);
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/F /T /PID {proc.Id}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    
                    using var killProc = Process.Start(startInfo);
                    killProc?.WaitForExit();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to kill process {procName} (PID: {pid})", procName, proc.Id);
                }
            }
        }

        _logger.LogInformation("Flushing TCP buffer. Waiting 15 seconds before restarting {service}...", serviceName);
        Thread.Sleep(15000); 

        try
        {
            _logger.LogInformation("Restarting Windows Service: {service}...", serviceName);
            
            var startServiceInfo = new ProcessStartInfo
            {
                FileName = "net",
                Arguments = $"start \"{serviceName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            
            using var startProc = Process.Start(startServiceInfo);
            startProc?.WaitForExit();
            
            _logger.LogInformation("{service} recovery sequence complete.", serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Windows Service: {service}", serviceName);
        }
    }
}