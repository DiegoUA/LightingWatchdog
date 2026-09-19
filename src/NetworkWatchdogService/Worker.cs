using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing;

namespace NetworkWatchdogService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private TraceEventSession? _etwSession;
        
        private readonly ConcurrentDictionary<int, int> _pidConnectionCounts = new();

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting AFD Kernel Tracing Session...");
            
            _ = Task.Run(() => StartEtwSession(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var targetProcesses = Process.GetProcessesByName("LightingService");

                foreach (var proc in targetProcesses)
                {
                    int currentConnections = _pidConnectionCounts.GetOrAdd(proc.Id, 0);
                    _logger.LogInformation($"[PID {proc.Id}] {proc.ProcessName} Winsock Handles: {currentConnections}");

                    if (currentConnections > 1000) 
                    {
                        _logger.LogWarning($"CRITICAL: Socket leak detected in {proc.ProcessName} (PID: {proc.Id})!");
                        // -> Implement aggressive taskkill /F /T and restart sequence here
                    }
                }

                await Task.Delay(5000, stoppingToken);
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

            using (_etwSession = new TraceEventSession("LightingWatchdogSession"))
            {
                stoppingToken.Register(() => _etwSession?.Dispose());

                // Use Dynamic Parser for non-standard or generic providers
                _etwSession.Source.Dynamic.All += HandleAfdEvent;
                
                // Subscribe to the Ancillary Function Driver (Winsock API Bridge)
                _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");

                _etwSession.Source.Process(); 
            }
        }

        private void HandleAfdEvent(TraceEvent data)
        {
            if (data.ProcessID <= 0) return;

            // Track handle creation/binding
            if (data.EventName.Contains("Bind") || data.EventName.Contains("Accept") || data.EventName.Contains("Connect"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (_, count) => count + 1);
            }
            // Track handle destruction
            else if (data.EventName.Contains("Close") || data.EventName.Contains("Abort") || data.EventName.Contains("Disconnect"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (_, count) => Math.Max(0, count - 1));
            }
        }
    }
}