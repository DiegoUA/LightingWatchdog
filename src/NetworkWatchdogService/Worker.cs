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

        // Persist dynamic whitelist edits (made over the IPC pipe) to disk so
        // they survive a service restart instead of silently reverting to
        // whatever was compiled/configured in.
        private readonly string _whitelistFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "whitelist.json");
        private readonly SemaphoreSlim _whitelistFileLock = new(1, 1);

        private volatile int _globalMaxTcpConnections = 1500;

        // Serializes writes to _pidConnectionCounts between the live ETW
        // increment/decrement stream and the periodic native-table baseline
        // resync, so a resync can never clobber an in-flight ETW update.
        private readonly object _connectionCountsLock = new();

        private readonly record struct AfdEvent(int ProcessId, bool IsConnect);

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting NetworkWatchdogService Worker...");

            LoadWhitelistFromDisk();
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

                                // Read the name exactly once, before anything is marked as
                                // "restart in progress", so a process that exits right here
                                // can't leave a stale _activeRestarts entry behind.
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
                            IntPtr rowPtr = tcpTablePtr + 4; // Skip dwNumEntries

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
                            // Some other failure (permissions, etc.) - bufferSize was
                            // not updated meaningfully, so don't loop forever on it.
                            return;
                        }

                        // The table grew between the sizing call and the fetch call
                        // (or between retries). bufferSize was updated by the API
                        // with the required size - loop and allocate again.
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(tcpTablePtr);
                    }
                }

                _logger.LogWarning("GetExtendedTcpTable (IPv{ver}) kept returning ERROR_INSUFFICIENT_BUFFER after {attempts} attempts; skipping this cycle.", ipVersion == AF_INET ? 4 : 6, MaxAttempts);
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

        /// <summary>
        /// Drops process/service-name and start-time cache entries for PIDs
        /// that are no longer tracked in _pidConnectionCounts (e.g. short-lived
        /// processes that never crossed a connection threshold), preventing
        /// unbounded cache growth over the service's lifetime.
        /// </summary>
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
        private async Task StartNamedPipeServerAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Initializing Named Pipe IPC Server: \\\\.\\pipe\\NetworkWatchdogPipe");

            var pipeSecurity = new PipeSecurity();

            // Strip any inherited/default ACEs so the DACL is *only* what we add
            // below, even if the host wrapper (or a non-standard hosting
            // environment) would otherwise contribute default access rules.
            pipeSecurity.SetAccessRuleProtection(true, false);

            var sidAdmin = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            // Locked down completely to BuiltinAdministratorsSid to prevent LPE/DoS
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidAdmin,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

            // The accept loop hands each connected instance to a handler task and
            // immediately creates the next instance while the previous one is
            // still alive. CreateNamedPipe checks FILE_CREATE_PIPE_INSTANCE against
            // the existing instance's DACL, and PipeAccessRights.ReadWrite does not
            // include it - so with an admins-only ReadWrite ACL the second instance
            // would fail with "access denied". Grant that one right to the
            // service's own identity only (clients don't need it).
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
                        "NetworkWatchdogPipe",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);

                    await server.WaitForConnectionAsync(stoppingToken);

                    // Hand the connected client off to its own task immediately
                    // so the accept loop can go straight back to listening.
                    // A slow/stalled client no longer blocks every other
                    // IPC caller (telemetry pollers, config updaters, etc.).
                    var connectedServer = server;
                    server = null; // ownership transferred to the handler task
                    _ = Task.Run(() => HandlePipeClientAsync(connectedServer, stoppingToken), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Named Pipe accept-loop error.");
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

        private async Task HandlePipeClientAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
        {
            using (server)
            using (var reader = new StreamReader(server, Encoding.UTF8))
            using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
            // Per-connection deadline: each client now has its own task and pipe
            // instance, so without this a client that connects and goes quiet would
            // hold an instance (of a finite pool) indefinitely.
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
                        else if (line.StartsWith("UPDATE_CONFIG:"))
                        {
                            string jsonPayload = line.Substring("UPDATE_CONFIG:".Length);
                            var updateCmd = JsonSerializer.Deserialize<ConfigUpdateCommand>(jsonPayload);
                            if (updateCmd != null) ApplyConfigUpdate(updateCmd);
                            await writer.WriteLineAsync("OK".AsMemory(), token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!stoppingToken.IsCancellationRequested)
                        _logger.LogWarning("IPC client did not complete within {seconds}s; dropping connection.", IpcClientTimeout.TotalSeconds);
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning("IPC client rejected: {reason}", ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Named Pipe client-handler error.");
                }
            }
        }

        /// <summary>
        /// Reads one line but never buffers more than maxChars, so a client that
        /// streams data without ever sending a newline can't grow the service's
        /// memory without bound (ReadLineAsync has no such limit).
        /// </summary>
        private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maxChars, CancellationToken token)
        {
            var sb = new StringBuilder();
            var one = new char[1];

            // StreamReader buffers internally, so single-char reads don't hit the pipe each time.
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
                // Same normalization/validation as entries loaded from disk, so
                // IPC edits and out-of-band file edits can't diverge.
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
                // Fire-and-forget: IPC handler shouldn't block the client on disk I/O,
                // but any failure is still logged inside the save method.
                _ = SaveWhitelistToDiskAsync();
            }
        }

        private const long MaxWhitelistFileBytes = 1024 * 1024;
        private const int MaxWhitelistEntries = 10_000;
        private const int MaxWhitelistEntryLength = 260;

        /// <summary>
        /// Normalizes a whitelist entry the same way everywhere (trim, strip
        /// any directory part and extension) and rejects empty/oversized values.
        /// </summary>
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

                // Refuse to trust the persisted file unless both it and its
                // directory are owned by, and writable only by, Administrators
                // or SYSTEM. This covers the window before the service first
                // locked the directory down, and a directory pre-created by a
                // standard user. Failing closed means an empty dynamic
                // whitelist, i.e. more processes are monitored, not fewer.
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

                // Validate + case-insensitively de-duplicate before touching the
                // live whitelist, so out-of-band edits (hand-edited casing,
                // "foo.exe" vs "FOO", nulls, blanks, junk) can't leave it in an
                // inconsistent state.
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

        /// <summary>
        /// True only if the path is not a reparse point, is owned by
        /// BuiltinAdministrators or LocalSystem, and no other principal has an
        /// Allow rule granting write/delete/permission-change rights.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private bool IsAdminOnlyLocation(string path, bool isDirectory)
        {
            try
            {
                var admins = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

                // A junction/symlink could redirect us to attacker-controlled content.
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

                // Inheritable ACEs and hand-built ACLs can carry *unmapped* generic
                // rights (GENERIC_WRITE 0x40000000, GENERIC_ALL 0x10000000), which the
                // FileSystemRights enum has no names for and which would slip past a
                // check that only looks at the specific bits. (CreateFiles and
                // CreateDirectories are the same bits as WriteData/AppendData, so
                // those are already covered below.)
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

                    // Lock the ACL down every save, not just on first creation:
                    // this is self-healing if someone (or some other install
                    // step) loosens the folder's permissions later, and the
                    // call is cheap compared to the disk write that follows.
                    try
                    {
                        SecureDirectoryToAdminsAndSystem(dir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to lock down ACLs on {dir}.", dir);
                    }

                    // Verify the resulting state rather than assuming it. This also
                    // catches a directory pre-created by a standard user: they stay
                    // the owner (and can always rewrite the DACL), so we refuse to
                    // write the whitelist there instead of leaving a tamperable file.
                    if (!IsAdminOnlyLocation(dir, isDirectory: true))
                    {
                        _logger.LogCritical("Not persisting whitelist: {dir} is not exclusively controlled by Administrators/SYSTEM.", dir);
                        return;
                    }
                }

                // Write to a temp file and swap it in, so a crash/power-loss
                // mid-write can't leave a truncated/corrupt whitelist.json
                // that fails to parse on the next startup.
                //
                // The temp name is unpredictable and opened with FileMode.CreateNew
                // (CREATE_NEW), which fails if *anything* already exists at that
                // path - a file, hard link, or symlink - so nothing pre-planted can
                // redirect the write. (The directory is also verified admin-only
                // above; this removes the reliance on that check staying true
                // between the check and the write.)
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
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist whitelist to {path}. In-memory whitelist is unaffected but will revert on restart until this succeeds.", _whitelistFilePath);
            }
            finally
            {
                _whitelistFileLock.Release();
            }
        }

        /// <summary>
        /// Strips inherited ACEs from the whitelist directory and grants
        /// FullControl only to BuiltinAdministrators and LocalSystem, so a
        /// standard user cannot edit whitelist.json to hide a leaking process
        /// from detection even if %ProgramData% itself is user-writable.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void SecureDirectoryToAdminsAndSystem(string dir)
        {
            var security = new System.Security.AccessControl.DirectorySecurity();

            // true, false = protect from inheritance, don't preserve the
            // inherited rules we're protecting against.
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

        /// <summary>
        /// Issues one Win32_Service WMI query covering every PID-owning
        /// service, instead of a separate WMI round-trip per PID. WMI/COM
        /// query overhead is fixed per call, so batching turns O(n) queries
        /// during a leak burst into O(1) on a TTL-bounded cadence.
        /// </summary>
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
                // Resolve ".." segments, symlinks-in-path, relative fragments, etc.
                // before doing any prefix comparison.
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
                // Require a directory-separator boundary after the root so
                // "C:\Program FilesEvil\x.exe" does not match "C:\Program Files".
                return fullPath.StartsWith(
                        normalizedRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
            });

            if (!underTrustedRoot) return false;

            // Being under a "trusted" directory is not sufficient on its own -
            // some subfolders under ProgramData/Program Files are writable by
            // standard users. Require a valid, chain-trusted Authenticode
            // signature before we ever relaunch a binary as SYSTEM.
            return IsAuthenticodeSigned(fullPath);
        }

        private bool IsAuthenticodeSigned(string fullPath)
        {
            try
            {
                // X509CertificateLoader is the recommended API on .NET 9+
                // (CreateFromSignedFile is marked obsolete there for security
                // reasons around ambiguous cert-vs-PKCS#7 parsing), but it
                // doesn't exist on older runtimes - branch on target framework
                // so this builds cleanly whichever SDK you're compiling against.
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
                // No signature, malformed signature, or chain failed to build.
                return false;
            }
        }

        private async Task RestartLeakingServiceAsync(string fallbackServiceName, int processId, int socketCount, DateTime expectedStartTime)
        {
            try
            {
                // Re-verify identity immediately before acting: time has passed
                // since the caller last checked, and PIDs are recycled by the OS.
                if (!TryVerifySamePid(processId, expectedStartTime, out Process? proc))
                {
                    _logger.LogInformation("PID {pid} no longer matches the process that triggered the restart; skipping.", processId);
                    return;
                }

                // Only a name the SCM itself reported for this PID may be handed to
                // sc.exe. The process *image name* is chosen by whoever launched the
                // process, so using it as a service name would let anyone stop/start
                // an arbitrary service just by naming a leaking binary after it.
                // It is kept for logging/telemetry only.
                string? actualServiceName = GetServiceNameFromPidCached(processId);
                string? scmServiceName = IsValidServiceName(actualServiceName) ? actualServiceName : null;
                string serviceNameToUse = scmServiceName ?? fallbackServiceName;

                _logger.LogWarning("Force killing leaking process/service {serviceName} (PID {pid})...", serviceNameToUse, processId);

                if (scmServiceName != null)
                {
                    ExecuteSafeCommand("sc.exe", new[] { "stop", scmServiceName }, suppressErrors: true);
                }

                // Final identity re-check right before the destructive taskkill call.
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

                // Restart strictly through the Service Control Manager. We
                // deliberately do NOT re-execute a standalone binary with
                // captured command-line arguments: an attacker able to
                // influence a process's command line (or place a file in a
                // "trusted" but user-writable subdirectory) could otherwise
                // get arbitrary arguments/code executed under this service's
                // SYSTEM identity.
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
                    // The specific reason (exit code, state, timeout) was logged by TryStartServiceAsync.
                    _logger.LogWarning("Service {serviceName} was not confirmed running after the restart attempt. " +
                        "Standalone executable relaunch is disabled by design; manual intervention may be required.", scmServiceName);
                }
            }
            finally
            {
                _activeRestarts.TryRemove(processId, out _);
            }
        }

        /// <summary>
        /// Defense-in-depth sanity check on a service short name before it is
        /// passed to sc.exe. Deliberately not "alphanumeric only": service names
        /// may legally contain spaces and punctuation, and a strict allow-list
        /// would silently stop us restarting those services. Rejects what could
        /// confuse sc.exe's own parsing (path separators / a leading option or
        /// server prefix), quotes, control characters, and over-long names.
        /// </summary>
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

        /// <summary>
        /// Confirms the PID still refers to the same process instance we
        /// observed earlier (same start time), guarding against PID reuse
        /// between the leak-detection check and any destructive action.
        /// </summary>
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

        /// <summary>
        /// Starts a service via sc.exe and then confirms it actually reached
        /// RUNNING, logging the specific reason whenever it did not. Returns
        /// true only for a confirmed-running service.
        /// </summary>
        private async Task<bool> TryStartServiceAsync(string serviceName)
        {
            // No separate existence probe: 'sc start' itself reports a missing
            // service (1060), access problems (5), a disabled service (1058), etc.
            var start = await RunScAsync("start", serviceName);
            if (start == null) return false; // launch/timeout failure already logged

            var (startExit, startOutput) = start.Value;

            // 0    = start request accepted (service is START_PENDING or RUNNING).
            // 1056 = already running. Expected when the service's own SCM recovery
            //        actions restarted it during our wait, so not a failure.
            if (startExit != 0 && startExit != ERROR_SERVICE_ALREADY_RUNNING)
            {
                _logger.LogWarning("'sc start {service}' failed with exit code {code}: {output}", serviceName, startExit, startOutput);
                return false;
            }

            // Accepted is not the same as running - poll the real state.
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
                    // START_PENDING (or another transitional state): keep waiting.
                }

                await Task.Delay(2000);
            }

            _logger.LogWarning("Service {service} did not reach RUNNING within {seconds}s (last observed state code: {state}).", serviceName, maxPolls * 2, lastState?.ToString() ?? "unknown");
            return false;
        }

        /// <summary>
        /// Runs sc.exe (from the system directory) and returns its exit code and
        /// combined output, or null if it could not be launched or timed out.
        /// </summary>
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

                // Drain both streams while waiting; waiting first on a redirected
                // stream that fills its pipe buffer would hang the child and us.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(ScCommandTimeout);
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
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

        /// <summary>
        /// Full path to a system utility. Launching a bare "sc.exe" as SYSTEM
        /// lets CreateProcess look in the service's own directory first, so a
        /// planted binary there would run with the service's privileges.
        /// </summary>
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