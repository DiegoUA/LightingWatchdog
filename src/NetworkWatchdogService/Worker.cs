using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetworkWatchdogService
{
    public class Worker : BackgroundService
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        private readonly ILogger<Worker> _logger;
        private TraceEventSession? _etwSession;
        
        private readonly Channel<AfdEvent> _etwChannel = Channel.CreateUnbounded<AfdEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private readonly ConcurrentDictionary<int, int> _pidConnectionCounts = new();
        private readonly ConcurrentDictionary<int, DateTime> _pidStartTimes = new();
        private readonly ConcurrentDictionary<int, string> _processNameCache = new();
        private readonly ConcurrentDictionary<int, string> _serviceNameCache = new();
        private readonly ConcurrentDictionary<int, DateTime> _activeRestarts = new();
        private readonly ConcurrentQueue<string> _recentRestarts = new();
        
        private readonly ConcurrentDictionary<string, byte> _whitelist = new(StringComparer.OrdinalIgnoreCase);
        
        private volatile int _globalMaxTcpConnections = 1500;

        private readonly record struct AfdEvent(int ProcessId, bool IsConnect);

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting NetworkWatchdogService Worker...");

            ResyncGlobalBaseline();

            _ = Task.Run(() => ProcessEtwChannelAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => StartEtwSession(stoppingToken), stoppingToken);
            _ = Task.Run(() => StartNamedPipeServerAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => PeriodicResyncLoopAsync(stoppingToken), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var currentPids = _pidConnectionCounts.Keys.ToList();

                foreach (int pid in currentPids)
                {
                    if (_activeRestarts.ContainsKey(pid)) continue;

                    if (_pidConnectionCounts.TryGetValue(pid, out int currentConnections))
                    {
                        if (currentConnections > _globalMaxTcpConnections)
                        {
                            try
                            {
                                using var proc = Process.GetProcessById(pid);
                                DateTime currentStartTime = proc.StartTime; // Can throw Win32Exception or InvalidOperationException

                                if (_pidStartTimes.TryGetValue(pid, out DateTime knownStartTime))
                                {
                                    if (currentStartTime != knownStartTime)
                                    {
                                        _logger.LogInformation("PID {pid} was reused. Resetting tracking states.", pid);
                                        _pidConnectionCounts.TryRemove(pid, out _);
                                        _pidStartTimes[pid] = currentStartTime;
                                        _processNameCache.TryRemove(pid, out _);
                                        _serviceNameCache.TryRemove(pid, out _);
                                        continue;
                                    }
                                }
                                else
                                {
                                    _pidStartTimes[pid] = currentStartTime;
                                }

                                string cleanName = Path.GetFileNameWithoutExtension(proc.ProcessName);
                                if (_whitelist.ContainsKey(cleanName) || _whitelist.ContainsKey(proc.ProcessName)) 
                                    continue;

                                _logger.LogWarning("CRITICAL: Socket leak breach ({count} > {max}) in PID {pid}!", currentConnections, _globalMaxTcpConnections, proc.Id);
                                
                                _activeRestarts.TryAdd(pid, DateTime.UtcNow);
                                
                                _ = RestartLeakingServiceAsync(proc.ProcessName, proc.Id, currentConnections);
                            }
                            catch (Exception)
                            {
                                // Process exited organically or access denied
                                _pidConnectionCounts.TryRemove(pid, out _);
                                _pidStartTimes.TryRemove(pid, out _);
                                _processNameCache.TryRemove(pid, out _);
                                _serviceNameCache.TryRemove(pid, out _);
                            }
                        }
                    }
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        private async Task ProcessEtwChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var evt in _etwChannel.Reader.ReadAllAsync(token))
                {
                    if (evt.IsConnect)
                    {
                        _pidConnectionCounts.AddOrUpdate(evt.ProcessId, 1, (_, count) => count + 1);
                    }
                    else
                    {
                        _pidConnectionCounts.AddOrUpdate(evt.ProcessId, 0, (_, count) => Math.Max(0, count - 1));
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task PeriodicResyncLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                ResyncGlobalBaseline();
            }
        }

        private Dictionary<int, int> GetNativeTcpConnectionCounts()
        {
            var counts = new Dictionary<int, int>();

            void Fetch(int ipVersion, int rowSize, int pidOffset)
            {
                int bufferSize = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);
                if (bufferSize == 0) return;

                IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    if (GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                    {
                        int rowCount = Marshal.ReadInt32(tcpTablePtr);
                        IntPtr rowPtr = tcpTablePtr + 4; // Skip dwNumEntries

                        for (int i = 0; i < rowCount; i++)
                        {
                            int pid = Marshal.ReadInt32(rowPtr + pidOffset);
                            counts[pid] = counts.TryGetValue(pid, out int c) ? c + 1 : 1;
                            rowPtr += rowSize;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(tcpTablePtr);
                }
            }

            // MIB_TCPROW_OWNER_PID: size 24, PID offset 20. MIB_TCP6ROW_OWNER_PID: size 56, PID offset 52.
            Fetch(AF_INET, 24, 20);
            Fetch(AF_INET6, 56, 52);

            return counts;
        }

        private void ResyncGlobalBaseline()
        {
            _logger.LogInformation("Resyncing socket baselines via IP Helper API...");
            var counts = new Dictionary<int, int>();
            try
            {
                counts = GetNativeTcpConnectionCounts();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute native TCP table baseline fetch.");
                return;
            }

            var trackedPids = _pidConnectionCounts.Keys.ToList();
            foreach (var pid in trackedPids)
            {
                if (!counts.ContainsKey(pid))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        _pidConnectionCounts[pid] = 0; 
                    }
                    catch
                    {
                        _pidConnectionCounts.TryRemove(pid, out _);
                        _pidStartTimes.TryRemove(pid, out _);
                        _processNameCache.TryRemove(pid, out _);
                        _serviceNameCache.TryRemove(pid, out _);
                    }
                }
            }

            foreach (var kvp in counts)
            {
                if (kvp.Key > 4)
                {
                    _pidConnectionCounts[kvp.Key] = kvp.Value;
                }
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task StartNamedPipeServerAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Named Pipe IPC Server: \\\\.\\pipe\\NetworkWatchdogPipe");

            var pipeSecurity = new PipeSecurity();
            var sidAdmin = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            // Locked down completely to BuiltinAdministratorsSid to prevent LPE/DoS
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidAdmin,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
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

                    using (var reader = new StreamReader(server, Encoding.UTF8))
                    using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
                    {
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
                                if (updateCmd != null) ApplyConfigUpdate(updateCmd);
                                await writer.WriteLineAsync("OK".AsMemory(), stoppingToken);
                            }
                        }
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
                RecentRestarts = _recentRestarts.ToList(),
                Whitelist = _whitelist.Keys.ToList()
            };

            foreach (var kvp in _pidConnectionCounts)
            {
                if (!_processNameCache.TryGetValue(kvp.Key, out string? procName))
                {
                    try
                    {
                        using var p = Process.GetProcessById(kvp.Key);
                        procName = p.ProcessName;
                        _processNameCache[kvp.Key] = procName;
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(procName))
                {
                    continue;
                }

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
            if (cmd.NewGlobalThreshold.HasValue && cmd.NewGlobalThreshold.Value >= 200 && cmd.NewGlobalThreshold.Value <= 10000)
            {
                _globalMaxTcpConnections = cmd.NewGlobalThreshold.Value;
                _logger.LogInformation("IPC: Updated global threshold to {val}", _globalMaxTcpConnections);
            }

            if (!string.IsNullOrWhiteSpace(cmd.AddWhitelist))
            {
                string entry = Path.GetFileNameWithoutExtension(cmd.AddWhitelist.Trim());
                _whitelist.TryAdd(entry, 1);
                _logger.LogInformation("IPC: Added {name} to whitelist.", entry);
            }
            if (!string.IsNullOrWhiteSpace(cmd.RemoveWhitelist))
            {
                string entry = Path.GetFileNameWithoutExtension(cmd.RemoveWhitelist.Trim());
                _whitelist.TryRemove(entry, out _);
                _logger.LogInformation("IPC: Removed {name} from whitelist.", entry);
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
                using var reg = stoppingToken.Register(() => 
                {
                    _etwSession?.Stop();
                    _etwSession?.Dispose();
                });

                _etwSession.Source.Dynamic.All += HandleAfdEvent;
                _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");
                _etwSession.Source.Process();
            }
        }

        private void HandleAfdEvent(TraceEvent data)
        {
            if (data.ProcessID <= 4) return;

            if (data.EventName.Contains("AfdConnect") || data.EventName.Contains("AfdAccept"))
            {
                _etwChannel.Writer.TryWrite(new AfdEvent(data.ProcessID, true));
            }
            else if (data.EventName.Contains("AfdClose"))
            {
                _etwChannel.Writer.TryWrite(new AfdEvent(data.ProcessID, false));
            }
        }

        private string? GetServiceNameFromPidCached(int processId)
        {
            if (_serviceNameCache.TryGetValue(processId, out string? cachedName)) return cachedName;

            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE ProcessId = {processId}");
                using var objects = searcher.Get();
                var obj = objects.Cast<ManagementObject>().FirstOrDefault();
                string? name = obj?["Name"]?.ToString();

                if (!string.IsNullOrEmpty(name)) _serviceNameCache[processId] = name;
                return name;
            }
            catch
            {
                return null;
            }
        }

        private string GetProcessCommandLine(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
                using var objects = searcher.Get();
                var obj = objects.Cast<ManagementObject>().FirstOrDefault();
                return obj?["CommandLine"]?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<string> ParseNativeArguments(string commandLine)
        {
            var argsList = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return argsList;

            IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
            if (argv == IntPtr.Zero || argc == 0) return argsList;

            try
            {
                for (int i = 1; i < argc; i++)
                {
                    IntPtr pStr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    string? arg = Marshal.PtrToStringUni(pStr);
                    if (arg != null) argsList.Add(arg);
                }
            }
            finally
            {
                LocalFree(argv);
            }
            return argsList;
        }

        private bool IsTrustedExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            return path.StartsWith(pf, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(pf86, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
        }

        private async Task RestartLeakingServiceAsync(string fallbackServiceName, int processId, int socketCount)
        {
            try
            {
                string? actualServiceName = GetServiceNameFromPidCached(processId);
                string serviceNameToUse = !string.IsNullOrEmpty(actualServiceName) ? actualServiceName : fallbackServiceName;
                string commandLine = GetProcessCommandLine(processId);

                _logger.LogWarning("Force killing leaking process/service {serviceName} (PID {pid})...", serviceNameToUse, processId);

                string processPath = string.Empty;
                try
                {
                    using var proc = Process.GetProcessById(processId);
                    processPath = proc.MainModule?.FileName ?? string.Empty;
                }
                catch { }

                ExecuteSafeCommand("sc.exe", new[] { "stop", serviceNameToUse }, suppressErrors: true);
                ExecuteSafeCommand("taskkill.exe", new[] { "/F", "/T", "/PID", processId.ToString() });

                _recentRestarts.Enqueue($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{serviceNameToUse},{processId},Exceeded {socketCount} sockets");
                while (_recentRestarts.Count > 50 && _recentRestarts.TryDequeue(out _)) { }

                _pidConnectionCounts.TryRemove(processId, out _);
                _pidStartTimes.TryRemove(processId, out _);
                _processNameCache.TryRemove(processId, out _);
                _serviceNameCache.TryRemove(processId, out _);

                await Task.Delay(30000);

                bool startedAsService = TryStartService(serviceNameToUse);
                
                if (!startedAsService && !string.IsNullOrEmpty(processPath) && File.Exists(processPath))
                {
                    if (!IsTrustedExecutablePath(processPath))
                    {
                        _logger.LogWarning("LPE GUARD: Refusing to auto-restart untrusted executable outside system boundaries: {path}", processPath);
                        return;
                    }

                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = processPath,
                            UseShellExecute = false
                        };

                        var parsedArgs = ParseNativeArguments(commandLine);
                        foreach (var arg in parsedArgs)
                        {
                            psi.ArgumentList.Add(arg);
                        }

                        _logger.LogInformation("Relaunching standalone executable: {path} (with {count} arguments)", processPath, parsedArgs.Count);
                        Process.Start(psi);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to restart process executable {path}", processPath);
                    }
                }
            }
            finally
            {
                _activeRestarts.TryRemove(processId, out _);
            }
        }

        private bool TryStartService(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    ArgumentList = { "query", serviceName },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();

                if (proc?.ExitCode == 0)
                {
                    ExecuteSafeCommand("sc.exe", new[] { "start", serviceName }, suppressErrors: true);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void ExecuteSafeCommand(string filename, string[] args, bool suppressErrors = false)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = filename, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                if (!suppressErrors) _logger.LogError(ex, "Command failed: {filename}", filename);
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