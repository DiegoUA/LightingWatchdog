using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
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
    private readonly List<string> _recentRestarts = new();
    private TelemetryPacket _latestPacket = new();

    public Worker(ILogger<Worker> logger, IOptions<WatchdogConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting AFD Kernel Tracing Session and IPC Pipe Server...");
        StartEtwSession(stoppingToken);
        _ = StartNamedPipeServerAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var telemetryBatch = new List<ProcessTelemetryItem>();

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
                        }

                        int currentConnections = _pidConnectionCounts.GetOrAdd(proc.Id, 0);
                        totalConnectionsForService += currentConnections;

                        string status = currentConnections < 500 ? "Healthy" : (currentConnections < target.MaxTcpConnections ? "Elevated" : "LEAK");
                        telemetryBatch.Add(new ProcessTelemetryItem { ServiceName = processName, Pid = proc.Id, Connections = currentConnections, Status = status });
                        TelemetryExporter.RecordHealthSnapshot(processName, proc.Id, currentConnections);
                    }
                }

                if (totalConnectionsForService >= target.MaxTcpConnections && target.MaxTcpConnections > 0)
                {
                    _logger.LogWarning(">>> LEAK DETECTED: {service} exceeded threshold ({count}/{max}) <<<",
                        target.ServiceName, totalConnectionsForService, target.MaxTcpConnections);

                    SyslogNotifier.SendAlert(_config.Syslog, target.ServiceName, targetPids.FirstOrDefault(), totalConnectionsForService, target.MaxTcpConnections, "Service leak threshold breached");
                    HandleServiceRecovery(target, targetPids);
                }
            }

            // 2. Universal Process Monitoring
            if (_config.MonitorAllProcesses)
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        int pid = proc.Id;
                        if (pid <= 4) continue;

                        string procName = proc.ProcessName;
                        if (_config.ProcessWhitelist.Contains(procName, StringComparer.OrdinalIgnoreCase)) continue;

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
                            string status = currentConnections < 500 ? "Healthy" : (currentConnections < _config.GlobalMaxTcpConnections ? "Elevated" : "LEAK");
                            telemetryBatch.Add(new ProcessTelemetryItem { ServiceName = procName, Pid = pid, Connections = currentConnections, Status = status });
                            TelemetryExporter.RecordHealthSnapshot(procName, pid, currentConnections);

                            if (currentConnections >= _config.GlobalMaxTcpConnections && _config.GlobalMaxTcpConnections > 0)
                            {
                                _logger.LogWarning(">>> GLOBAL LEAK DETECTED: {proc} (PID {pid}) exceeded threshold ({count}/{max}) <<<",
                                    procName, pid, currentConnections, _config.GlobalMaxTcpConnections);

                                SyslogNotifier.SendAlert(_config.Syslog, procName, pid, currentConnections, _config.GlobalMaxTcpConnections, "Global process socket leak mitigated");
                                ExecuteCommand("taskkill", $"/F /T /PID {pid}");
                                RecordRestartEvent(procName, pid, $"Exceeded global {_config.GlobalMaxTcpConnections} socket threshold");

                                _pidConnectionCounts.TryRemove(pid, out _);
                                _baselineInitialized.TryRemove(pid, out _);
                            }
                        }
                    }
                    catch { }
                }
            }

            // Update cached in-memory packet for instant IPC broadcast
            _latestPacket = new TelemetryPacket
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                GlobalMaxTcpConnections = _config.GlobalMaxTcpConnections,
                Whitelist = new List<string>(_config.ProcessWhitelist),
                Processes = telemetryBatch,
                RecentRestarts = new List<string>(_recentRestarts)
            };

            await Task.Delay(_config.WatchdogIntervalSeconds * 1000, stoppingToken);
        }
    }

    private async Task StartNamedPipeServerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream(
                    "NetworkWatchdogPipe",
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(stoppingToken);

                using var reader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipeServer, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(stoppingToken);
                if (line == "GET_TELEMETRY")
                {
                    string json = JsonSerializer.Serialize(_latestPacket);
                    await writer.WriteLineAsync(json.AsMemory(), stoppingToken);
                }
                else if (line?.StartsWith("UPDATE_CONFIG:") == true)
                {
                    string cmdJson = line["UPDATE_CONFIG:".Length..];
                    var cmd = JsonSerializer.Deserialize<ConfigUpdateCommand>(cmdJson);
                    if (cmd != null)
                    {
                        if (cmd.NewGlobalThreshold.HasValue && cmd.NewGlobalThreshold > 0)
                        {
                            _config.GlobalMaxTcpConnections = cmd.NewGlobalThreshold.Value;
                        }
                        if (!string.IsNullOrWhiteSpace(cmd.AddWhitelist) && !_config.ProcessWhitelist.Contains(cmd.AddWhitelist, StringComparer.OrdinalIgnoreCase))
                        {
                            _config.ProcessWhitelist.Add(cmd.AddWhitelist.Trim());
                        }
                        if (!string.IsNullOrWhiteSpace(cmd.RemoveWhitelist))
                        {
                            _config.ProcessWhitelist.RemoveAll(x => x.Equals(cmd.RemoveWhitelist.Trim(), StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    await writer.WriteLineAsync("OK".AsMemory(), stoppingToken);
                }
            }
            catch when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private void RecordRestartEvent(string serviceName, int pid, string reason)
    {
        string entry = $"{DateTime.UtcNow:o},{serviceName},{pid},{reason}";
        _recentRestarts.Insert(0, entry);
        if (_recentRestarts.Count > 100) _recentRestarts.RemoveAt(_recentRestarts.Count - 1);
        TelemetryExporter.RecordRestart(serviceName, pid, reason);
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
            return int.TryParse(output, out int count) ? count : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void StartEtwSession(CancellationToken stoppingToken)
    {
        if (!(TraceEventSession.IsElevated() ?? false)) return;

        if (TraceEventSession.GetActiveSessionNames().Contains("LightingWatchdogSession"))
        {
            using var oldSession = new TraceEventSession("LightingWatchdogSession");
            oldSession.Stop();
        }

        _etwSession = new TraceEventSession("LightingWatchdogSession");
        stoppingToken.Register(() => { _etwSession?.Stop(); _etwSession?.Dispose(); });
        _etwSession.Source.Dynamic.All += HandleAfdEvent;
        _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");

        Task.Run(() =>
        {
            try { _etwSession.Source.Process(); } catch { }
        }, stoppingToken);
    }

    private void HandleAfdEvent(TraceEvent data)
    {
        if (data.ProcessID == 0) return;

        if (!_pidConnectionCounts.ContainsKey(data.ProcessID) && _config.MonitorAllProcesses)
        {
            _pidConnectionCounts.TryAdd(data.ProcessID, 0);
            _baselineInitialized.TryAdd(data.ProcessID, true);
        }

        if (!_pidConnectionCounts.ContainsKey(data.ProcessID)) return;

        if (data.EventName.Contains("AfdConnect") || data.EventName.Contains("AfdAccept"))
        {
            _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (_, count) => count + 1);
        }
        else if (data.EventName.Contains("AfdClose"))
        {
            _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    private void HandleServiceRecovery(MonitoredService target, List<int> pids)
    {
        if (!target.EnableRestart) return;

        if (_lastRestartTimes.TryGetValue(target.ServiceName, out var lastRestart))
        {
            if ((DateTime.UtcNow - lastRestart).TotalMinutes < _config.CooldownSeconds) return;
        }

        RecordRestartEvent(target.ServiceName, pids.FirstOrDefault(), $"Exceeded {target.MaxTcpConnections} socket threshold");

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
            }
            catch { }
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
            var psi = new ProcessStartInfo { FileName = filename, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }
}