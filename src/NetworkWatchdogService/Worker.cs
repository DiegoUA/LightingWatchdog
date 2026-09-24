using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetworkWatchdogService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private TraceEventSession? _etwSession;

        private readonly ConcurrentDictionary<int, int> _pidConnectionCounts = new();
        private readonly ConcurrentDictionary<int, bool> _baselineInitialized = new();
        private readonly ConcurrentBag<string> _recentRestarts = new();
        private readonly HashSet<string> _whitelist = new(StringComparer.OrdinalIgnoreCase);
        private int _globalMaxTcpConnections = 1500;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting NetworkWatchdogService Worker...");

            // 1. Start ETW Trace in Background
            _ = Task.Run(() => StartEtwSession(stoppingToken), stoppingToken);

            // 2. Start Named Pipe Server for Tray App IPC
            _ = Task.Run(() => StartNamedPipeServerAsync(stoppingToken), stoppingToken);

            // 3. Health & Mitigation Loop
            while (!stoppingToken.IsCancellationRequested)
            {
                var targetProcesses = Process.GetProcessesByName("LightingService");

                foreach (var proc in targetProcesses)
                {
                    if (!_baselineInitialized.ContainsKey(proc.Id))
                    {
                        int baseline = GetBaselineSocketCount(proc.Id);
                        _pidConnectionCounts.AddOrUpdate(proc.Id, baseline, (_, current) => current + baseline);
                        _baselineInitialized.TryAdd(proc.Id, true);
                        _logger.LogInformation("[PID {pid}] Baseline initialized with {count} sockets.", proc.Id, baseline);
                    }

                    int currentConnections = _pidConnectionCounts.GetOrAdd(proc.Id, 0);

                    lock (_whitelist)
                    {
                        if (_whitelist.Contains(proc.ProcessName))
                        {
                            continue;
                        }
                    }

                    if (currentConnections > _globalMaxTcpConnections)
                    {
                        _logger.LogWarning("CRITICAL: Socket leak breach ({count} > {max}) in {name} (PID: {pid})!", currentConnections, _globalMaxTcpConnections, proc.ProcessName, proc.Id);
                        RestartLeakingService(proc.ProcessName, proc.Id, currentConnections);
                    }
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task StartNamedPipeServerAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Named Pipe IPC Server: \\\\.\\pipe\\NetworkWatchdogPipe");

            var pipeSecurity = new PipeSecurity();

            // Grant Everyone (WorldSid) and Authenticated Users (AuthenticatedUserSid) full Read/Write access
            var sidWorld = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.WorldSid, null);
            var sidAuth = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);

            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidWorld,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidAuth,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Create using NamedPipeServerStreamAcl to bind security descriptor
                    using var server = NamedPipeServerStreamAcl.Create(
                        "NetworkWatchdogPipe",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);

                    await server.WaitForConnectionAsync(stoppingToken);

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                    string? line = await reader.ReadLineAsync(stoppingToken);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (line == "GET_TELEMETRY")
                        {
                            var packet = BuildTelemetryPacket();
                            string json = JsonSerializer.Serialize(packet);
                            await writer.WriteLineAsync(json.AsMemory(), stoppingToken);
                        }
                        else if (line.StartsWith("UPDATE_CONFIG:"))
                        {
                            string jsonPayload = line.Substring("UPDATE_CONFIG:".Length);
                            var updateCmd = JsonSerializer.Deserialize<ConfigUpdateCommand>(jsonPayload);
                            if (updateCmd != null)
                            {
                                ApplyConfigUpdate(updateCmd);
                            }
                            await writer.WriteLineAsync("OK".AsMemory(), stoppingToken);
                        }
                    }

                    if (server.IsConnected)
                    {
                        server.Disconnect();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Named Pipe connection error.");
                    await Task.Delay(250, stoppingToken);
                }
            }
        }
        private TelemetryPacket BuildTelemetryPacket()
        {
            var packet = new TelemetryPacket
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                GlobalMaxTcpConnections = _globalMaxTcpConnections,
                RecentRestarts = _recentRestarts.Take(20).ToList()
            };

            lock (_whitelist)
            {
                packet.Whitelist = _whitelist.ToList();
            }

            foreach (var kvp in _pidConnectionCounts)
            {
                string procName = "LightingService";
                try
                {
                    var p = Process.GetProcessById(kvp.Key);
                    procName = p.ProcessName;
                }
                catch { }

                string status = kvp.Value < 500 ? "Healthy" : (kvp.Value < _globalMaxTcpConnections ? "Elevated" : "CRITICAL LEAK");
                packet.Processes.Add(new ProcessTelemetryItem
                {
                    ServiceName = procName,
                    Pid = kvp.Key,
                    Connections = kvp.Value,
                    Status = status
                });
            }

            return packet;
        }

        private void ApplyConfigUpdate(ConfigUpdateCommand cmd)
        {
            if (cmd.NewGlobalThreshold.HasValue && cmd.NewGlobalThreshold.Value >= 500)
            {
                _globalMaxTcpConnections = cmd.NewGlobalThreshold.Value;
                _logger.LogInformation("IPC: Updated global threshold to {val}", _globalMaxTcpConnections);
            }

            lock (_whitelist)
            {
                if (!string.IsNullOrWhiteSpace(cmd.AddWhitelist))
                {
                    _whitelist.Add(cmd.AddWhitelist.Trim());
                    _logger.LogInformation("IPC: Added {name} to whitelist.", cmd.AddWhitelist);
                }
                if (!string.IsNullOrWhiteSpace(cmd.RemoveWhitelist))
                {
                    _whitelist.Remove(cmd.RemoveWhitelist.Trim());
                    _logger.LogInformation("IPC: Removed {name} from whitelist.", cmd.RemoveWhitelist);
                }
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
                return int.TryParse(output, out int count) ? count : 0;
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
            if (data.ProcessID == 0 || !_pidConnectionCounts.ContainsKey(data.ProcessID)) return;

            if (data.EventName.Contains("AfdConnect") || data.EventName.Contains("AfdAccept"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 1, (_, count) => count + 1);
            }
            else if (data.EventName.Contains("AfdClose"))
            {
                _pidConnectionCounts.AddOrUpdate(data.ProcessID, 0, (_, count) => Math.Max(0, count - 1));
            }
        }

        private void RestartLeakingService(string serviceName, int processId, int socketCount)
        {
            _logger.LogWarning("Stopping service {serviceName}...", serviceName);
            ExecuteCommand("sc", $"stop {serviceName}");

            _logger.LogWarning("Force killing PID {pid}...", processId);
            ExecuteCommand("taskkill", $"/F /T /PID {processId}");

            _recentRestarts.Add($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{serviceName},{processId},Exceeded {socketCount} sockets");

            Thread.Sleep(15000);

            ExecuteCommand("sc", $"start {serviceName}");

            _pidConnectionCounts.TryRemove(processId, out _);
            _baselineInitialized.TryRemove(processId, out _);
        }

        private void ExecuteCommand(string filename, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = filename, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command failed: {filename} {arguments}", filename, arguments);
            }
        }
    }

    public class TelemetryPacket
{
    public string Timestamp { get; set; } = string.Empty;
    public int GlobalMaxTcpConnections { get; set; }
    public List<string> Whitelist { get; set; } = new();
    public List<ProcessTelemetryItem> Processes { get; set; } = new();
    public List<string> RecentRestarts { get; set; } = new();
}

public class ProcessTelemetryItem
{
    public string ServiceName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public int Connections { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class ConfigUpdateCommand
{
    public int? NewGlobalThreshold { get; set; }
    public string? AddWhitelist { get; set; }
    public string? RemoveWhitelist { get; set; }
}
}