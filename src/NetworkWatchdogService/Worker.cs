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
                        RestartLeakingService(proc.ProcessName, proc.Id);
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
            // 1. Proactive PID Filter: Discard irrelevant kernel events instantly
            if (data.ProcessID == 0 || !_pidConnectionCounts.ContainsKey(data.ProcessID))
            {
                return;
            }

            // 2. Parse AFD Socket Allocations based on discovered kernel event names
            if (data.EventName.Contains("AfdCreate") || data.EventName.Contains("AfdBind") || data.EventName.Contains("AfdConnect"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (pid, count) => count + 1);
            }
            else if (data.EventName.Contains("AfdClose") || data.EventName.Contains("AfdDisconnect"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (pid, count) => Math.Max(0, count - 1));
            }
        }

        private void RestartLeakingService(string serviceName, int processId)
        {
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
