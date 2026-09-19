using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace NetworkWatchdogService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private TraceEventSession? _etwSession; // CS8618 Fix: Marked as nullable
        
        private readonly ConcurrentDictionary<int, int> _pidConnectionCounts = new();

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting ETW Kernel Tracing Session...");
            
            _ = Task.Run(() => StartEtwSession(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var targetProcesses = Process.GetProcessesByName("LightingService");

                foreach (var proc in targetProcesses)
                {
                    int currentConnections = _pidConnectionCounts.GetOrAdd(proc.Id, 0);
                    _logger.LogInformation($"[PID {proc.Id}] {proc.ProcessName} TCP Connections: {currentConnections}");

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
                _logger.LogCritical("ETW Tracing requires Administrator privileges. Shutting down ETW thread.");
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

                _etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                // CS0123 & CS1061 Fixes: Use lambdas to route specific data types to a unified ID handler
                // IPv4 and IPv6 are both natively handled by these base events
                _etwSession.Source.Kernel.TcpIpConnect += data => UpdateConnectionCount(data.ProcessID, 1);
                _etwSession.Source.Kernel.TcpIpAccept += data => UpdateConnectionCount(data.ProcessID, 1);

                _etwSession.Source.Kernel.TcpIpDisconnect += data => UpdateConnectionCount(data.ProcessID, -1);
                _etwSession.Source.Kernel.TcpIpFail += data => UpdateConnectionCount(data.ProcessID, -1);

                _etwSession.Source.Process(); 
            }
        }

        private void UpdateConnectionCount(int processId, int adjustment)
        {
            if (processId <= 0) return; // Ignore System Idle or invalid PIDs

            if (adjustment > 0)
            {
                _pidConnectionCounts.AddOrUpdate(processId, 1, (_, count) => count + 1);
            }
            else
            {
                _pidConnectionCounts.AddOrUpdate(processId, 0, (_, count) => Math.Max(0, count - 1));
            }
        }
    }
}