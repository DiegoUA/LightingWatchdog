using Microsoft.Extensions.Options;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService; // Ensure this matches Program.cs

public class Worker : BackgroundService // Ensure it is public and inherits BackgroundService
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
            _logger.LogInformation("Watchdog loop running. Interval: {seconds}s", _config.WatchdogIntervalSeconds);
            
            // ETW socket monitoring logic will go here
            
            await Task.Delay(_config.WatchdogIntervalSeconds * 1000, stoppingToken);
        }
    }
}