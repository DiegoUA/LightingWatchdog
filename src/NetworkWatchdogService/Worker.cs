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
        private readonly ConcurrentDictionary<int, bool> _baselineInitialized = new();

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
                    // 1. One-time baseline initialization
                    if (!_baselineInitialized.ContainsKey(proc.Id))
                    {
                        int baseline = GetBaselineSocketCount(proc.Id);
                        
                        // Add baseline to any events ETW might have already caught
                        _pidConnectionCounts.AddOrUpdate(proc.Id, baseline, (_, current) => current + baseline);
                        _baselineInitialized.TryAdd(proc.Id, true);
                        
                        _logger.LogInformation($"[PID {proc.Id}] Baseline initialized with {baseline} pre-existing handles.");
                    }

                    // 2. Read live count
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

        private int GetBaselineSocketCount(int processId)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var proc = Process.Start(psi);
                if (proc == null) return 0;

                int count = 0;
                string? line;
                string pidSuffix = $" {processId}"; // netstat outputs PID at the end of the line

                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.EndsWith(pidSuffix))
                    {
                        count++;
                    }
                }
                return count;
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

            using (_etwSession = new TraceEventSession("LightingWatchdogSession"))
            {
                stoppingToken.Register(() => _etwSession?.Dispose());

                _etwSession.Source.Dynamic.All += HandleAfdEvent;
                _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");
                _etwSession.Source.Process(); 
            }
        }

        private void HandleAfdEvent(TraceEvent data)
        {
            if (data.ProcessID <= 0) return;

            if (data.EventName.Contains("Bind") || data.EventName.Contains("Accept") || data.EventName.Contains("Connect"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (_, count) => count + 1);
            }
            else if (data.EventName.Contains("Close") || data.EventName.Contains("Abort") || data.EventName.Contains("Disconnect"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (_, count) => Math.Max(0, count - 1));
            }
        }
    }
}