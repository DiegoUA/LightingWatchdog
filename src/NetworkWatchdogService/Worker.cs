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

            StartEtwSession(stoppingToken);

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

                    // 3. Export telemetry snapshot
                    TelemetryExporter.RecordHealthSnapshot(proc.ProcessName, proc.Id, currentConnections);

                    // 4. Enforce threshold limit (1000 sockets)
                    if (currentConnections >= 1000)
                    {
                        _logger.LogWarning($"[PID {proc.Id}] Socket threshold exceeded ({currentConnections} >= 1000). Triggering service recovery...");
                        RestartLeakingService("LightingService", proc.Id);
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

            // Create persistent session instance (removed inner using block to prevent premature disposal)
            _etwSession = new TraceEventSession("LightingWatchdogSession");

            stoppingToken.Register(() => 
            {
                _etwSession?.Stop();
                _etwSession?.Dispose();
            });

            _etwSession.Source.Dynamic.All += HandleAfdEvent;
            _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");

            // Process ETW events asynchronously on a background thread
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
            if (data.ProcessID == 0 || !_pidConnectionCounts.ContainsKey(data.ProcessID)) return;

            // Track actual connection establishments instead of raw allocations
            if (data.EventName.Contains("AfdConnect") || data.EventName.Contains("AfdAccept"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (pid, count) => count + 1);
            }
            else if (data.EventName.Contains("AfdClose"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (pid, count) => Math.Max(0, count - 1));
            }
        }

        private void RestartLeakingService(string serviceName, int processId)
        {
            TelemetryExporter.RecordRestart(serviceName, processId, "Exceeded 1000 socket threshold");

            _logger.LogWarning($"Attempting graceful stop of {serviceName}...");
            ExecuteCommand("sc", $"stop {serviceName}");

            _logger.LogWarning($"Force killing process tree for PID {processId}...");
            ExecuteCommand("taskkill", $"/F /T /PID {processId}");

            _logger.LogWarning("Process dead. Waiting 15s for Windows kernel to flush sockets...");
            Thread.Sleep(15000);

            _logger.LogInformation($"Sockets flushed. Restarting {serviceName}...");
            ExecuteCommand("sc", $"start {serviceName}");

            // Reset dictionaries so the baseline re-initializes on the next loop
            _pidConnectionCounts.TryRemove(processId, out _);
            _baselineInitialized.TryRemove(processId, out _);
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
                _logger.LogError(ex, $"Command failed: {filename} {arguments}");
            }
        }
    }
}