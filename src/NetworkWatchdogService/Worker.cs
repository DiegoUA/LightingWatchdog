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
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        private const int AF_INET = 2;
        private const int AF_INET6 = 23;

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

        private readonly string _stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "state.json");
        private readonly SemaphoreSlim _stateFileLock = new(1, 1);

        private volatile int _globalMaxTcpConnections = 1500;
        private readonly object _connectionCountsLock = new();

        private readonly record struct AfdEvent(int ProcessId, bool IsConnect);

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting NetworkWatchdogService Worker...");

            LoadStateFromDisk();
            ResyncGlobalBaseline();

            _ = Task.Run(() => ProcessEtwChannelAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => StartEtwSession(stoppingToken), stoppingToken);
            _ = Task.Run(() => StartTelemetryPipeServerAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => StartControlPipeServerAsync(stoppingToken), stoppingToken);
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
                                DateTime currentStartTime = proc.StartTime; 

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

                                string procName = proc.ProcessName;
                                string cleanName = Path.GetFileNameWithoutExtension(procName);
                                if (_whitelist.ContainsKey(cleanName) || _whitelist.ContainsKey(procName)) 
                                    continue;

                                _logger.LogWarning("CRITICAL: Socket leak breach ({count} > {max}) in PID {pid}!", currentConnections, _globalMaxTcpConnections, proc.Id);
                                
                                _activeRestarts.TryAdd(pid, DateTime.UtcNow);
                                
                                _ = RestartLeakingServiceAsync(procName, proc.Id, currentConnections, currentStartTime);
                            }
                            catch (Exception)
                            {
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
                    lock (_connectionCountsLock)
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

        private Dictionary<int, int>? GetNativeConnectionCounts()
        {
            var counts = new Dictionary<int, int>();
            const uint ERROR_INSUFFICIENT_BUFFER = 122;
            const int MaxAttempts = 5;

            bool Fetch(bool isUdp, int ipVersion, int rowSize, int pidOffset, int tblClass)
            {
                int bufferSize = 0;
                if (isUdp) GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, ipVersion, tblClass, 0);
                else GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, ipVersion, tblClass, 0);

                if (bufferSize == 0) return true;

                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    // Add substantial unmanaged padding to definitively beat rapid table TOCTOU growth
                    bufferSize += 100000; 
                    IntPtr tablePtr = Marshal.AllocHGlobal(bufferSize);
                    try
                    {
                        uint result = isUdp 
                            ? GetExtendedUdpTable(tablePtr, ref bufferSize, false, ipVersion, tblClass, 0)
                            : GetExtendedTcpTable(tablePtr, ref bufferSize, false, ipVersion, tblClass, 0);

                        if (result == 0)
                        {
                            int rowCount = Marshal.ReadInt32(tablePtr);
                            IntPtr rowPtr = tablePtr + 4; 
                            for (int i = 0; i < rowCount; i++)
                            {
                                int pid = Marshal.ReadInt32(rowPtr + pidOffset);
                                counts[pid] = counts.TryGetValue(pid, out int c) ? c + 1 : 1;
                                rowPtr += rowSize;
                            }
                            return true;
                        }

                        if (result != ERROR_INSUFFICIENT_BUFFER)
                        {
                            _logger.LogWarning("Native {type} fetch (IPv{ver}) failed with OS code {code}.", isUdp ? "UDP" : "TCP", ipVersion == AF_INET ? 4 : 6, result);
                            return false;
                        }
                    }
                    finally { Marshal.FreeHGlobal(tablePtr); }
                }

                _logger.LogWarning("GetExtended{type}Table (IPv{ver}) exhausted {attempts} buffer allocation attempts.", isUdp ? "Udp" : "Tcp", ipVersion == AF_INET ? 4 : 6, MaxAttempts);
                return false;
            }

            if (!Fetch(false, AF_INET, 24, 20, 5)) return null; // TCP_TABLE_OWNER_PID_ALL
            if (!Fetch(false, AF_INET6, 56, 52, 5)) return null;
            if (!Fetch(true, AF_INET, 12, 8, 1)) return null;   // UDP_TABLE_OWNER_PID
            if (!Fetch(true, AF_INET6, 28, 24, 1)) return null;

            return counts;
        }

        private void ResyncGlobalBaseline()
        {
            var counts = GetNativeConnectionCounts();
            if (counts == null)
            {
                _logger.LogWarning("Aborting baseline resync due to native API fetch failure. Preserving ETW states to prevent false negatives.");
                return;
            }

            lock (_connectionCountsLock)
            {
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

                PruneStaleCaches();
            }
        }

        private void PruneStaleCaches()
        {
            var live = new HashSet<int>(_pidConnectionCounts.Keys);

            foreach (var pid in _pidStartTimes.Keys.ToList())
                if (!live.Contains(pid)) _pidStartTimes.TryRemove(pid, out _);

            foreach (var pid in _processNameCache.Keys.ToList())
                if (!live.Contains(pid)) _processNameCache.TryRemove(pid, out _);

            foreach (var pid in _serviceNameCache.Keys.ToList())
                if (!live.Contains(pid)) _serviceNameCache.TryRemove(pid, out _);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task StartTelemetryPipeServerAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Telemetry IPC Server: \\\\.\\pipe\\NetworkWatchdogTelemetry");

            var pipeSecurity = new PipeSecurity();
            pipeSecurity.SetAccessRuleProtection(true, false);

            var sidAuth = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);

            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidAuth,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            var serviceSid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (serviceSid != null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    serviceSid,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = NamedPipeServerStreamAcl.Create(
                        "NetworkWatchdogTelemetry",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);

                    await server.WaitForConnectionAsync(stoppingToken);

                    var connectedServer = server;
                    server = null; 
                    _ = Task.Run(() => HandleTelemetryClientAsync(connectedServer, stoppingToken), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Telemetry Named Pipe accept-loop error.");
                    await Task.Delay(250, stoppingToken);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task StartControlPipeServerAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Admin Control IPC Server: \\\\.\\pipe\\NetworkWatchdogControl");

            var pipeSecurity = new PipeSecurity();
            pipeSecurity.SetAccessRuleProtection(true, false);

            var sidAdmin = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidAdmin,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            var serviceSid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (serviceSid != null)
            {
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    serviceSid,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = NamedPipeServerStreamAcl.Create(
                        "NetworkWatchdogControl",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);

                    await server.WaitForConnectionAsync(stoppingToken);

                    var connectedServer = server;
                    server = null; 
                    _ = Task.Run(() => HandleControlClientAsync(connectedServer, stoppingToken), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Control Named Pipe accept-loop error.");
                    await Task.Delay(250, stoppingToken);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private const int MaxIpcLineChars = 64 * 1024;
        private static readonly TimeSpan IpcClientTimeout = TimeSpan.FromSeconds(10);

        private async Task HandleTelemetryClientAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
        {
            using (server)
            using (var reader = new StreamReader(server, Encoding.UTF8))
            using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                cts.CancelAfter(IpcClientTimeout);
                var token = cts.Token;

                try
                {
                    string? line = await ReadBoundedLineAsync(reader, MaxIpcLineChars, token);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (line == "GET_TELEMETRY")
                        {
                            var packet = BuildTelemetryPacket();
                            string json = JsonSerializer.Serialize(packet);
                            await writer.WriteLineAsync(json.AsMemory(), token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
        }

        private async Task HandleControlClientAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
        {
            using (server)
            using (var reader = new StreamReader(server, Encoding.UTF8))
            using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                cts.CancelAfter(IpcClientTimeout);
                var token = cts.Token;

                try
                {
                    string? line = await ReadBoundedLineAsync(reader, MaxIpcLineChars, token);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        if (line.StartsWith("UPDATE_CONFIG:"))
                        {
                            string jsonPayload = line.Substring("UPDATE_CONFIG:".Length);
                            var updateCmd = JsonSerializer.Deserialize<ConfigUpdateCommand>(jsonPayload);
                            if (updateCmd != null) ApplyConfigUpdate(updateCmd);
                            await writer.WriteLineAsync("OK".AsMemory(), token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
        }

        private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maxChars, CancellationToken token)
        {
            var sb = new StringBuilder();
            var one = new char[1];

            while (await reader.ReadAsync(one.AsMemory(), token) > 0)
            {
                char c = one[0];
                if (c == '\n') break;
                if (c == '\r') continue;

                if (sb.Length >= maxChars)
                    throw new InvalidDataException($"IPC line exceeded {maxChars} characters.");

                sb.Append(c);
            }

            return sb.Length == 0 ? null : sb.ToString();
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
                // UI Noise Reduction: Do not serialize background processes with fewer than 30 sockets
                if (kvp.Value < 30) continue; 

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

                if (string.IsNullOrEmpty(procName)) continue;

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
            if (cmd.SaveRequested == true)
            {
                _ = SaveStateToDiskAsync();
                return;
            }

            if (cmd.NewGlobalThreshold.HasValue && cmd.NewGlobalThreshold.Value >= 200 && cmd.NewGlobalThreshold.Value <= 10000)
            {
                _globalMaxTcpConnections = cmd.NewGlobalThreshold.Value;
                _logger.LogInformation("IPC: Updated global threshold to {val}", _globalMaxTcpConnections);
            }

            if (!string.IsNullOrWhiteSpace(cmd.AddWhitelist))
            {
                if (TryNormalizeWhitelistEntry(cmd.AddWhitelist, out string entry) && _whitelist.TryAdd(entry, 1))
                {
                    _logger.LogInformation("IPC: Added {name} to whitelist.", entry);
                }
            }
            if (!string.IsNullOrWhiteSpace(cmd.RemoveWhitelist))
            {
                if (TryNormalizeWhitelistEntry(cmd.RemoveWhitelist, out string entry) && _whitelist.TryRemove(entry, out _))
                {
                    _logger.LogInformation("IPC: Removed {name} from whitelist.", entry);
                }
            }
        }

        private const long MaxStateFileBytes = 1024 * 1024;
        private const int MaxWhitelistEntries = 10_000;
        private const int MaxWhitelistEntryLength = 260;

        private static bool TryNormalizeWhitelistEntry(string? raw, out string entry)
        {
            entry = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string normalized = Path.GetFileNameWithoutExtension(raw.Trim());
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > MaxWhitelistEntryLength) return false;

            entry = normalized;
            return true;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void LoadStateFromDisk()
        {
            try
            {
                if (!File.Exists(_stateFilePath)) return;

                string? dir = Path.GetDirectoryName(_stateFilePath);
                if (string.IsNullOrEmpty(dir) ||
                    !IsAdminOnlyLocation(dir, isDirectory: true) ||
                    !IsAdminOnlyLocation(_stateFilePath, isDirectory: false))
                {
                    _logger.LogCritical("Refusing to load state from {path}: directory is not exclusively owned by Administrators. Starting clean.", _stateFilePath);
                    return;
                }

                if (new FileInfo(_stateFilePath).Length > MaxStateFileBytes) return;

                PersistedState? state;
                try
                {
                    state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_stateFilePath));
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "State file {path} is not valid JSON.", _stateFilePath);
                    return;
                }

                if (state == null) return;

                if (state.GlobalThreshold >= 200 && state.GlobalThreshold <= 10000)
                {
                    _globalMaxTcpConnections = state.GlobalThreshold;
                }

                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in state.Whitelist)
                {
                    if (unique.Count >= MaxWhitelistEntries) break;
                    if (TryNormalizeWhitelistEntry(raw, out string entry)) unique.Add(entry);
                }

                foreach (var entry in unique) _whitelist.TryAdd(entry, 1);

                _logger.LogInformation("Loaded config from {path}: Threshold={thresh}, Whitelist={count} items.", _stateFilePath, _globalMaxTcpConnections, unique.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load persisted state from {path}.", _stateFilePath);
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private bool IsAdminOnlyLocation(string path, bool isDirectory)
        {
            try
            {
                var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;

                System.Security.AccessControl.FileSystemSecurity security = isDirectory
                    ? new DirectoryInfo(path).GetAccessControl()
                    : new FileInfo(path).GetAccessControl();

                var owner = security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
                if (owner == null || !(owner.Equals(admins) || owner.Equals(system))) return false;

                const System.Security.AccessControl.FileSystemRights writeish = (System.Security.AccessControl.FileSystemRights)0x40000000 | (System.Security.AccessControl.FileSystemRights)0x10000000 | System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData | System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes | System.Security.AccessControl.FileSystemRights.WriteAttributes | System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;

                foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
                    var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
                    if (sid.Equals(admins) || sid.Equals(system)) continue;
                    if ((rule.FileSystemRights & writeish) != 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task SaveStateToDiskAsync()
        {
            await _stateFileLock.WaitAsync();
            try
            {
                string? dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    try { SecureDirectoryToAdminsAndSystem(dir); } catch { }
                    if (!IsAdminOnlyLocation(dir, isDirectory: true)) return;
                }

                var state = new PersistedState
                {
                    GlobalThreshold = _globalMaxTcpConnections,
                    Whitelist = _whitelist.Keys.ToList()
                };

                string tempPath = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state));
                try
                {
                    await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    {
                        await fs.WriteAsync(payload);
                        await fs.FlushAsync();
                    }
                    File.Move(tempPath, _stateFilePath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tempPath); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist state to disk.");
            }
            finally
            {
                _stateFileLock.Release();
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void SecureDirectoryToAdminsAndSystem(string dir)
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

            foreach (var sid in new[] { admins, system })
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            }
            new DirectoryInfo(dir).SetAccessControl(security);
        }

        private void StartEtwSession(CancellationToken stoppingToken)
        {
            if (!(TraceEventSession.IsElevated() ?? false)) return;
            if (TraceEventSession.GetActiveSessionNames().Contains("LightingWatchdogSession"))
            {
                using var oldSession = new TraceEventSession("LightingWatchdogSession");
                oldSession.Stop();
            }
            using (_etwSession = new TraceEventSession("LightingWatchdogSession"))
            {
                using var reg = stoppingToken.Register(() => { _etwSession?.Stop(); _etwSession?.Dispose(); });
                _etwSession.Source.Dynamic.All += HandleAfdEvent;
                _etwSession.EnableProvider("Microsoft-Windows-Winsock-AFD");
                _etwSession.Source.Process();
            }
        }

        private void HandleAfdEvent(TraceEvent data)
        {
            if (data.ProcessID <= 4) return;
            // Added AfdBind to actively catch UDP socket generation during high-frequency telemetry spikes
            if (data.EventName.Contains("AfdConnect") || data.EventName.Contains("AfdAccept") || data.EventName.Contains("AfdBind"))
            {
                _etwChannel.Writer.TryWrite(new AfdEvent(data.ProcessID, true));
            }
            else if (data.EventName.Contains("AfdClose"))
            {
                _etwChannel.Writer.TryWrite(new AfdEvent(data.ProcessID, false));
            }
        }

        private DateTime _lastServiceCacheRefreshUtc = DateTime.MinValue;
        private readonly object _serviceCacheRefreshLock = new();
        private static readonly TimeSpan ServiceCacheTtl = TimeSpan.FromSeconds(30);

        private string? GetServiceNameFromPidCached(int processId)
        {
            RefreshServiceNameCacheIfStale();
            return _serviceNameCache.TryGetValue(processId, out string? cachedName) ? cachedName : null;
        }

        private void RefreshServiceNameCacheIfStale()
        {
            lock (_serviceCacheRefreshLock)
            {
                if (DateTime.UtcNow - _lastServiceCacheRefreshUtc < ServiceCacheTtl) return;
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId != 0");
                    using var objects = searcher.Get();
                    foreach (ManagementObject obj in objects.Cast<ManagementObject>())
                    {
                        if (obj["ProcessId"] is uint pid && obj["Name"] is string name && !string.IsNullOrEmpty(name))
                            _serviceNameCache[(int)pid] = name;
                    }
                    _lastServiceCacheRefreshUtc = DateTime.UtcNow;
                }
                catch { }
            }
        }

        private async Task RestartLeakingServiceAsync(string fallbackServiceName, int processId, int socketCount, DateTime expectedStartTime)
        {
            try
            {
                if (!TryVerifySamePid(processId, expectedStartTime, out Process? proc)) return;

                string? actualServiceName = GetServiceNameFromPidCached(processId);
                string? scmServiceName = IsValidServiceName(actualServiceName) ? actualServiceName : null;
                string serviceNameToUse = scmServiceName ?? fallbackServiceName;

                _logger.LogWarning("Force killing leaking process/service {serviceName} (PID {pid})...", serviceNameToUse, processId);

                if (scmServiceName != null) ExecuteSafeCommand("sc.exe", new[] { "stop", scmServiceName }, suppressErrors: true);

                if (!TryVerifySamePid(processId, expectedStartTime, out _))
                {
                    proc?.Dispose();
                    return;
                }
                ExecuteSafeCommand("taskkill.exe", new[] { "/F", "/T", "/PID", processId.ToString() });
                proc?.Dispose();

                _recentRestarts.Enqueue($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{serviceNameToUse},{processId},Exceeded {socketCount} sockets");
                while (_recentRestarts.Count > 50 && _recentRestarts.TryDequeue(out _)) { }

                _pidConnectionCounts.TryRemove(processId, out _);
                _pidStartTimes.TryRemove(processId, out _);
                _processNameCache.TryRemove(processId, out _);
                _serviceNameCache.TryRemove(processId, out _);

                await Task.Delay(30000);

                if (scmServiceName != null)
                {
                    await TryStartServiceAsync(scmServiceName);
                }
            }
            finally
            {
                _activeRestarts.TryRemove(processId, out _);
            }
        }

        private static bool IsValidServiceName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256) return false;
            if (name != name.Trim()) return false;
            if (name[0] == '-' || name[0] == '/') return false;
            foreach (char c in name) if (char.IsControl(c) || c == '/' || c == '\\' || c == '"') return false;
            return true;
        }

        private bool TryVerifySamePid(int processId, DateTime expectedStartTime, out Process? proc)
        {
            proc = null;
            try
            {
                var candidate = Process.GetProcessById(processId);
                if (candidate.StartTime != expectedStartTime)
                {
                    candidate.Dispose();
                    return false;
                }
                proc = candidate;
                return true;
            }
            catch { return false; }
        }

        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
        private const int ServiceStateStopped = 1;
        private const int ServiceStateRunning = 4;
        private static readonly TimeSpan ScCommandTimeout = TimeSpan.FromSeconds(30);
        private static readonly System.Text.RegularExpressions.Regex ScStateRegex = new(@"STATE\s*:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private async Task<bool> TryStartServiceAsync(string serviceName)
        {
            var start = await RunScAsync("start", serviceName);
            if (start == null) return false; 
            var (startExit, startOutput) = start.Value;

            if (startExit != 0 && startExit != ERROR_SERVICE_ALREADY_RUNNING) return false;

            const int maxPolls = 15;
            for (int i = 0; i < maxPolls; i++)
            {
                var query = await RunScAsync("query", serviceName);
                if (query != null)
                {
                    var (queryExit, queryOutput) = query.Value;
                    if (queryExit != 0) return false;
                    var match = ScStateRegex.Match(queryOutput);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out int state)) return false;
                    if (state == ServiceStateRunning) return true;
                    if (state == ServiceStateStopped) return false;
                }
                await Task.Delay(2000);
            }
            return false;
        }

        private async Task<(int ExitCode, string Output)?> RunScAsync(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(ScCommandTimeout);
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                return (proc.ExitCode, ((await stdoutTask) + (await stderrTask)).Trim());
            }
            catch { return null; }
        }

        private void ExecuteSafeCommand(string filename, string[] args, bool suppressErrors = false)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, filename), UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in args) psi.ArgumentList.Add(arg);
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            catch { }
        }
    }

    public class PersistedState
    {
        public int GlobalThreshold { get; set; } = 1500;
        public List<string> Whitelist { get; set; } = new();
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
        public bool? SaveRequested { get; set; }
    }
}