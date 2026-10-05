using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Net.Sockets;
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
using Microsoft.Extensions.Options;
using NetworkWatchdogService.Models;

namespace NetworkWatchdogService
{
    public class Worker : BackgroundService
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        // --- Windows Restart Manager (rstrtmgr.dll) ---
        //
        // HIGH-RISK, UNVERIFIED NATIVE INTEROP. This has NOT been compiled or run
        // against a real Windows machine from this environment - it is written
        // against the widely-published, long-standing reference shape for this
        // API (the same struct layout/signatures reproduced across numerous
        // vetted samples), but has not been tested here. Before relying on this
        // in production, verify on a real box that:
        //   (a) it builds without marshaling warnings,
        //   (b) RmGetList returns the PIDs you expect for a known locked file
        //       (e.g. run it against a file you've deliberately left open in
        //       another process), and
        //   (c) nothing it returns gets killed incorrectly.
        // This is exactly the class of bug (silently wrong, not crashing) this
        // codebase has already been burned by twice. It is deliberately wired as
        // an ADDITIVE safety layer, not a replacement for the existing taskkill
        // /T of the primary target: if the RM query fails for any reason, the
        // code logs and continues with just the original single-target kill
        // rather than blocking or crashing the restart.
        private const int CCH_RM_SESSION_KEY = 32;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle,
            uint nFiles, string[]? rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
            ref uint lpdwRebootReasons);

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

        // Persisted separately from whitelist.json (same directory, same ACL
        // lockdown, same atomic-write pattern) rather than rewriting
        // appsettings.json in place at runtime - mutating the deployed config
        // file risks fighting an installer/GPO/config-management tool that
        // also owns it. This file holds only the small set of values the
        // TrayApp's "Save Configuration" button is meant to persist; it is an
        // override layered on top of appsettings.json at startup, not a
        // replacement for it.
        private readonly string _runtimeOverridesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "runtime-overrides.json");
        private readonly SemaphoreSlim _runtimeOverridesFileLock = new(1, 1);

        // Fallback only - overwritten from WatchdogConfig.GlobalMaxTcpConnections
        // in the constructor. Kept as a sane default in case config binding ever
        // fails to populate (e.g. missing section).
        private volatile int _globalMaxTcpConnections = 1000;

        // Dashboard/telemetry display filter only - processes at or below this
        // are hidden from the Live Processes view. Detection and restart logic
        // is unaffected: every tracked PID is still checked against its
        // resolved threshold regardless of this value.
        private const int TelemetryDisplayThreshold = 30;
        private readonly object _connectionCountsLock = new();

        private readonly record struct AfdEvent(int ProcessId, bool IsConnect);

        // Bumped every time this file changes, logged loudly at startup and
        // exposed in telemetry, so you can tell at a glance - from the
        // service's own log or the dashboard itself - whether the process
        // that's actually running matches the source you just built.
        private const string BuildMarker = "2026-10-03-config1";

        private readonly WatchdogConfig _config;

        // Case-insensitive lookup from a process name (either a MonitoredServices
        // ServiceName or any name in its ProcessTree) to that service's config
        // entry, built once at startup. Used to resolve per-service threshold
        // overrides and the EnableRestart toggle.
        private readonly Dictionary<string, MonitoredService> _serviceConfigByProcessName;

        // Cheap pre-filter for the 5-second detection loop: the lowest threshold
        // any process could possibly be held to (global or any per-service
        // override, whichever is smaller). A PID below this can never breach
        // any applicable threshold, so its name/config never needs resolving -
        // this keeps the hot path cheap even with MonitorAllProcesses=true and
        // CIM reporting counts for every process on the box.
        private readonly int _minPossibleThreshold;

        // Cooldown: minimum time between restart *attempts* of the same service
        // name (not PID - PIDs change every restart). Keyed by the clean process
        // name actually used to trigger the restart.
        private readonly ConcurrentDictionary<string, DateTime> _lastRestartAttempt = new(StringComparer.OrdinalIgnoreCase);

        // Quarantine: rolling history of restart-attempt timestamps per service
        // name, used to detect "restarted too many times too recently" without
        // any separate sticky "quarantined" flag - see IsQuarantined for why.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _restartHistory = new(StringComparer.OrdinalIgnoreCase);

        public Worker(ILogger<Worker> logger, IOptions<WatchdogConfig> options)
        {
            _logger = logger;
            _config = options.Value ?? new WatchdogConfig();
            _globalMaxTcpConnections = _config.GlobalMaxTcpConnections;

            _serviceConfigByProcessName = new Dictionary<string, MonitoredService>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in _config.MonitoredServices)
            {
                if (!string.IsNullOrWhiteSpace(svc.ServiceName))
                    _serviceConfigByProcessName[svc.ServiceName] = svc;
                foreach (var name in svc.ProcessTree)
                    if (!string.IsNullOrWhiteSpace(name))
                        _serviceConfigByProcessName[name] = svc;
            }

            _minPossibleThreshold = _config.MonitoredServices.Count > 0
                ? Math.Min(_config.GlobalMaxTcpConnections, _config.MonitoredServices.Min(s => s.MaxTcpConnections))
                : _config.GlobalMaxTcpConnections;
        }

        /// <summary>
        /// Resolves the connection threshold and restart permission that apply
        /// to a given process name: a per-service override from MonitoredServices
        /// if one matches (by ServiceName or ProcessTree membership), else the
        /// global default with restarts enabled.
        /// </summary>
        private (int Threshold, bool RestartEnabled, string? ServiceGroupName) ResolveThresholdForProcess(string cleanName)
        {
            if (_serviceConfigByProcessName.TryGetValue(cleanName, out var svc))
                return (svc.MaxTcpConnections, svc.EnableRestart, svc.ServiceName);
            return (_globalMaxTcpConnections, true, null);
        }

        private string GetOrCacheProcessName(int pid, Process proc)
        {
            if (_processNameCache.TryGetValue(pid, out string? cached)) return cached;
            string name = proc.ProcessName;
            _processNameCache[pid] = name;
            return name;
        }

        // --- Syslog (RFC 3164, UDP) ---
        //
        // WatchdogConfig.Syslog existed as a data holder with nothing reading it;
        // this is that missing consumer. Deliberately UDP and fire-and-forget: a
        // syslog server being down or unreachable must never block or throw from
        // detection/restart logic, so every failure path here only logs locally
        // and continues. Deliberately NOT a mirror of every log line - only the
        // handful of events below that represent a meaningful state change
        // someone monitoring centrally would actually want to see, to avoid
        // turning this into log spam at your real connection-count volumes.
        private UdpClient? _syslogClient;
        private readonly object _syslogInitLock = new();

        private enum SyslogSeverity { Emergency = 0, Alert = 1, Critical = 2, Error = 3, Warning = 4, Notice = 5, Informational = 6, Debug = 7 }

        private void EnsureSyslogClient()
        {
            if (!_config.Syslog.Enabled || _syslogClient != null) return;

            lock (_syslogInitLock)
            {
                if (_syslogClient != null) return;
                try
                {
                    var client = new UdpClient();
                    client.Connect(_config.Syslog.ServerIp, _config.Syslog.Port);
                    _syslogClient = client;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize syslog UDP client for {ip}:{port}; syslog forwarding disabled for this session.",
                        _config.Syslog.ServerIp, _config.Syslog.Port);
                }
            }
        }

        /// <summary>
        /// Sends one RFC 3164 formatted line via UDP to the configured syslog
        /// server. Facility is fixed at local0 (16), which is conventional for
        /// an application-specific daemon with no more specific facility of its
        /// own. No-op (and cheap) when Syslog.Enabled is false.
        /// </summary>
        private void SendSyslog(SyslogSeverity severity, string message)
        {
            if (!_config.Syslog.Enabled) return;
            EnsureSyslogClient();
            if (_syslogClient == null) return;

            try
            {
                const int facility = 16; // local0
                int priority = facility * 8 + (int)severity;

                // RFC 3164 wants the day-of-month space-padded (not zero-padded)
                // to two characters, e.g. "Oct  4" not "Oct 04".
                string timestamp = DateTime.Now.ToString("MMM dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                if (timestamp.Length > 4 && timestamp[4] == '0')
                {
                    var chars = timestamp.ToCharArray();
                    chars[4] = ' ';
                    timestamp = new string(chars);
                }

                string line = $"<{priority}>{timestamp} {Environment.MachineName} NetworkWatchdogService: {message}";
                byte[] bytes = Encoding.ASCII.GetBytes(line);
                _syslogClient.Send(bytes, bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send syslog message.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogCritical("Starting NetworkWatchdogService Worker. BuildMarker={marker}", BuildMarker);
            SendSyslog(SyslogSeverity.Informational, $"NetworkWatchdogService starting (BuildMarker={BuildMarker}).");

            LoadWhitelistFromDisk();
            LoadRuntimeOverrides();
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
                    if (!_pidConnectionCounts.TryGetValue(pid, out int currentConnections)) continue;

                    // Cheap pre-filter: below the lowest threshold any process could
                    // possibly be held to, so there is nothing to resolve or check.
                    if (currentConnections <= _minPossibleThreshold) continue;

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

                        string procName = GetOrCacheProcessName(pid, proc);
                        string cleanName = Path.GetFileNameWithoutExtension(procName);

                        if (_whitelist.ContainsKey(cleanName) || _whitelist.ContainsKey(procName))
                            continue;

                        var (threshold, restartEnabled, serviceGroupName) = ResolveThresholdForProcess(cleanName);

                        // MonitorAllProcesses=false: only processes configured in
                        // MonitoredServices are tracked for restart purposes at all,
                        // regardless of how high an unconfigured process's count is.
                        if (!_config.MonitorAllProcesses && serviceGroupName == null)
                            continue;

                        if (currentConnections <= threshold) continue;

                        // The name used for cooldown/quarantine bookkeeping and logging:
                        // the configured service group name if this process belongs to
                        // one, otherwise its own clean process name. This is what makes
                        // cooldown/quarantine apply per logical service, not per PID.
                        string bookkeepingName = serviceGroupName ?? cleanName;

                        _logger.LogCritical("CRITICAL: Socket leak breach ({count} > {max}) in PID {pid} ({name})!",
                            currentConnections, threshold, proc.Id, bookkeepingName);
                        SendSyslog(SyslogSeverity.Warning, $"Socket leak breach: {bookkeepingName} (PID {proc.Id}) has {currentConnections} connections, threshold {threshold}.");

                        if (!restartEnabled)
                        {
                            _logger.LogWarning("EnableRestart is false for {name}; leak detected but not acting.", bookkeepingName);
                            continue;
                        }

                        if (IsInCooldown(bookkeepingName, out TimeSpan remaining))
                        {
                            // Not sent to syslog: this can repeat every 5s while a
                            // service sits in cooldown, which would spam a central
                            // log server with no new information each time.
                            _logger.LogInformation("{name} breached its threshold again but is still in cooldown for {remaining}s; not restarting yet.",
                                bookkeepingName, (int)remaining.TotalSeconds);
                            continue;
                        }

                        if (_config.EnableQuarantine && IsQuarantined(bookkeepingName, out int recentCount))
                        {
                            _logger.LogCritical("QUARANTINED: {name} has restarted {count} times in the last {window} minute(s) (limit {limit}). " +
                                "Not auto-restarting - this needs manual investigation.",
                                bookkeepingName, recentCount, _config.QuarantineWindowMinutes, _config.QuarantineRestartLimit);
                            SendSyslog(SyslogSeverity.Alert, $"QUARANTINED: {bookkeepingName} restarted {recentCount} times in the last {_config.QuarantineWindowMinutes} minute(s); auto-restart suspended, manual investigation needed.");
                            continue;
                        }

                        RecordRestartAttempt(bookkeepingName);
                        _activeRestarts.TryAdd(pid, DateTime.UtcNow);
                        SendSyslog(SyslogSeverity.Notice, $"Restarting {bookkeepingName} (PID {proc.Id}) due to socket leak ({currentConnections} connections).");

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

                await Task.Delay(5000, stoppingToken);
            }
        }

        /// <summary>
        /// True if this service name attempted a restart within CooldownSeconds.
        /// Distinct from quarantine: cooldown is a short, per-attempt throttle
        /// (avoid re-triggering every 5s while the previous restart is still
        /// settling); quarantine is the longer-window "this keeps happening,
        /// stop trying" circuit breaker.
        /// </summary>
        private bool IsInCooldown(string serviceName, out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            if (!_lastRestartAttempt.TryGetValue(serviceName, out DateTime last)) return false;

            var elapsed = DateTime.UtcNow - last;
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _config.CooldownSeconds));
            if (elapsed >= cooldown) return false;

            remaining = cooldown - elapsed;
            return true;
        }

        /// <summary>
        /// Rolling-window check, not a sticky flag: counts restart attempts for
        /// this service name within the last QuarantineWindowMinutes. No separate
        /// "quarantined" state is stored, so there is nothing to get stuck or to
        /// need manually clearing - once enough time passes for old attempts to
        /// age out of the window, restarts are simply allowed again on their own.
        /// </summary>
        private bool IsQuarantined(string serviceName, out int recentCount)
        {
            recentCount = 0;
            if (!_restartHistory.TryGetValue(serviceName, out var history)) return false;

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(0, _config.QuarantineWindowMinutes));
            recentCount = history.Count(t => t >= cutoff);
            return recentCount >= Math.Max(1, _config.QuarantineRestartLimit);
        }

        private void RecordRestartAttempt(string serviceName)
        {
            _lastRestartAttempt[serviceName] = DateTime.UtcNow;

            var history = _restartHistory.GetOrAdd(serviceName, _ => new ConcurrentQueue<DateTime>());
            history.Enqueue(DateTime.UtcNow);

            // Trim opportunistically so this can't grow without bound for a
            // service that restarts constantly over a long uptime.
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _config.QuarantineWindowMinutes) * 4);
            while (history.TryPeek(out DateTime oldest) && oldest < cutoff)
                history.TryDequeue(out _);
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
                // Config-driven (WatchdogConfig.WatchdogIntervalSeconds, default 15s).
                // At 60s, a leak crossing the threshold could sit undetected for up
                // to ~65s (this interval plus the 5s detection poll) before a restart
                // even triggers; 15s keeps that worst case closer to ~20s. If CIM
                // query cost at your actual connection volume turns out to be high
                // enough to matter, raise WatchdogIntervalSeconds in appsettings.json -
                // watch the resync log timestamps after deploying to confirm each
                // cycle finishes well under this interval.
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.WatchdogIntervalSeconds)), token);
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
        /// Counts TCP rows per owning PID via the same CIM provider
        /// (root\StandardCimv2:MSFT_NetTCPConnection) that Get-NetTCPConnection
        /// itself queries. This is now the authoritative source for
        /// leak detection: unlike the legacy GetExtendedTcpTable fetch, it sees
        /// "Bound" sockets - bind() with no connect/listen/close - which is
        /// exactly the pattern LightingService's leak produces. Returns null
        /// on failure so the caller can fall back rather than silently zeroing
        /// out every tracked PID.
        /// </summary>
        private Dictionary<int, int>? GetTcpConnectionCountsViaCim()
        {
            var counts = new Dictionary<int, int>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\StandardCimv2",
                    "SELECT OwningProcess FROM MSFT_NetTCPConnection");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results.Cast<ManagementObject>())
                {
                    if (obj["OwningProcess"] is uint pid)
                    {
                        counts[(int)pid] = counts.TryGetValue((int)pid, out int c) ? c + 1 : 1;
                    }
                    obj.Dispose();
                }
                return counts;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "CIM/NSI query for MSFT_NetTCPConnection failed.");
                return null;
            }
        }

        /// <summary>
        /// Logs the count this cycle's fetch saw for whichever PID(s) currently
        /// belong to a process with this name. Resolved by name every call, not
        /// cached, so it survives the process restarting (and therefore getting
        /// a new PID) without a code edit/redeploy.
        /// </summary>
        private void LogCountForWatchedProcess(Dictionary<int, int> counts, string processName, bool usedFallback)
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
                    _logger.LogCritical("Baseline resync: {name} (PID {pid}) = {val} ({source}).",
                        processName, pid,
                        counts.TryGetValue(pid, out int val) ? val.ToString() : "not present",
                        usedFallback ? "legacy GetExtendedTcpTable fallback" : "CIM/NSI");
                }
            }
            finally
            {
                foreach (var proc in matches) proc.Dispose();
            }
        }

        private void ResyncGlobalBaseline()
        {
            _logger.LogCritical("Resyncing socket baselines...");

            // CIM/NSI first - this is what actually sees LightingService's
            // leaked Bound sockets. Only fall back to the legacy IP Helper
            // table if CIM itself is unavailable, so detection degrades
            // instead of going blind.
            var counts = GetTcpConnectionCountsViaCim();
            bool usedFallback = false;

            if (counts == null)
            {
                try
                {
                    counts = GetNativeTcpConnectionCounts();
                    usedFallback = true;
                    _logger.LogCritical("CIM/NSI query failed; fell back to GetExtendedTcpTable, which will UNDERCOUNT bind()-only leaked sockets.");
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Both CIM/NSI and the legacy native TCP table fetch failed this cycle; baseline not updated.");
                    SendSyslog(SyslogSeverity.Error, $"Baseline resync failed completely this cycle (both CIM/NSI and legacy fetch failed): {ex.Message}");
                    return;
                }
            }

            // Unconditional, every cycle, regardless of threshold: proves the
            // fetch ran and shows exactly what it's seeing for the watched
            // process. Looked up by name, not a hardcoded PID - PIDs are
            // reassigned every time the process restarts.
            LogCountForWatchedProcess(counts, "LightingService", usedFallback);

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

                // Display filter only - this does NOT affect detection. Every
                // PID in _pidConnectionCounts, including ones below this, is
                // still checked against _globalMaxTcpConnections in the main
                // detection loop and can still trigger a restart; this just
                // keeps idle/low-connection processes (wininit with 2
                // connections, etc.) off the dashboard so only processes worth
                // looking at show up.
                if (kvp.Value <= TelemetryDisplayThreshold) continue;

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

            if (cmd.SaveRequested == true)
            {
                // Re-save the whitelist too, not just the threshold: harmless if
                // it's already current, and gives the user a working retry via
                // the UI if an earlier auto-save after Add/Remove ever failed.
                _ = SaveWhitelistToDiskAsync();
                _ = SaveRuntimeOverridesAsync();
                _logger.LogInformation("IPC: Save Configuration requested - persisting current threshold and whitelist.");
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
            // Config-seeded defaults (WatchdogConfig.ProcessWhitelist, e.g. chrome/
            // firefox/msedge/steam/Discord) are applied first, every startup,
            // regardless of whether the persisted whitelist.json exists yet. This
            // does NOT replace the persisted/ACL-locked whitelist mechanism below -
            // it just guarantees sane common defaults are present even on a brand
            // new install before anyone has added anything via IPC. Entries added
            // at runtime still only persist through the existing whitelist.json
            // save path, not back into appsettings.json.
            foreach (var seed in _config.ProcessWhitelist)
            {
                if (TryNormalizeWhitelistEntry(seed, out string entry))
                    _whitelist.TryAdd(entry, 1);
            }

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

        private class RuntimeOverrides
        {
            public int? GlobalMaxTcpConnections { get; set; }
        }

        /// <summary>
        /// Applies any previously-saved runtime override on top of the value
        /// bound from appsettings.json. Same ACL verification discipline as
        /// LoadWhitelistFromDisk: refuses to trust the file unless its
        /// directory is admin/SYSTEM-only, failing closed to "use the
        /// appsettings.json value" rather than trusting a tampered override.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void LoadRuntimeOverrides()
        {
            try
            {
                if (!File.Exists(_runtimeOverridesFilePath)) return;

                string? dir = Path.GetDirectoryName(_runtimeOverridesFilePath);
                if (string.IsNullOrEmpty(dir) ||
                    !IsAdminOnlyLocation(dir, isDirectory: true) ||
                    !IsAdminOnlyLocation(_runtimeOverridesFilePath, isDirectory: false))
                {
                    _logger.LogCritical("Refusing to load runtime overrides from {path}: file or directory is not exclusively owned/writable by Administrators or SYSTEM. Using appsettings.json values only.", _runtimeOverridesFilePath);
                    return;
                }

                var overrides = JsonSerializer.Deserialize<RuntimeOverrides>(File.ReadAllText(_runtimeOverridesFilePath));
                if (overrides?.GlobalMaxTcpConnections is int saved && saved >= 200 && saved <= 10000)
                {
                    _globalMaxTcpConnections = saved;
                    _logger.LogInformation("Applied saved runtime override: GlobalMaxTcpConnections={val} (from {path}).", saved, _runtimeOverridesFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load runtime overrides from {path}; using appsettings.json values only.", _runtimeOverridesFilePath);
            }
        }

        /// <summary>
        /// Persists the current in-memory threshold so it survives a service
        /// restart, without touching appsettings.json itself. Triggered by
        /// ConfigUpdateCommand.SaveRequested (the TrayApp's "Save
        /// Configuration" button). Same atomic-write pattern as
        /// SaveWhitelistToDiskAsync: random temp name, FileMode.CreateNew,
        /// atomic Move.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task SaveRuntimeOverridesAsync()
        {
            await _runtimeOverridesFileLock.WaitAsync();
            try
            {
                string? dir = Path.GetDirectoryName(_runtimeOverridesFilePath);
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
                        _logger.LogCritical("Not persisting runtime overrides: {dir} is not exclusively controlled by Administrators/SYSTEM.", dir);
                        return;
                    }
                }

                var overrides = new RuntimeOverrides { GlobalMaxTcpConnections = _globalMaxTcpConnections };
                string tempPath = _runtimeOverridesFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(overrides));
                try
                {
                    await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        bufferSize: 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    {
                        await fs.WriteAsync(payload);
                        await fs.FlushAsync();
                    }
                    File.Move(tempPath, _runtimeOverridesFilePath, overwrite: true);
                    _logger.LogInformation("Saved runtime overrides to {path} (GlobalMaxTcpConnections={val}).", _runtimeOverridesFilePath, _globalMaxTcpConnections);
                }
                catch
                {
                    try { File.Delete(tempPath); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist runtime overrides to disk.");
            }
            finally
            {
                _runtimeOverridesFileLock.Release();
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

                // Discover, by file lock (not by name), any OTHER process holding
                // the target's own executable open - this is what "a related
                // process blocks the restart" actually means at the OS level, and
                // it works for any leaking process, not just ones named in config.
                // Deliberately queried BEFORE the target is killed, while its exe
                // path is still readable from the live process.
                string? exePath = null;
                try { exePath = proc?.MainModule?.FileName; }
                catch (Exception ex) { _logger.LogInformation(ex, "Could not read MainModule.FileName for PID {pid}; skipping dependent-process discovery.", processId); }

                var blockingPids = !string.IsNullOrEmpty(exePath)
                    ? GetProcessesLockingFile(exePath, processId)
                    : new List<int>();

                if (blockingPids.Count > 0)
                {
                    _logger.LogWarning("Restart Manager reports {count} other process(es) locking {path}; these will be terminated alongside PID {pid} so the restart isn't blocked.",
                        blockingPids.Count, exePath, processId);
                    SendSyslog(SyslogSeverity.Notice, $"Terminating {blockingPids.Count} dependent process(es) blocking restart of PID {processId} ({exePath}).");
                }

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

                // Terminate each discovered blocking process too, with the same
                // whitelist respect as the primary target. Each gets its own
                // fresh Process.GetProcessById lookup immediately before acting,
                // so one that already exited between discovery and now is simply
                // skipped rather than acted on blind.
                foreach (int blockingPid in blockingPids)
                {
                    try
                    {
                        using var blockingProc = Process.GetProcessById(blockingPid);
                        string blockingName = blockingProc.ProcessName;
                        string blockingClean = Path.GetFileNameWithoutExtension(blockingName);

                        if (_whitelist.ContainsKey(blockingClean) || _whitelist.ContainsKey(blockingName))
                        {
                            _logger.LogInformation("Not terminating blocking process {name} (PID {pid}): whitelisted.", blockingName, blockingPid);
                            continue;
                        }

                        _logger.LogWarning("Terminating {name} (PID {pid}): blocking restart of {target} (PID {targetPid}).",
                            blockingName, blockingPid, serviceNameToUse, processId);
                        ExecuteSafeCommand("taskkill.exe", new[] { "/F", "/T", "/PID", blockingPid.ToString() });
                    }
                    catch (ArgumentException)
                    {
                        // Already exited between discovery and now - nothing to do.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to terminate blocking PID {pid}.", blockingPid);
                    }
                }

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

        /// <summary>
        /// Returns the PIDs of other processes holding a lock on the given file,
        /// via Windows Restart Manager - the same mechanism installers/updaters
        /// use to determine what needs to close before a file can be replaced.
        /// Returns an empty list (never null) on any failure, logging a warning
        /// rather than throwing, so a Restart Manager problem degrades to "just
        /// restart the primary target" instead of blocking the restart entirely.
        /// </summary>
        private List<int> GetProcessesLockingFile(string filePath, int excludePid)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return result;

            const int ERROR_MORE_DATA = 234;
            uint handle = 0;
            bool sessionStarted = false;

            try
            {
                var sessionKey = new StringBuilder(CCH_RM_SESSION_KEY + 1);
                int res = RmStartSession(out handle, 0, sessionKey);
                if (res != 0)
                {
                    _logger.LogWarning("RmStartSession failed with code {code}; skipping dependent-process discovery for {path}.", res, filePath);
                    return result;
                }
                sessionStarted = true;

                res = RmRegisterResources(handle, 1, new[] { filePath }, 0, null, 0, null);
                if (res != 0)
                {
                    _logger.LogWarning("RmRegisterResources failed with code {code} for {path}; skipping dependent-process discovery.", res, filePath);
                    return result;
                }

                uint pnProcInfoNeeded = 0;
                uint pnProcInfo = 0;
                uint lpdwRebootReasons = 0;

                // Sizing call: null array, just asks how many entries exist.
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);
                if (res != 0 && res != ERROR_MORE_DATA)
                {
                    _logger.LogWarning("RmGetList (sizing call) failed with code {code} for {path}; skipping dependent-process discovery.", res, filePath);
                    return result;
                }
                if (pnProcInfoNeeded == 0) return result;

                var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
                if (res != 0)
                {
                    _logger.LogWarning("RmGetList failed with code {code} for {path}; skipping dependent-process discovery.", res, filePath);
                    return result;
                }

                for (int i = 0; i < pnProcInfo; i++)
                {
                    int pid = processInfo[i].Process.dwProcessId;
                    if (pid != excludePid && pid > 4) result.Add(pid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Restart Manager query failed unexpectedly for {path}; continuing without dependent-process discovery.", filePath);
            }
            finally
            {
                if (sessionStarted)
                {
                    try { RmEndSession(handle); } catch { /* best effort */ }
                }
            }

            return result;
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

}