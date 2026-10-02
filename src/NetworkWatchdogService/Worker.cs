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

        private readonly string _whitelistFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "whitelist.json");
        private readonly SemaphoreSlim _whitelistFileLock = new(1, 1);

        private volatile int _globalMaxTcpConnections = 1500;
        private readonly object _connectionCountsLock = new();

        private readonly record struct AfdEvent(int ProcessId, bool IsConnect);

        // Bumped every time this file changes, logged loudly at startup and
        // exposed in telemetry, so you can tell at a glance - from the
        // service's own log or the dashboard itself - whether the process
        // that's actually running matches the source you just built. Given
        // that none of the last several fixes changed the observed behavior
        // at all, confirming this before chasing another code theory will
        // save us both time if the real issue turns out to be a stale build.
        private const string BuildMarker = "2026-10-02-diag1";

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogCritical("Starting NetworkWatchdogService Worker. BuildMarker={marker}", BuildMarker);

            LoadWhitelistFromDisk();
            SafeResyncGlobalBaseline("startup");

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

                                // Temporarily LogCritical, not LogWarning: your Event Log provider is
                                // filtering out Warning/Information entirely (confirmed - only Critical
                                // came through), so this and the other lines below are bumped purely so
                                // we can actually see them while we diagnose. Revert once we're done.
                                _logger.LogCritical("CRITICAL: Socket leak breach ({count} > {max}) in PID {pid}!", currentConnections, _globalMaxTcpConnections, proc.Id);
                                
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
                SafeResyncGlobalBaseline("periodic");
            }
        }

        /// <summary>
        /// ResyncGlobalBaseline already guards the native fetch itself, but
        /// NOT the dictionary merge/prune section that follows it, and
        /// PeriodicResyncLoopAsync's call site had no guard at all. Since this
        /// loop runs as an unobserved fire-and-forget Task, a single unhandled
        /// exception anywhere in ResyncGlobalBaseline - fetch or merge - kills
        /// resync silently and permanently for the life of the process, with
        /// zero crash and zero log, leaving _pidConnectionCounts to drift on
        /// ETW deltas alone indefinitely. This wrapper closes that gap and
        /// guarantees we at least see it happen if it does.
        /// </summary>
        private void SafeResyncGlobalBaseline(string trigger)
        {
            try
            {
                ResyncGlobalBaseline();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "ResyncGlobalBaseline threw and was about to silently kill baseline resync for the rest of the process's life (trigger: {trigger}, BuildMarker: {marker}).", trigger, BuildMarker);
            }
        }

        private Dictionary<int, int> GetNativeTcpConnectionCounts()
        {
            var counts = new Dictionary<int, int>();

            const uint ERROR_INSUFFICIENT_BUFFER = 122;
            const int MaxAttempts = 5;

            void Fetch(int ipVersion, int rowSize, int pidOffset)
            {
                int bufferSize = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);
                if (bufferSize == 0) return;

                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
                    try
                    {
                        uint result = GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);

                        if (result == 0)
                        {
                            int rowCount = Marshal.ReadInt32(tcpTablePtr);
                            IntPtr rowPtr = tcpTablePtr + 4; 

                            for (int i = 0; i < rowCount; i++)
                            {
                                int pid = Marshal.ReadInt32(rowPtr + pidOffset);
                                counts[pid] = counts.TryGetValue(pid, out int c) ? c + 1 : 1;
                                rowPtr += rowSize;
                            }
                            return;
                        }

                        if (result != ERROR_INSUFFICIENT_BUFFER)
                        {
                            return;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(tcpTablePtr);
                    }
                }

                _logger.LogCritical("GetExtendedTcpTable (IPv{ver}) kept returning ERROR_INSUFFICIENT_BUFFER after {attempts} attempts; skipping this cycle.", ipVersion == AF_INET ? 4 : 6, MaxAttempts);
            }

            Fetch(AF_INET, 24, 20);
            Fetch(AF_INET6, 56, 52);

            return counts;
        }

        /// <summary>
        /// Diagnostic only: logs the native table row count for whichever PID(s)
        /// currently belong to a process with this name. Resolved by name every
        /// call rather than cached, since the whole point is surviving restarts
        /// (and therefore PID changes) without needing a code edit/redeploy. If
        /// the process restarts mid-leak you may briefly see it logged under two
        /// PIDs (the dying one and its replacement) in the same cycle - that's
        /// expected, not a bug.
        /// </summary>
        private void LogNativeCountForWatchedProcess(Dictionary<int, int> counts, string processName)
        {
            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to resolve PID(s) for watched process name {name}.", processName);
                return;
            }

            try
            {
                if (matches.Length == 0)
                {
                    _logger.LogCritical("Baseline resync: no running process named {name} found.", processName);
                    return;
                }

                foreach (var proc in matches)
                {
                    int pid = proc.Id;
                    _logger.LogCritical("Baseline resync: {name} (PID {pid}) = {val} native TCP table rows.",
                        processName, pid, counts.TryGetValue(pid, out int val) ? val.ToString() : "not present in native table");
                }
            }
            finally
            {
                foreach (var proc in matches) proc.Dispose();
            }
        }

        private void ResyncGlobalBaseline()
        {
            _logger.LogCritical("Resyncing socket baselines via IP Helper API...");
            var counts = new Dictionary<int, int>();
            try
            {
                counts = GetNativeTcpConnectionCounts();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to execute native TCP table baseline fetch.");
                return;
            }

            // Unconditional, every cycle, regardless of threshold: proves the
            // fetch ran and shows exactly what it saw for the watched process,
            // present or not. Looked up by name, not a hardcoded PID - PIDs are
            // reassigned every time the process restarts, so a literal number
            // here would need editing (and redeploying) after every restart.
            LogNativeCountForWatchedProcess(counts, "LightingService");

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
                    if (kvp.Value >= 50)
                    {
                        // Proof, not a guess: if this logs a number close to what
                        // Get-NetTCPConnection shows for the same PID, resync is
                        // reading reality correctly and the dashboard's gap is a
                        // semantics issue (live AFD handles vs. all table rows,
                        // including TIME_WAIT), not a fetch bug. If this NEVER
                        // logs for a PID you know is leaking, resync still isn't
                        // reaching that PID's true count.
                        _logger.LogCritical("Baseline resync: PID {pid} has {count} native TCP table rows. BuildMarker={marker}", kvp.Key, kvp.Value, BuildMarker);
                    }
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
                Whitelist = _whitelist.Keys.ToList(),
                BuildMarker = BuildMarker
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
            bool whitelistChanged = false;

            if (cmd.NewGlobalThreshold.HasValue && cmd.NewGlobalThreshold.Value >= 200 && cmd.NewGlobalThreshold.Value <= 10000)
            {
                _globalMaxTcpConnections = cmd.NewGlobalThreshold.Value;
                _logger.LogInformation("IPC: Updated global threshold to {val}", _globalMaxTcpConnections);
            }

            if (!string.IsNullOrWhiteSpace(cmd.AddWhitelist))
            {
                if (TryNormalizeWhitelistEntry(cmd.AddWhitelist, out string entry) && _whitelist.TryAdd(entry, 1))
                {
                    whitelistChanged = true;
                    _logger.LogInformation("IPC: Added {name} to whitelist.", entry);
                }
            }
            if (!string.IsNullOrWhiteSpace(cmd.RemoveWhitelist))
            {
                if (TryNormalizeWhitelistEntry(cmd.RemoveWhitelist, out string entry) && _whitelist.TryRemove(entry, out _))
                {
                    whitelistChanged = true;
                    _logger.LogInformation("IPC: Removed {name} from whitelist.", entry);
                }
            }

            if (whitelistChanged)
            {
                _ = SaveWhitelistToDiskAsync();
            }
        }

        private const long MaxWhitelistFileBytes = 1024 * 1024;
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
        private void LoadWhitelistFromDisk()
        {
            try
            {
                if (!File.Exists(_whitelistFilePath)) return;

                string? dir = Path.GetDirectoryName(_whitelistFilePath);
                if (string.IsNullOrEmpty(dir) ||
                    !IsAdminOnlyLocation(dir, isDirectory: true) ||
                    !IsAdminOnlyLocation(_whitelistFilePath, isDirectory: false))
                {
                    _logger.LogCritical("Refusing to load whitelist from {path}: file or directory is not exclusively owned/writable by Administrators or SYSTEM. Starting with an empty dynamic whitelist.", _whitelistFilePath);
                    return;
                }

                if (new FileInfo(_whitelistFilePath).Length > MaxWhitelistFileBytes)
                {
                    _logger.LogError("Refusing to load whitelist from {path}: file exceeds {max} bytes.", _whitelistFilePath, MaxWhitelistFileBytes);
                    return;
                }

                List<string?>? rawEntries;
                try
                {
                    rawEntries = JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(_whitelistFilePath));
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Whitelist file {path} is not a valid JSON array of strings; starting with an empty dynamic whitelist.", _whitelistFilePath);
                    return;
                }

                if (rawEntries == null) return;

                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int invalid = 0, duplicates = 0;

                foreach (var raw in rawEntries)
                {
                    if (unique.Count >= MaxWhitelistEntries)
                    {
                        _logger.LogWarning("Whitelist file has more than {max} entries; ignoring the rest.", MaxWhitelistEntries);
                        break;
                    }

                    if (!TryNormalizeWhitelistEntry(raw, out string entry)) { invalid++; continue; }
                    if (!unique.Add(entry)) { duplicates++; }
                }

                foreach (var entry in unique)
                    _whitelist.TryAdd(entry, 1);

                _logger.LogInformation("Loaded {count} whitelist entries from {path} ({dupes} duplicates and {invalid} invalid entries skipped).",
                    unique.Count, _whitelistFilePath, duplicates, invalid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load persisted whitelist from {path}; starting with an empty dynamic whitelist.", _whitelistFilePath);
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private bool IsAdminOnlyLocation(string path, bool isDirectory)
        {
            try
            {
                var admins = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    _logger.LogWarning("{path} is a reparse point; treating as untrusted.", path);
                    return false;
                }

                System.Security.AccessControl.FileSystemSecurity security = isDirectory
                    ? new DirectoryInfo(path).GetAccessControl()
                    : new FileInfo(path).GetAccessControl();

                var owner = security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))
                    as System.Security.Principal.SecurityIdentifier;
                if (owner == null || !(owner.Equals(admins) || owner.Equals(system)))
                {
                    _logger.LogWarning("{path} is owned by {owner}, not Administrators/SYSTEM.", path, owner?.Value ?? "unknown");
                    return false;
                }

                const System.Security.AccessControl.FileSystemRights genericWrite = (System.Security.AccessControl.FileSystemRights)0x40000000;
                const System.Security.AccessControl.FileSystemRights genericAll = (System.Security.AccessControl.FileSystemRights)0x10000000;

                const System.Security.AccessControl.FileSystemRights writeish =
                    genericWrite |
                    genericAll |
                    System.Security.AccessControl.FileSystemRights.WriteData |
                    System.Security.AccessControl.FileSystemRights.AppendData |
                    System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes |
                    System.Security.AccessControl.FileSystemRights.WriteAttributes |
                    System.Security.AccessControl.FileSystemRights.Delete |
                    System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles |
                    System.Security.AccessControl.FileSystemRights.ChangePermissions |
                    System.Security.AccessControl.FileSystemRights.TakeOwnership;

                foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                    security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;

                    var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
                    if (sid.Equals(admins) || sid.Equals(system)) continue;

                    if ((rule.FileSystemRights & writeish) != 0)
                    {
                        _logger.LogWarning("{path} grants write-capable access to {sid}.", path, sid.Value);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not verify ownership/ACL of {path}; treating as untrusted.", path);
                return false;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task SaveWhitelistToDiskAsync()
        {
            await _whitelistFileLock.WaitAsync();
            try
            {
                string? dir = Path.GetDirectoryName(_whitelistFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);

                    try
                    {
                        SecureDirectoryToAdminsAndSystem(dir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to lock down ACLs on {dir}.", dir);
                    }

                    if (!IsAdminOnlyLocation(dir, isDirectory: true))
                    {
                        _logger.LogCritical("Not persisting whitelist: {dir} is not exclusively controlled by Administrators/SYSTEM.", dir);
                        return;
                    }
                }

                string tempPath = _whitelistFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_whitelist.Keys.ToList()));
                try
                {
                    await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        bufferSize: 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    {
                        await fs.WriteAsync(payload);
                        await fs.FlushAsync();
                    }
                    File.Move(tempPath, _whitelistFilePath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tempPath); } catch { }
                    throw;
                }
            }
            // Ensure proper disposal and error handling on file cleanup:
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist whitelist to disk.");
            }
            finally
            {
                _whitelistFileLock.Release();
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void SecureDirectoryToAdminsAndSystem(string dir)
        {
            var security = new System.Security.AccessControl.DirectorySecurity();

            security.SetAccessRuleProtection(true, false);

            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

            foreach (var sid in new[] { admins, system })
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            new DirectoryInfo(dir).SetAccessControl(security);
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
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId != 0");
                    using var objects = searcher.Get();

                    foreach (ManagementObject obj in objects.Cast<ManagementObject>())
                    {
                        if (obj["ProcessId"] is uint pid && obj["Name"] is string name && !string.IsNullOrEmpty(name))
                        {
                            _serviceNameCache[(int)pid] = name;
                        }
                    }

                    _lastServiceCacheRefreshUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh PID->service name cache via WMI.");
                }
            }
        }

        private bool IsTrustedExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(fullPath)) return false;

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };

            bool underTrustedRoot = roots.Any(root =>
            {
                if (string.IsNullOrEmpty(root)) return false;
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.StartsWith(
                        normalizedRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
            });

            if (!underTrustedRoot) return false;

            return IsAuthenticodeSigned(fullPath);
        }

        private bool IsAuthenticodeSigned(string fullPath)
        {
            try
            {
#if NET9_0_OR_GREATER
                using var signer = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(fullPath);
#else
                using var signer = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromSignedFile(fullPath);
#endif
                using var chain = new System.Security.Cryptography.X509Certificates.X509Chain
                {
                    ChainPolicy =
                    {
                        RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
                        VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.NoFlag
                    }
                };
                return chain.Build(new System.Security.Cryptography.X509Certificates.X509Certificate2(signer));
            }
            catch
            {
                return false;
            }
        }

        private async Task RestartLeakingServiceAsync(string fallbackServiceName, int processId, int socketCount, DateTime expectedStartTime)
        {
            try
            {
                if (!TryVerifySamePid(processId, expectedStartTime, out Process? proc))
                {
                    _logger.LogInformation("PID {pid} no longer matches the process that triggered the restart; skipping.", processId);
                    return;
                }

                string? actualServiceName = GetServiceNameFromPidCached(processId);
                string? scmServiceName = IsValidServiceName(actualServiceName) ? actualServiceName : null;
                string serviceNameToUse = scmServiceName ?? fallbackServiceName;

                _logger.LogWarning("Force killing leaking process/service {serviceName} (PID {pid})...", serviceNameToUse, processId);

                if (scmServiceName != null)
                {
                    ExecuteSafeCommand("sc.exe", new[] { "stop", scmServiceName }, suppressErrors: true);
                }

                if (!TryVerifySamePid(processId, expectedStartTime, out _))
                {
                    _logger.LogInformation("PID {pid} was recycled before taskkill could run; aborting to avoid killing an unrelated process.", processId);
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

                if (scmServiceName == null)
                {
                    _logger.LogInformation("PID {pid} ({name}) is not a registered Windows service; it was terminated and will not be relaunched.", processId, serviceNameToUse);
                }
                else if (await TryStartServiceAsync(scmServiceName))
                {
                    _logger.LogInformation("Service {serviceName} is running again after the restart.", scmServiceName);
                }
                else
                {
                    _logger.LogWarning("Service {serviceName} was not confirmed running after the restart attempt. " +
                        "Standalone executable relaunch is disabled by design; manual intervention may be required.", scmServiceName);
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

            foreach (char c in name)
            {
                if (char.IsControl(c) || c == '/' || c == '\\' || c == '"') return false;
            }
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
            catch
            {
                return false;
            }
        }

        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
        private const int ServiceStateStopped = 1;
        private const int ServiceStateRunning = 4;
        private static readonly TimeSpan ScCommandTimeout = TimeSpan.FromSeconds(30);
        private static readonly System.Text.RegularExpressions.Regex ScStateRegex =
            new(@"STATE\s*:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private async Task<bool> TryStartServiceAsync(string serviceName)
        {
            var start = await RunScAsync("start", serviceName);
            if (start == null) return false; 

            var (startExit, startOutput) = start.Value;

            if (startExit != 0 && startExit != ERROR_SERVICE_ALREADY_RUNNING)
            {
                _logger.LogWarning("'sc start {service}' failed with exit code {code}: {output}", serviceName, startExit, startOutput);
                return false;
            }

            const int maxPolls = 15;
            int? lastState = null;

            for (int i = 0; i < maxPolls; i++)
            {
                var query = await RunScAsync("query", serviceName);
                if (query != null)
                {
                    var (queryExit, queryOutput) = query.Value;
                    if (queryExit != 0)
                    {
                        _logger.LogWarning("'sc query {service}' failed with exit code {code}: {output}", serviceName, queryExit, queryOutput);
                        return false;
                    }

                    var match = ScStateRegex.Match(queryOutput);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out int state))
                    {
                        _logger.LogWarning("Start of {service} was accepted (sc start exit code {code}) but its state could not be read from 'sc query', so it is not confirmed running.", serviceName, startExit);
                        return false;
                    }

                    lastState = state;

                    if (state == ServiceStateRunning) return true;

                    if (state == ServiceStateStopped)
                    {
                        _logger.LogWarning("Service {service} went back to STOPPED right after start was accepted (it likely failed during startup). sc query output: {output}", serviceName, queryOutput);
                        return false;
                    }
                }

                await Task.Delay(2000);
            }

            _logger.LogWarning("Service {service} did not reach RUNNING within {seconds}s (last observed state code: {state}).", serviceName, maxPolls * 2, lastState?.ToString() ?? "unknown");
            return false;
        }

        private async Task<(int ExitCode, string Output)?> RunScAsync(params string[] args)
        {
            string argText = string.Join(' ', args);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SystemExe("sc.exe"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _logger.LogError("sc.exe {args} could not be started.", argText);
                    return null;
                }

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(ScCommandTimeout);
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    _logger.LogWarning("sc.exe {args} did not finish within {seconds}s and was terminated.", argText, ScCommandTimeout.TotalSeconds);
                    return null;
                }

                string output = ((await stdoutTask) + (await stderrTask)).Trim();
                return (proc.ExitCode, output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run sc.exe {args}.", argText);
                return null;
            }
        }

        private static string SystemExe(string fileName) => Path.Combine(Environment.SystemDirectory, fileName);

        private void ExecuteSafeCommand(string filename, string[] args, bool suppressErrors = false)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = SystemExe(filename), UseShellExecute = false, CreateNoWindow = true };
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
        public string BuildMarker { get; set; } = string.Empty;
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