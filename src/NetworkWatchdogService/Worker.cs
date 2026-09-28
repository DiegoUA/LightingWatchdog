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

                                string cleanName = Path.GetFileNameWithoutExtension(proc.ProcessName);
                                if (_whitelist.ContainsKey(cleanName) || _whitelist.ContainsKey(proc.ProcessName)) 
                                    continue;

                                _logger.LogWarning("CRITICAL: Socket leak breach ({count} > {max}) in PID {pid}!", currentConnections, _globalMaxTcpConnections, proc.Id);
                                
                                _activeRestarts.TryAdd(pid, DateTime.UtcNow);
                                
                                _ = RestartLeakingServiceAsync(proc.ProcessName, proc.Id, currentConnections, currentStartTime);
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
            var sidAdmin = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            // Locked down completely to BuiltinAdministratorsSid to prevent LPE/DoS
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                sidAdmin,
                PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));

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

        private async Task HandlePipeClientAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
        {
            using (server)
            using (var reader = new StreamReader(server, Encoding.UTF8))
            using (var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true })
            {
                try
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
                catch (OperationCanceledException)
                {
                    // Service shutting down or client-specific timeout - ignore.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Named Pipe client-handler error.");
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
            bool whitelistChanged = false;

            if (cmd.NewGlobalThreshold.HasValue && cmd.NewGlobalThreshold.Value >= 200 && cmd.NewGlobalThreshold.Value <= 10000)
            {
                _globalMaxTcpConnections = cmd.NewGlobalThreshold.Value;
                _logger.LogInformation("IPC: Updated global threshold to {val}", _globalMaxTcpConnections);
            }

            if (!string.IsNullOrWhiteSpace(cmd.AddWhitelist))
            {
                string entry = Path.GetFileNameWithoutExtension(cmd.AddWhitelist.Trim());
                if (_whitelist.TryAdd(entry, 1))
                {
                    whitelistChanged = true;
                    _logger.LogInformation("IPC: Added {name} to whitelist.", entry);
                }
            }
            if (!string.IsNullOrWhiteSpace(cmd.RemoveWhitelist))
            {
                string entry = Path.GetFileNameWithoutExtension(cmd.RemoveWhitelist.Trim());
                if (_whitelist.TryRemove(entry, out _))
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

        private void LoadWhitelistFromDisk()
        {
            try
            {
                if (!File.Exists(_whitelistFilePath)) return;

                string json = File.ReadAllText(_whitelistFilePath);
                var entries = JsonSerializer.Deserialize<List<string>>(json);
                if (entries == null) return;

                foreach (var entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry))
                        _whitelist.TryAdd(entry, 1);
                }

                _logger.LogInformation("Loaded {count} whitelist entries from {path}.", entries.Count, _whitelistFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load persisted whitelist from {path}; starting with an empty dynamic whitelist.", _whitelistFilePath);
            }
        }

        private async Task SaveWhitelistToDiskAsync()
        {
            await _whitelistFileLock.WaitAsync();
            try
            {
                string? dir = Path.GetDirectoryName(_whitelistFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    bool dirExisted = Directory.Exists(dir);
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
                        _logger.LogError(ex, "Failed to lock down ACLs on {dir}; whitelist directory may be writable by non-admin users.", dir);
                        if (!dirExisted)
                        {
                            // We just created an unsecured directory and couldn't
                            // secure it - refuse to write the whitelist into it
                            // rather than silently leaving a tamperable file.
                            return;
                        }
                    }
                }

                // Write to a temp file and swap it in, so a crash/power-loss
                // mid-write can't leave a truncated/corrupt whitelist.json
                // that fails to parse on the next startup.
                string tempPath = _whitelistFilePath + ".tmp";
                string json = JsonSerializer.Serialize(_whitelist.Keys.ToList());
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, _whitelistFilePath, overwrite: true);
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

                string? actualServiceName = GetServiceNameFromPidCached(processId);
                string serviceNameToUse = !string.IsNullOrEmpty(actualServiceName) ? actualServiceName : fallbackServiceName;

                _logger.LogWarning("Force killing leaking process/service {serviceName} (PID {pid})...", serviceNameToUse, processId);

                ExecuteSafeCommand("sc.exe", new[] { "stop", serviceNameToUse }, suppressErrors: true);

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
                bool startedAsService = TryStartService(serviceNameToUse);
                if (!startedAsService)
                {
                    _logger.LogWarning("Service {serviceName} could not be restarted via the Service Control Manager. " +
                        "Standalone executable relaunch is disabled by design; manual intervention is required.", serviceNameToUse);
                }
            }
            finally
            {
                _activeRestarts.TryRemove(processId, out _);
            }
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