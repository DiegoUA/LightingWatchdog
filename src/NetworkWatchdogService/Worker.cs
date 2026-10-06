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

        // Status banding relative to the process's OWN restart threshold. Keep in sync with
        // HealthBands in the TrayApp (AmberStartFraction / RedStartFraction). Previously
        // "Healthy" was a hardcoded < 500 and "CRITICAL" only at >= the threshold - which a
        // process restarted at threshold+1 could essentially never display.
        private const double StatusAmberFraction = 0.50;
        private const double StatusRedFraction = 0.80;

        // Persistent mitigation archive. Lives in the same ACL-locked directory as
        // whitelist.json / runtime-overrides.json, so the unprivileged TrayApp cannot read it
        // directly - it asks for it over the telemetry pipe (GET_HISTORY) instead.
        private readonly string _mitigationHistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "MitigationHistory.csv");
        private readonly SemaphoreSlim _mitigationHistoryLock = new(1, 1);

        // Pre-mitigation R&D evidence (see EvidenceCollector.cs). Cases live in the same
        // Administrators/SYSTEM-only %ProgramData%\NetworkWatchdogService tree.
        private readonly string _evidenceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetworkWatchdogService", "Evidence");
        private readonly SemaphoreSlim _evidenceLock = new(1, 1);          // one capture at a time
        private readonly ConcurrentDictionary<string, DateTime> _lastAutoEvidence = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool _captureEvidenceBeforeMitigation;
        private const long MaxMitigationHistoryBytes = 5L * 1024 * 1024;
        private const int MaxHistoryEntriesServed = 1000;
        private readonly object _connectionCountsLock = new();

        private readonly record struct AfdEvent(int ProcessId, bool IsConnect);

        // Bumped every time this file changes, logged loudly at startup and
        // exposed in telemetry, so you can tell at a glance - from the
        // service's own log or the dashboard itself - whether the process
        // that's actually running matches the source you just built.
        private const string BuildMarker = "2026-10-09-v3.9.0-evidence";

        private readonly WatchdogConfig _config;

        // Case-insensitive lookup from a process name (either a MonitoredServices
        // ServiceName or any name in its ProcessTree) to that service's config
        // entry, built once at startup. Used to resolve per-service threshold
        // overrides and the EnableRestart toggle.
        private readonly Dictionary<string, MonitoredService> _serviceConfigByProcessName;

        // Cheap pre-filter for the 5-second detection loop: the lowest threshold any process
        // could possibly be held to. A PID below it can never breach any applicable threshold,
        // so its name/config never needs resolving - this keeps the hot path cheap even with
        // MonitorAllProcesses=true and CIM reporting counts for every process on the box.
        //
        // Only the PINNED per-service minimum is fixed at startup. The global threshold changes at
        // runtime (dashboard slider, saved override), so the floor is recomputed on every pass.
        // (Before v3.9.0 the whole floor was frozen at startup, so a threshold lowered below the
        // startup value - by the slider or by a saved override - never triggered a restart.)
        private readonly int _lowestPinnedServiceThreshold;
        private int MinPossibleThreshold => Math.Min(_globalMaxTcpConnections, _lowestPinnedServiceThreshold);

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
            _captureEvidenceBeforeMitigation = _config.Evidence.Enabled && _config.Evidence.CaptureBeforeMitigation;

            _serviceConfigByProcessName = new Dictionary<string, MonitoredService>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in _config.MonitoredServices)
            {
                if (!string.IsNullOrWhiteSpace(svc.ServiceName))
                    _serviceConfigByProcessName[svc.ServiceName] = svc;
                foreach (var name in svc.ProcessTree)
                    if (!string.IsNullOrWhiteSpace(name))
                        _serviceConfigByProcessName[name] = svc;
            }

            _lowestPinnedServiceThreshold = _config.MonitoredServices
                .Where(s => s.MaxTcpConnections.HasValue)
                .Select(s => s.MaxTcpConnections!.Value)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
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
                // A service entry without MaxTcpConnections inherits the live global threshold (the dashboard slider).
                return (svc.MaxTcpConnections ?? _globalMaxTcpConnections, svc.EnableRestart, svc.ServiceName);
            return (_globalMaxTcpConnections, true, null);
        }

        private string GetOrCacheProcessName(int pid, Process proc)
        {
            if (_processNameCache.TryGetValue(pid, out string? cached)) return cached;
            string name = proc.ProcessName;
            _processNameCache[pid] = name;
            return name;
        }

        // --- Syslog ---
        // The single RFC 3164/UDP implementation lives in SyslogNotifier.cs (it used to be
        // duplicated inline here while SyslogNotifier.cs sat unused). Fire-and-forget: a syslog
        // failure never affects detection or restarts. Only meaningful state changes are sent.
        private void SendSyslog(SyslogSeverity severity, string message)
            => SyslogNotifier.Send(_config.Syslog, severity, message);

        // Rate limiter for repeated notices (log + syslog), e.g. a process that keeps breaching
        // its threshold every 5 s while it sits in cooldown or quarantine.
        private readonly ConcurrentDictionary<string, DateTime> _lastNotice = new();

        private bool ShouldNotify(string key, TimeSpan interval)
        {
            DateTime now = DateTime.UtcNow;
            while (true)
            {
                if (!_lastNotice.TryGetValue(key, out DateTime last))
                {
                    if (_lastNotice.TryAdd(key, now)) return true;
                    continue;
                }
                if (now - last < interval) return false;
                if (_lastNotice.TryUpdate(key, now, last)) return true;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting NetworkWatchdogService Worker. BuildMarker={marker}", BuildMarker);
            SendSyslog(SyslogSeverity.Informational, $"NetworkWatchdogService starting (BuildMarker={BuildMarker}).");

            LoadWhitelistFromDisk();
            LoadRuntimeOverrides();

            string? dataDir = EnsureSecureDataDirectory();
            if (dataDir != null) TelemetryExporter.Initialize(dataDir);
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
                    if (currentConnections <= MinPossibleThreshold) continue;

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

                        if (ShouldNotify("breach:" + bookkeepingName, TimeSpan.FromSeconds(60)))
                        {
                            _logger.LogWarning("Socket leak breach ({count} > {max}) in PID {pid} ({name})!",
                                currentConnections, threshold, proc.Id, bookkeepingName);
                            SendSyslog(SyslogSeverity.Warning, $"Socket leak breach: {bookkeepingName} (PID {proc.Id}) has {currentConnections} connections, threshold {threshold}.");
                        }

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
                            if (ShouldNotify("cooldown:" + bookkeepingName, TimeSpan.FromSeconds(60)))
                                _logger.LogInformation("{name} breached its threshold again but is still in cooldown for {remaining}s; not restarting yet.",
                                    bookkeepingName, (int)remaining.TotalSeconds);
                            continue;
                        }

                        if (_config.EnableQuarantine && IsQuarantined(bookkeepingName, out int recentCount))
                        {
                            if (ShouldNotify("quarantine:" + bookkeepingName, TimeSpan.FromMinutes(10)))
                            {
                                _logger.LogCritical("QUARANTINED: {name} has restarted {count} times in the last {window} minute(s) (limit {limit}). " +
                                    "Not auto-restarting - this needs manual investigation.",
                                    bookkeepingName, recentCount, _config.QuarantineWindowMinutes, _config.QuarantineRestartLimit);
                                SendSyslog(SyslogSeverity.Alert, $"QUARANTINED: {bookkeepingName} restarted {recentCount} times in the last {_config.QuarantineWindowMinutes} minute(s); auto-restart suspended, manual investigation needed.");
                            }
                            continue;
                        }

                        RecordRestartAttempt(bookkeepingName);
                        _activeRestarts.TryAdd(pid, DateTime.UtcNow);
                        SendSyslog(SyslogSeverity.Notice, $"Restarting {bookkeepingName} (PID {proc.Id}) due to socket leak ({currentConnections} connections).");

                        _ = RestartLeakingServiceAsync(procName, proc.Id, currentConnections, currentStartTime, threshold);
                    }
                    catch (Exception ex)
                    {
                        // Typically the process exited between the count update and this check.
                        _logger.LogDebug(ex, "Detection check for PID {pid} failed; dropping its tracking state.", pid);
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

                _logger.LogWarning("GetExtendedTcpTable (IPv{ver}) kept returning ERROR_INSUFFICIENT_BUFFER after {attempts} attempts; skipping this cycle.", ipVersion == AF_INET ? 4 : 6, MaxAttempts);
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
                if (ShouldNotify("cim-failed", TimeSpan.FromMinutes(5))) _logger.LogError(ex, "CIM/NSI query for MSFT_NetTCPConnection failed.");
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
                _logger.LogWarning(ex, "Failed to resolve PID(s) for watched process name {name}.", processName);
                return;
            }

            try
            {
                if (matches.Length == 0)
                {
                    _logger.LogDebug("Baseline resync: no running process named {name} found.", processName);
                    return;
                }

                foreach (var proc in matches)
                {
                    int pid = proc.Id;
                    _logger.LogDebug("Baseline resync: {name} (PID {pid}) = {val} ({source}).",
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
            _logger.LogDebug("Resyncing socket baselines...");

            // CIM/NSI first - this is what actually sees LightingService's
            // leaked Bound sockets. Only fall back to the legacy IP Helper
            // table if CIM itself is unavailable, so detection degrades
            // instead of going blind.
            var counts = GetTcpConnectionCountsViaCim();
            bool usedFallback = false;
            var healthSnapshots = new List<(int Pid, int Count)>();

            if (counts == null)
            {
                try
                {
                    counts = GetNativeTcpConnectionCounts();
                    usedFallback = true;
                    if (ShouldNotify("cim-fallback", TimeSpan.FromMinutes(5))) _logger.LogError("CIM/NSI query failed; fell back to GetExtendedTcpTable, which will UNDERCOUNT bind()-only leaked sockets.");
                }
                catch (Exception ex)
                {
                    if (ShouldNotify("resync-total-failure", TimeSpan.FromMinutes(5))) _logger.LogCritical(ex, "Both CIM/NSI and the legacy native TCP table fetch failed this cycle; baseline not updated.");
                    SendSyslog(SyslogSeverity.Error, $"Baseline resync failed completely this cycle (both CIM/NSI and legacy fetch failed): {ex.Message}");
                    return;
                }
            }

            // Unconditional, every cycle, regardless of threshold: proves the
            // fetch ran and shows exactly what it's seeing for the watched
            // process. Looked up by name, not a hardcoded PID - PIDs are
            // reassigned every time the process restarts.
            foreach (var monitored in _config.MonitoredServices)
                if (!string.IsNullOrWhiteSpace(monitored.ServiceName))
                    LogCountForWatchedProcess(counts, monitored.ServiceName, usedFallback);

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
                        _logger.LogDebug("Baseline resync: PID {pid} has {count} native TCP table rows.", kvp.Key, kvp.Value);
                    }
                    if (kvp.Key > 4)
                    {
                        _pidConnectionCounts[kvp.Key] = kvp.Value;
                        if (kvp.Value >= TelemetryDisplayThreshold) healthSnapshots.Add((kvp.Key, kvp.Value));
                    }
                }

                PruneStaleCaches();
            }

            // File I/O happens outside the lock the ETW consumer also takes. These snapshots are the
            // leak-growth timeline that ends up in evidence bundles (and in HealthTrend_v2.csv).
            foreach (var (pid, count) in healthSnapshots)
                TelemetryExporter.RecordHealthSnapshot(ResolveProcessNameCached(pid), pid, count);
        }

        private string ResolveProcessNameCached(int pid)
        {
            if (_processNameCache.TryGetValue(pid, out string? cached)) return cached;
            try
            {
                using var p = Process.GetProcessById(pid);
                string name = p.ProcessName;
                _processNameCache[pid] = name;
                return name;
            }
            catch { return "unknown"; }
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
                        else if (line == "GET_HISTORY")
                        {
                            var entries = await ReadMitigationHistoryAsync(MaxHistoryEntriesServed, token);
                            string json = JsonSerializer.Serialize(new HistoryPacket { Entries = entries });
                            await writer.WriteLineAsync(json.AsMemory(), token);
                        }
                        else if (line == "LIST_EVIDENCE")
                        {
                            // Metadata only (no file contents): the case files themselves stay in the
                            // Administrators/SYSTEM-only folder and are exported via the control pipe.
                            var list = new EvidenceListPacket { Cases = EvidenceCollector.ListCases(_evidenceDirectory) };
                            string json = JsonSerializer.Serialize(list);
                            await writer.WriteLineAsync(json.AsMemory(), token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // Was a bare catch that discarded everything.
                    _logger.LogWarning(ex, "Telemetry pipe client handler failed.");
                }
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
                        else if (line.StartsWith("CAPTURE_EVIDENCE:"))
                        {
                            // A capture legitimately takes longer than the normal 10 s IPC deadline.
                            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _config.Evidence.CaptureTimeoutSeconds) + 180));
                            string reply = await HandleCaptureEvidenceCommandAsync(line.Substring("CAPTURE_EVIDENCE:".Length));
                            await writer.WriteLineAsync(reply.AsMemory(), token);
                        }
                        else if (line.StartsWith("EXPORT_EVIDENCE:"))
                        {
                            cts.CancelAfter(TimeSpan.FromMinutes(10));      // large dumps can take a while to copy
                            string reply = HandleExportEvidenceCommand(server, line.Substring("EXPORT_EVIDENCE:".Length));
                            await writer.WriteLineAsync(reply.AsMemory(), token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Control pipe client handler failed.");
                }
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
                BuildMarker = BuildMarker,
                EvidenceAutoCapture = _captureEvidenceBeforeMitigation && _config.Evidence.Enabled
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

                int processThreshold = ResolveThresholdForProcess(Path.GetFileNameWithoutExtension(procName)).Threshold;
                double ratio = (double)kvp.Value / Math.Max(1, processThreshold);
                string status = ratio >= StatusRedFraction ? "CRITICAL LEAK" : (ratio >= StatusAmberFraction ? "Elevated" : "Healthy");
                packet.Processes.Add(new ProcessTelemetryItem
                {
                    ServiceName = procName,
                    Pid = kvp.Key,
                    Connections = kvp.Value,
                    Status = status,
                    Threshold = processThreshold
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

            if (cmd.AutoCaptureEvidence.HasValue)
            {
                _captureEvidenceBeforeMitigation = cmd.AutoCaptureEvidence.Value && _config.Evidence.Enabled;
                _logger.LogInformation("IPC: capture-evidence-before-mitigation set to {val}.", _captureEvidenceBeforeMitigation);
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
            public bool? CaptureEvidenceBeforeMitigation { get; set; }
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
                if (overrides?.CaptureEvidenceBeforeMitigation is bool savedCapture)
                {
                    _captureEvidenceBeforeMitigation = savedCapture && _config.Evidence.Enabled;
                    _logger.LogInformation("Applied saved runtime override: CaptureEvidenceBeforeMitigation={val}.", _captureEvidenceBeforeMitigation);
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

                var overrides = new RuntimeOverrides { GlobalMaxTcpConnections = _globalMaxTcpConnections, CaptureEvidenceBeforeMitigation = _captureEvidenceBeforeMitigation };
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

        /// <summary>
        /// Appends one mitigation record to MitigationHistory.csv (cross-reboot archive).
        /// Same directory lock-down and verification discipline as the whitelist file; capped
        /// at 5 MB with a single rotated backup (.1) so it cannot grow without bound.
        /// Fire-and-forget safe: failures are logged and never affect the restart itself.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task AppendMitigationHistoryAsync(string csvLine)
        {
            await _mitigationHistoryLock.WaitAsync();
            try
            {
                string? dir = Path.GetDirectoryName(_mitigationHistoryFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    try { SecureDirectoryToAdminsAndSystem(dir); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to lock down ACLs on {dir}.", dir); }

                    if (!IsAdminOnlyLocation(dir, isDirectory: true))
                    {
                        _logger.LogCritical("Not writing mitigation history: {dir} is not exclusively controlled by Administrators/SYSTEM.", dir);
                        return;
                    }
                }

                var fi = new FileInfo(_mitigationHistoryFilePath);
                if (fi.Exists && fi.Length > MaxMitigationHistoryBytes)
                    File.Move(_mitigationHistoryFilePath, _mitigationHistoryFilePath + ".1", overwrite: true);

                await File.AppendAllTextAsync(_mitigationHistoryFilePath, csvLine + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to append to mitigation history {path}.", _mitigationHistoryFilePath);
            }
            finally
            {
                _mitigationHistoryLock.Release();
            }
        }

        /// <summary>Returns the newest <paramref name="maxEntries"/> history lines (oldest first).</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<List<string>> ReadMitigationHistoryAsync(int maxEntries, CancellationToken token)
        {
            var result = new List<string>();
            await _mitigationHistoryLock.WaitAsync(token);
            try
            {
                if (!File.Exists(_mitigationHistoryFilePath)) return result;

                string? dir = Path.GetDirectoryName(_mitigationHistoryFilePath);
                if (string.IsNullOrEmpty(dir) ||
                    !IsAdminOnlyLocation(dir, isDirectory: true) ||
                    !IsAdminOnlyLocation(_mitigationHistoryFilePath, isDirectory: false))
                {
                    _logger.LogCritical("Refusing to serve mitigation history from {path}: not exclusively owned/writable by Administrators or SYSTEM.", _mitigationHistoryFilePath);
                    return result;
                }

                var tail = new Queue<string>(maxEntries + 1);
                foreach (var line in File.ReadLines(_mitigationHistoryFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    tail.Enqueue(line);
                    if (tail.Count > maxEntries) tail.Dequeue();
                }
                result.AddRange(tail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read mitigation history {path}.", _mitigationHistoryFilePath);
            }
            finally
            {
                _mitigationHistoryLock.Release();
            }
            return result;
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

        // =====================================================================================
        // Restart pipeline (v3.9.0): discover related processes -> capture evidence -> stop -> kill
        // -> restart target -> restart related services.
        // =====================================================================================
        private const string DependentServiceReason = "dependent service";

        private sealed record RelatedProcess(int Pid, DateTime StartTime, string Name, string? ServiceName, string Reason);

        // Never terminated as a "related" process, whatever discovers it (shared service hosts
        // such as svchost would take unrelated services down with them).
        private static readonly HashSet<string> NeverTerminateNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "svchost", "fontdrvhost", "dwm", "explorer", "memory compression", "secure system"
        };

        // Never dumped on request: credential / kernel-critical processes.
        private static readonly HashSet<string> NeverDumpNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "secure system", "memory compression"
        };

        private async Task RestartLeakingServiceAsync(string fallbackServiceName, int processId, int socketCount, DateTime expectedStartTime, int thresholdAtBreach)
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
                string cleanName = Path.GetFileNameWithoutExtension(fallbackServiceName);

                string? exePath = null;
                try { exePath = proc?.MainModule?.FileName; }
                catch (Exception ex) { _logger.LogInformation(ex, "Could not read MainModule.FileName for PID {pid}; file-lock discovery skipped.", processId); }

                // 1) Everything that blocks, or must accompany, this restart - found dynamically,
                //    while the target is still alive and its executable path is still readable.
                var related = DiscoverRelatedProcesses(processId, exePath, scmServiceName, cleanName);
                if (related.Count > 0)
                {
                    string summary = string.Join("; ", related.Select(r => $"{r.Name} (PID {r.Pid}, {r.Reason})"));
                    _logger.LogWarning("{count} related process(es) will be restarted with PID {pid}: {summary}", related.Count, processId, summary);
                    SendSyslog(SyslogSeverity.Notice, $"Restarting {related.Count} related process(es) together with PID {processId}: {summary}");
                }

                // 2) Evidence FIRST: once the process is killed its sockets, handles and memory are gone.
                //    Bounded by EvidenceConfig.CaptureTimeoutSeconds; a failure here never blocks the restart.
                PendingEvidenceCase? evidence = null;
                if (ShouldCaptureEvidenceAutomatically(cleanName))
                {
                    _lastAutoEvidence[cleanName] = DateTime.UtcNow;
                    evidence = await CaptureEvidenceAsync(processId, fallbackServiceName, scmServiceName, exePath, "auto-breach",
                        socketCount, thresholdAtBreach,
                        related.Select(r => $"{r.Name} (PID {r.Pid}) - {r.Reason}").ToList(),
                        TimeSpan.FromSeconds(Math.Max(10, _config.Evidence.CaptureTimeoutSeconds)));
                }

                // 3) Stop. Dependents first (a service with running dependents cannot stop cleanly),
                //    then the target through the Service Control Manager.
                _logger.LogWarning("Force killing leaking process/service {serviceName} (PID {pid})...", serviceNameToUse, processId);

                foreach (var dep in related.Where(r => r.Reason == DependentServiceReason && r.ServiceName != null))
                    ExecuteSafeCommand("sc.exe", new[] { "stop", dep.ServiceName! }, suppressErrors: true);
                if (scmServiceName != null)
                    ExecuteSafeCommand("sc.exe", new[] { "stop", scmServiceName }, suppressErrors: true);

                // 4) Kill - but only the exact process instance that triggered this. If 'sc stop' already
                //    ended it (or the PID was recycled) there is nothing to kill; the restart continues
                //    either way. (Previously this case returned early and left the service stopped.)
                if (TryVerifySamePid(processId, expectedStartTime, out _))
                {
                    ExecuteSafeCommand("taskkill.exe", new[] { "/F", "/T", "/PID", processId.ToString() });
                }
                else
                {
                    _logger.LogInformation("PID {pid} already exited (or was recycled); not killing, continuing with the restart.", processId);
                }
                proc?.Dispose();

                // Related processes: each is re-verified against the start time captured at discovery
                // immediately before it is touched, so one that already exited (or whose PID was
                // recycled) is skipped rather than acted on blind.
                if (related.Count > 0) await Task.Delay(2000);
                foreach (var r in related)
                {
                    try
                    {
                        if (!TryVerifySamePid(r.Pid, r.StartTime, out Process? rp)) continue;
                        using (rp)
                        {
                            if (CountServicesInProcess(r.Pid) > 1)
                            {
                                _logger.LogInformation("Not terminating {name} (PID {pid}): the process hosts several services.", r.Name, r.Pid);
                                continue;
                            }
                            _logger.LogWarning("Terminating {name} (PID {pid}): {reason} of {target} (PID {targetPid}).",
                                r.Name, r.Pid, r.Reason, serviceNameToUse, processId);
                            ExecuteSafeCommand("taskkill.exe", new[] { "/F", "/T", "/PID", r.Pid.ToString() });
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to terminate related PID {pid}.", r.Pid); }
                }

                // Commas/newlines in a process name would corrupt the CSV the dashboard parses.
                string csvName = serviceNameToUse.Replace(',', '_').Replace('\r', ' ').Replace('\n', ' ');
                string mitigationLine = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{csvName},{processId},Exceeded {socketCount} sockets";
                _recentRestarts.Enqueue(mitigationLine);
                while (_recentRestarts.Count > 50 && _recentRestarts.TryDequeue(out _)) { }
                _ = AppendMitigationHistoryAsync(mitigationLine);
                TelemetryExporter.RecordRestart(serviceNameToUse, processId, $"Exceeded {socketCount} sockets (threshold {thresholdAtBreach})");

                _pidConnectionCounts.TryRemove(processId, out _);
                _pidStartTimes.TryRemove(processId, out _);
                _processNameCache.TryRemove(processId, out _);
                _serviceNameCache.TryRemove(processId, out _);

                // Packaging (hashing + zipping) can take a while for a large dump, so it runs in the
                // background now that the process is gone.
                if (evidence != null) _ = FinalizeEvidenceAsync(evidence);

                await Task.Delay(30000);

                // 5) Bring everything back through the SCM only (no standalone relaunch: removed as an LPE risk).
                if (scmServiceName == null)
                {
                    _logger.LogInformation("PID {pid} ({name}) is not a registered Windows service; it was terminated and will not be relaunched.", processId, serviceNameToUse);
                }
                else if (await TryStartServiceAsync(scmServiceName))
                {
                    _logger.LogInformation("Service {serviceName} is running again after the restart.", scmServiceName);
                    SendSyslog(SyslogSeverity.Informational, $"Service {scmServiceName} is running again after the mitigation.");
                }
                else
                {
                    _logger.LogWarning("Service {serviceName} was not confirmed running after the restart attempt. " +
                        "Standalone executable relaunch is disabled by design; manual intervention may be required.", scmServiceName);
                    SendSyslog(SyslogSeverity.Error, $"Service {scmServiceName} was NOT confirmed running after the mitigation; manual intervention may be required.");
                }

                var relatedServices = related
                    .Where(r => IsValidServiceName(r.ServiceName))
                    .Select(r => r.ServiceName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => !string.Equals(n, scmServiceName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var svc in relatedServices)
                {
                    if (await TryStartServiceAsync(svc)) _logger.LogInformation("Related service {serviceName} is running again.", svc);
                    else _logger.LogWarning("Related service {serviceName} was not confirmed running after the restart.", svc);
                }
                foreach (var r in related.Where(r => !IsValidServiceName(r.ServiceName)))
                    _logger.LogInformation("{name} (PID {pid}) was terminated with the target; it is not a Windows service and is not relaunched.", r.Name, r.Pid);
            }
            finally
            {
                _activeRestarts.TryRemove(processId, out _);
            }
        }

        // -------------------------------------------------------------------------------------
        // Related-process discovery. Three independent, universal sources (no hardcoded names):
        //   1. Windows services that DEPEND on the target service (they must stop before it can),
        //   2. other processes locking the target's executable (Restart Manager),
        //   3. other processes in the same MonitoredServices entry's ProcessTree (explicit config -
        //      this is the legacy v2.x behaviour of restarting the whole tree together).
        // -------------------------------------------------------------------------------------
        private List<RelatedProcess> DiscoverRelatedProcesses(int targetPid, string? exePath, string? scmServiceName, string cleanName)
        {
            var found = new Dictionary<int, RelatedProcess>();

            void Add(int pid, string reason, string? serviceName)
            {
                if (pid <= 4 || pid == targetPid || pid == Environment.ProcessId || found.ContainsKey(pid)) return;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    string name = p.ProcessName;
                    string clean = Path.GetFileNameWithoutExtension(name);
                    if (NeverTerminateNames.Contains(clean)) return;
                    if (_whitelist.ContainsKey(clean) || _whitelist.ContainsKey(name)) return;

                    string? svc = serviceName;
                    if (svc == null)
                    {
                        string? resolved = GetServiceNameFromPidCached(pid);
                        if (IsValidServiceName(resolved)) svc = resolved;
                    }
                    found[pid] = new RelatedProcess(pid, p.StartTime, name, svc, reason);
                }
                catch { /* exited or inaccessible: nothing to restart */ }
            }

            try
            {
                if (scmServiceName != null)
                    foreach (var dep in GetRunningDependentServices(scmServiceName, 0))
                        Add(dep.Pid, DependentServiceReason, dep.Name);

                if (!string.IsNullOrEmpty(exePath))
                    foreach (int pid in GetProcessesLockingFile(exePath, targetPid))
                        Add(pid, "locks the target executable", null);

                if (_serviceConfigByProcessName.TryGetValue(cleanName, out var cfg))
                {
                    foreach (var name in cfg.ProcessTree.Append(cfg.ServiceName).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, cleanName, StringComparison.OrdinalIgnoreCase)) continue;
                        Process[] matches;
                        try { matches = Process.GetProcessesByName(name); } catch { continue; }
                        foreach (var m in matches)
                        {
                            try { Add(m.Id, "configured ProcessTree member", null); }
                            finally { m.Dispose(); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Related-process discovery failed; continuing with the primary target only.");
            }

            return found.Values.ToList();
        }

        /// <summary>Running services that (transitively) depend on the given service, via WMI.</summary>
        private List<(string Name, int Pid)> GetRunningDependentServices(string serviceName, int depth)
        {
            var result = new List<(string Name, int Pid)>();
            if (depth > 4 || serviceName.Contains('\'')) return result;

            try
            {
                // Services that depend ON serviceName play the "Dependent" role of Win32_DependentService;
                // serviceName itself is the "Antecedent".
                using var searcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_Service.Name='{serviceName}'}} WHERE AssocClass=Win32_DependentService Role=Antecedent");
                foreach (ManagementObject o in searcher.Get())
                {
                    string? name = o["Name"]?.ToString();
                    string? state = o["State"]?.ToString();
                    if (string.IsNullOrEmpty(name) || !IsValidServiceName(name)) continue;
                    if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase)) continue;

                    int pid = Convert.ToInt32(o["ProcessId"] ?? 0);
                    if (result.All(r => !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add((name, pid));
                        foreach (var nested in GetRunningDependentServices(name, depth + 1))
                            if (result.All(r => !string.Equals(r.Name, nested.Name, StringComparison.OrdinalIgnoreCase)))
                                result.Add(nested);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate dependent services of {service}.", serviceName);
            }
            return result;
        }

        private int CountServicesInProcess(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE ProcessId = {pid}");
                return searcher.Get().Count;
            }
            catch { return 1; }
        }

        // -------------------------------------------------------------------------------------
        // Evidence capture (see EvidenceCollector.cs)
        // -------------------------------------------------------------------------------------
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private string? EnsureSecureDataDirectory()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_whitelistFilePath);
                if (string.IsNullOrEmpty(dir)) return null;
                Directory.CreateDirectory(dir);
                try { SecureDirectoryToAdminsAndSystem(dir); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to lock down ACLs on {dir}.", dir); }
                return IsAdminOnlyLocation(dir, isDirectory: true) ? dir : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not prepare the secure data directory.");
                return null;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private string? EnsureEvidenceDirectory()
        {
            if (EnsureSecureDataDirectory() == null) return null;
            try
            {
                Directory.CreateDirectory(_evidenceDirectory);
                return IsAdminOnlyLocation(_evidenceDirectory, isDirectory: true) ? _evidenceDirectory : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not prepare the evidence directory {dir}.", _evidenceDirectory);
                return null;
            }
        }

        private bool ShouldCaptureEvidenceAutomatically(string cleanName)
        {
            var ev = _config.Evidence;
            if (!ev.Enabled || !_captureEvidenceBeforeMitigation) return false;
            if (_lastAutoEvidence.TryGetValue(cleanName, out DateTime last) &&
                DateTime.UtcNow - last < TimeSpan.FromMinutes(Math.Max(0, ev.MinIntervalMinutes)))
            {
                _logger.LogInformation("Evidence for {name} was captured recently; skipping this time (MinIntervalMinutes={min}).", cleanName, ev.MinIntervalMinutes);
                return false;
            }
            return true;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<PendingEvidenceCase?> CaptureEvidenceAsync(
            int pid, string processName, string? serviceName, string? exePath, string trigger,
            int connections, int threshold, List<string> relatedDescriptions, TimeSpan budget)
        {
            try
            {
                string? dir = EnsureEvidenceDirectory();
                if (dir == null)
                {
                    _logger.LogError("Evidence capture skipped: the evidence directory is not exclusively controlled by Administrators/SYSTEM.");
                    return null;
                }

                if (!await _evidenceLock.WaitAsync(budget))
                {
                    _logger.LogWarning("Evidence capture skipped: another capture is still running.");
                    return null;
                }

                try
                {
                    var history = await ReadMitigationHistoryAsync(200, CancellationToken.None);
                    var request = new EvidenceRequest
                    {
                        ProcessId = pid,
                        ProcessName = processName,
                        ServiceName = serviceName,
                        ExecutablePath = exePath,
                        Trigger = trigger,
                        Connections = connections,
                        Threshold = threshold,
                        DumpMode = _config.Evidence.DumpMode,
                        BuildMarker = BuildMarker,
                        EvidenceDirectory = dir,
                        RelatedProcesses = relatedDescriptions,
                        HistoryLines = history,
                        ConfigSnapshotJson = JsonSerializer.Serialize(new
                        {
                            EffectiveGlobalMaxTcpConnections = _globalMaxTcpConnections,
                            EvidenceAutoCapture = _captureEvidenceBeforeMitigation,
                            Config = _config
                        }, new JsonSerializerOptions { WriteIndented = true })
                    };

                    _logger.LogWarning("Capturing evidence for {name} (PID {pid}) before mitigation (budget {sec}s, dump mode {mode}).",
                        processName, pid, (int)budget.TotalSeconds, request.DumpMode);

                    using var cts = new CancellationTokenSource(budget);
                    return await EvidenceCollector.CollectAsync(request, _logger, cts.Token);
                }
                finally
                {
                    _evidenceLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Evidence capture failed for PID {pid}; the mitigation continues without it.", pid);
                return null;
            }
        }

        private async Task FinalizeEvidenceAsync(PendingEvidenceCase pending)
        {
            var info = await EvidenceCollector.PackageAsync(pending, _logger);
            if (info == null) return;

            EvidenceCollector.ApplyRetention(_evidenceDirectory, Math.Max(1, _config.Evidence.MaxCases), _logger);
            _logger.LogWarning("Evidence case {case} saved ({mb} MB).", info.CaseId, info.SizeBytes / (1024 * 1024));
            SendSyslog(SyslogSeverity.Notice, $"Evidence case {info.CaseId} saved ({info.SizeBytes / (1024 * 1024)} MB).");
            if (pending.Warnings.Count > 0)
                _logger.LogInformation("Evidence case {case} finished with {n} warning(s); see 00_Manifest.json.", info.CaseId, pending.Warnings.Count);
        }

        /// <summary>CAPTURE_EVIDENCE:{"Pid":123} (admin control pipe). Non-destructive: the process keeps running.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<string> HandleCaptureEvidenceCommandAsync(string json)
        {
            try
            {
                if (!_config.Evidence.Enabled) return "ERR:Evidence capture is disabled in the service configuration.";

                CaptureEvidenceCommand? cmd = null;
                try { cmd = JsonSerializer.Deserialize<CaptureEvidenceCommand>(json); } catch { }
                if (cmd == null || cmd.Pid <= 4) return "ERR:Invalid request.";

                Process proc;
                try { proc = Process.GetProcessById(cmd.Pid); }
                catch { return "ERR:The process no longer exists."; }

                using (proc)
                {
                    string name = proc.ProcessName;
                    string clean = Path.GetFileNameWithoutExtension(name);
                    if (NeverDumpNames.Contains(clean)) return $"ERR:Refusing to capture a protected system process ({clean}).";

                    string? exePath = null;
                    try { exePath = proc.MainModule?.FileName; } catch { }
                    string? svc = GetServiceNameFromPidCached(cmd.Pid);
                    if (!IsValidServiceName(svc)) svc = null;

                    int connections = _pidConnectionCounts.TryGetValue(cmd.Pid, out int c) ? c : 0;
                    int threshold = ResolveThresholdForProcess(clean).Threshold;

                    var pending = await CaptureEvidenceAsync(cmd.Pid, name, svc, exePath, "manual", connections, threshold,
                        new List<string>(), TimeSpan.FromSeconds(Math.Max(30, _config.Evidence.CaptureTimeoutSeconds)));
                    if (pending == null) return "ERR:Capture failed - see the service log.";

                    var info = await EvidenceCollector.PackageAsync(pending, _logger);
                    if (info == null) return "ERR:Packaging failed - see the service log.";

                    EvidenceCollector.ApplyRetention(_evidenceDirectory, Math.Max(1, _config.Evidence.MaxCases), _logger);
                    SendSyslog(SyslogSeverity.Notice, $"Manual evidence case {info.CaseId} saved.");
                    return "OK:" + info.CaseId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAPTURE_EVIDENCE failed.");
                return "ERR:" + ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            }
        }

        /// <summary>
        /// EXPORT_EVIDENCE:{"CaseId":"...","DestinationPath":"C:\\...\\x.zip"} (admin control pipe).
        /// The case file is opened here, as SYSTEM, but the DESTINATION is written while impersonating the
        /// pipe client. Windows therefore enforces the caller's own permissions on the destination: the
        /// service cannot be used to write anywhere the caller could not write himself.
        /// </summary>
        private string HandleExportEvidenceCommand(NamedPipeServerStream server, string json)
        {
            try
            {
                ExportEvidenceCommand? cmd = null;
                try { cmd = JsonSerializer.Deserialize<ExportEvidenceCommand>(json); } catch { }
                if (cmd == null || !EvidenceCollector.IsValidCaseId(cmd.CaseId)) return "ERR:Invalid case id.";

                string? zip = EvidenceCollector.FindCaseZip(_evidenceDirectory, cmd.CaseId);
                if (zip == null) return "ERR:Evidence case not found.";

                string dest;
                try { dest = Path.GetFullPath(cmd.DestinationPath ?? string.Empty); }
                catch { return "ERR:Invalid destination path."; }
                if (!dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || dest.StartsWith(@"\\", StringComparison.Ordinal))
                    return "ERR:The destination must be a local .zip file path.";

                using var source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
                string? error = null;
                server.RunAsClient(() =>
                {
                    try
                    {
                        string? destDir = Path.GetDirectoryName(dest);
                        if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir)) { error = "The destination folder does not exist."; return; }
                        using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                        source.CopyTo(target);
                        target.Flush();
                    }
                    catch (Exception ex) { error = ex.Message; }
                });

                if (error != null) return "ERR:" + error.Replace('\r', ' ').Replace('\n', ' ');
                _logger.LogInformation("Evidence case {case} exported to {dest}.", cmd.CaseId, dest);
                SendSyslog(SyslogSeverity.Notice, $"Evidence case {cmd.CaseId} exported.");
                return "OK";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EXPORT_EVIDENCE failed.");
                return "ERR:" + ex.Message.Replace('\r', ' ').Replace('\n', ' ');
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
                // Bounded wait: a hung sc.exe/taskkill.exe must never stall the whole restart pipeline.
                if (proc != null && !proc.WaitForExit(30_000))
                {
                    try { proc.Kill(true); } catch { }
                    if (!suppressErrors) _logger.LogWarning("Command timed out after 30 s and was terminated: {filename}", filename);
                }
            }
            catch (Exception ex)
            {
                if (!suppressErrors) _logger.LogError(ex, "Command failed: {filename}", filename);
            }
        }
    }

}