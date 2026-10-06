# Changelog

All notable changes to LightingWatchdog are documented here.

---

## [3.8.0] - 2026-10-06

### Added

- **R&D Diagnostic Evidence Suite**: Added a comprehensive forensic export engine that packages system port limits, CIM socket tables, active process metrics, historical restart audit logs, event logs, and memory dumps into a single `.zip` file for vendor ticket submissions.
- **Native Memory Minidumps**: Integrated `DbgHelp.dll` (`MiniDumpWriteDump`) directly into the TrayApp to capture unmanaged memory dumps (`.dmp`) of leaking processes on demand.
- **Dual Diagnostic Targeting Modes**: Users can manually select any active PID and invoke a "Save As" dialog, or check "Auto-Target Top Socket Consuming Process for Dumps" to save bundles directly to the application directory.

## [3.7.2] - 2026-10-06

### Added

- **Historical Mitigation Archive**: Split Dashboard mitigation logs into "Session Logs" (in-memory for the active TrayApp session) and "Historical Archive" (persistent cross-reboot records loaded directly from `%ProgramData%\NetworkWatchdogService\MitigationHistory.csv`).

### Fixed

- **Infinite Auto-Updater Loop**: Replaced the static hardcoded version string in `GitHubAutoUpdater` with dynamic execution assembly inspection (`Assembly.GetExecutingAssembly().GetName().Version`). Replaced brittle string alphabetical comparisons with robust `System.Version` semantic parsing to eliminate endless recursive update prompts.

## [3.7.1] - 2026-10-06

### Fixed

- **TrayApp Thread-Safety & UI Deadlocks:** Resolved critical WinForms stability bugs causing the TrayApp to crash or freeze. IPC NamedPipe queries were moved from the threshold slider's continuous `ValueChanged` event to `MouseUp` to prevent synchronous thread-blocking. `ContextMenuStrip` destruction is now safely deferred to the message pump (`Task.Delay(500)`), eliminating `ObjectDisposedException` race conditions.
- **Async Void Timer Crashes:** Wrapped the auto-updater's asynchronous timer tick in strict `try/catch` boundaries to prevent unhandled network timeouts from silently crashing the host process.
- **Startup Handle Exceptions:** Fixed a race condition where the auto-updater attempted to invoke the UI thread before window handles were fully created.
- **Auto-Updater TrayApp Respawning:** Fixed an issue where the silent Inno Setup execution (`/SILENT`) failed to relaunch the TrayApp into the user session after an OTA update. Modified the installer script `[Run]` flags to enforce `runasoriginaluser`.

## [3.7.0] - 2026-10-06

### Added

- **Native Restart Manager Integration**: Implemented Windows Restart Manager (`rstrtmgr.dll`) interop to universally detect and terminate any dependent processes holding a file lock on a mitigated executable. This guarantees clean SCM restarts without relying on hardcoded child process names.
- **Quarantine & Cooldown Lifecycle**: Introduced rolling-window Quarantine (suspends auto-restarts after 3 attempts in 60 minutes) and Cooldown (120-second short-term throttling). This prevents restart-storming and protects system stability during terminal leak scenarios.
- **Dynamic Feature Flags**: Added per-service `EnableRestart` toggles (detect-and-alert mode) and a global `MonitorAllProcesses` flag to precisely restrict Watchdog tracking scope.
- **Remote Syslog Integration**: Added automated UDP syslog forwarding (RFC 3164) for centralized network telemetry, capturing critical events like process mitigation, quarantine activation, and severe resync failures.

### Changed

- **IConfiguration & AppSettings Integration**: Fully wired `appsettings.json` into the Worker via `IOptions<WatchdogConfig>`. The service now natively obeys `WatchdogIntervalSeconds` (15s default), `GlobalMaxTcpConnections` (1000 default), and per-service threshold overrides, completely resolving previous configuration technical debt.
- **Process Whitelist Seeding**: Merged the static `ProcessWhitelist` config array seamlessly into the persistent, ACL-locked runtime whitelist on startup.
- **Working Directory Context**: Bound the executable's `CurrentDirectory` to `AppDomain.CurrentDomain.BaseDirectory` in `Program.cs` to ensure the Service Control Manager reads `appsettings.json` locally instead of targeting `System32`.

### Fixed

- **Persistent Configuration State**: Fixed a bug where clicking "Save Configuration" in the Tray App failed to persist the threshold. The service now securely saves user overrides to `runtime-overrides.json` using the same atomic-write and Administrator-only ACL lock-downs as the whitelist.

## [3.6.1] - 2026-10-03

### Added

- **Decoupled Telemetry Display Filter:** Introduced a dedicated `TelemetryDisplayThreshold = 30` filter inside the IPC packet builder. This hides idle, low-connection processes (≤ 30 sockets) from the Tray App dashboard to declutter the UI, without excluding them from the core mitigation engine. The background service continues to evaluate every active PID system-wide against the global threshold.

### Changed

- **Configuration Precedence (Technical Debt):** The application temporarily bypasses `appsettings.json` per-service limits, `MonitorAllProcesses` flags, and process tree targets. All system-wide socket tracking currently relies exclusively on the hardcoded `1000` socket limit and the `15s` polling interval, with user-driven runtime overrides persisting securely to `state.json`.

## [3.6.0] - 2026-10-02

### Added

- **Self-Contained Installer Package:** Created an Inno Setup script (`installer/NetworkWatchdogInstaller.iss`) that automatically stops legacy background processes, copies compiled binaries, registers the SCM Background Service, and configures the Tray Application to launch silently at user login.
- **GitHub Auto-Update Engine:** Natively queries the `LightingWatchdog` GitHub API every 24 hours (or on-demand), prompts the user, downloads the `.exe` installer asset to `%TEMP%`, and executes a silent overwrite update.
- **Persistent State Saving:** Introduced `state.json` and a `Save Configuration` button inside the Dashboard to permanently commit Global Threshold limits and Whitelist entries.
- **Precise Threshold Constraints:** Paired a `NumericUpDown` input box seamlessly with the `Global Max TCP/UDP Connections` slider.
- **UI Noise Filtration:** Hard-filtered any system process exhibiting fewer than `30` total sockets to declutter the "Live Processes" grid.
- **Application Icon:** Embedded a native `app.ico` into the TrayApp binary.
- **Diagnostic Build Markers:** Added a hardcoded `BuildMarker` field to the IPC `TelemetryPacket` and root startup logs to definitively verify CI/CD deployment parity.

### Changed

- **Threshold & Polling Tuning:** Lowered the default Global Threshold from `1500` to `1000` to align with strict mitigation targets. Accelerated the periodic baseline resync interval from `60s` to `15s` so rapidly climbing socket leaks are detected and mitigated up to 45 seconds faster.

### Fixed

- **Bound Socket Leak Blindness (CIM/NSI Migration):** Fixed a critical core flaw where the native `GetExtendedTcpTable` API completely ignored `bind()`-only orphaned sockets (specifically the type leaked by `LightingService`). The primary baseline synchronization engine has been rewritten to use the authoritative CIM/NSI provider (`MSFT_NetTCPConnection`), instantly exposing thousands of previously invisible leaked sockets. The legacy API is now retained solely as a fallback.
- **Silent Background Task Annihilation:** Resolved a critical stability flaw where unobserved `Task.Run` background threads (specifically the ETW parser and the periodic native table synchronizer) would encounter transient OS exceptions and permanently, silently crash. Strict `try/catch` boundaries (`SafeResyncGlobalBaseline`) now guarantee the synchronization loops survive transient errors and continue executing indefinitely.
- **IPC Telemetry & UI Thread Freezing:** Resolved severe 1-2 second application hangs inside the Tray Dashboard. The UI serialization layer was attempting to read the names of fast-dying background processes, triggering computationally expensive `ArgumentException` loops. Dead PIDs are now safely cached as `"Terminated"` and instantly bypassed.
- **Auto-Updater API Mismatch:** Corrected the OTA updater target from `NetworkWatchdog` to `LightingWatchdog` to resolve 404 Not Found API errors.
- **TrayApp Silent Crash:** Enforced `/p:PublishSingleFile=true` in the GitHub Actions deployment pipeline for the TrayApp to prevent runtime crashes caused by orphaned framework libraries.

## [3.5.3] - 2026-09-28

### Security & Hardening

- **IPC Pipeline Segregation (Best Practice):** Splintered the monolithic Named Pipe architecture into two dedicated endpoints to permanently resolve the un-elevated `[Access Denied]` connection issue without reintroducing Local Privilege Escalation (LPE) or Denial of Service (DoS) vulnerabilities.
  - `NetworkWatchdogTelemetry`: Granted `ReadWrite` to `AuthenticatedUserSid`. Strictly scoped to process `GET_TELEMETRY` requests, ignoring configuration payloads.
  - `NetworkWatchdogControl`: Granted `ReadWrite` strictly to `BuiltinAdministratorsSid` and `LocalSystem`. Handles all `UPDATE_CONFIG` and whitelisting overrides.

### Fixed

- **Tray App Graceful Elevation Prompts:** If a standard, un-elevated user attempts to modify the threshold slider or adjust the process whitelist via the Tray Dashboard, the app will safely catch the resulting `UnauthorizedAccessException` and render an explicit message box instructing the user to restart the interface as Administrator, preventing silent failures.

## [3.5.2] - 2026-09-28

### Security & Hardening

- **Binary Hijacking Defense:** Resolving utility calls via `Environment.SystemDirectory` (`SystemExe()`) guarantees that binaries like `sc.exe` and `taskkill.exe` are loaded exclusively from the Windows system folder, fully neutralizing executable planting attacks in working directories.
- **Strict SCM Service Identity Enforcement:** Restricting `sc.exe` invocations strictly to names confirmed via WMI (`scmServiceName`), combined with rejecting path delimiters, control characters, leading option flags, and quotes via `IsValidServiceName`, resolves severe service name spoofing and parameter injection vulnerabilities.
- **Bounded IPC Pipeline & Client Timeouts:** Replaced standard `ReadLineAsync` with a custom `ReadBoundedLineAsync` enforcing a strict 64 KB read limit and a 10-second per-connection deadline, preventing unbounded memory growth and resource starvation from malicious or hanging IPC clients.
- **Atomic Temp File Generation:** Replacing predictable temporary file paths with `Guid.NewGuid()` and utilizing `FileMode.CreateNew` effectively eliminates symlink-planting vectors and removes Time-of-Check to Time-of-Use (TOCTOU) gaps during whitelist persistence.
- **Explicit Mask Mapping:** Included explicit integer representations for `GENERIC_WRITE` (`0x40000000`) and `GENERIC_ALL` (`0x10000000`) in the directory ACL validator, ensuring complete bitmask coverage against unmapped ACEs during persistence integrity verification.

### Added

- **State Verification via TryStartServiceAsync:** Overhauled the service restart lifecycle. Converts `sc start` execution into an accepted-intent signal, gracefully handles `1056` (already running) states, and explicitly polls `sc query` every 2 seconds for up to 30 seconds using Regex to strictly confirm a `RUNNING` status.
- **End-to-End Service Restart Tracing:** The worker now produces detailed, multi-phase operational traces across detection, the 30-second `TIME_WAIT` post-kill pause, and the full SCM recovery execution lifecycle.
- **Explicit Failure Diagnostics:** Blind exception handlers have been stripped and replaced with high-fidelity error reporting across all execution branches (unsupported state output, non-zero return codes, timeouts, or failure to launch).

### Fixed

- **Process Stream Drain Deadlocks:** The implementation of `RunScAsync` now reads standard output and standard error concurrently while awaiting process termination. This structurally avoids standard pipe buffer exhaustion deadlocks that previously caused the worker to hang during `sc query`.

## [3.5.1] - 2026-09-28

### Added

- **Native OS Socket Synchronization:** Completely eliminated `netstat.exe` subprocess dependencies. `PeriodicResyncLoopAsync` now queries the Windows kernel directly via P/Invoke `GetExtendedTcpTable`, providing sub-millisecond, dead-lock-free socket baseline resolution to correct ETW telemetry drift.
- **WMI Service Mapping:** Integrated Windows Management Instrumentation (WMI) to automatically map process IDs directly to their registered SCM service names. WMI resolution is optimized via `RefreshServiceNameCacheIfStale` to batch queries and eliminate high-load CPU spikes.
- **LPE Target Boundary & Authenticode Validation (Anti-Malware):** Introduced `IsTrustedExecutablePath` and `IsAuthenticodeSigned`. The watchdog actively blocks path traversal (`..\`) and refuses to relaunch any non-service executable that is not cryptographically signed and housed inside `C:\Windows` or `C:\Program Files`, entirely mitigating Local Privilege Escalation (LPE) vulnerabilities.
- **Cache Pruning Engine:** Added `PruneStaleCaches` to dynamically clear dead PID metadata mappings (`_pidStartTimes`, `_processNameCache`, `_serviceNameCache`), eliminating infinite internal memory growth.
- **Atomic Persistence Engine:** IPC dynamic whitelist modifications are now asynchronously persisted to disk using a fail-safe `.tmp` swap mechanism, preventing `whitelist.json` corruption during mid-write power failures or service crashes.
- **Secure Directory ACLs:** Hardened the `%ProgramData%` persistence folder by proactively stripping inherited permissions (`SetAccessRuleProtection`) and strictly locking `FullControl` to `BuiltinAdministrators` and `LocalSystem`. This closes a vulnerability where standard users could modify the whitelist JSON to exclude malware from watchdog restarts.
- **Multi-Target TFM Compatibility:** Implemented conditional compilation (`#if NET9_0_OR_GREATER`) for Authenticode signature validation, ensuring the codebase compiles natively without obsolete warnings across both legacy .NET 8 environments and modern .NET 9+ SDKs.
- **Concurrency Guard:** Integrated `SemaphoreSlim(1, 1)` to perfectly serialize asynchronous disk I/O operations from rapid, overlapping IPC config updates.

### Fixed

- **ETW Race Conditions:** Synchronized the `HandleAfdEvent` ETW asynchronous channel and `ResyncGlobalBaseline` using a discrete `_connectionCountsLock`, eliminating memory corruption and dropped increments during simultaneous read/write cycles.
- **IPC Access Security (CVE Defense):** Stripped `AuthenticatedUserSid` from the `NamedPipeServerStreamAcl`. The IPC stream is now locked exclusively to `BuiltinAdministratorsSid`, preventing unprivileged local users from tampering with network thresholds or exhausting IPC server instances.
- **IPC Polling Deadlocks:** Refactored `StartNamedPipeServerAsync` to hand off connected pipes to `HandlePipeClientAsync` on independent tasks, guaranteeing simultaneous, non-blocking cross-process telemetry streaming.
- **PID Reuse Race Conditions:** Guarded `Process.StartTime` property retrievals within explicit `TryVerifySamePid` boundaries. The PID identity is now double-verified both before *and* directly after the 30-second mitigation window, ensuring the OS hasn't recycled the PID to an innocent application right before the `taskkill` strike.
- **TCP Table Buffer Exhaustion:** Wrapped native `GetExtendedTcpTable` pointers inside an `ERROR_INSUFFICIENT_BUFFER` loop, ensuring table queries automatically expand unmanaged memory allocations when the Windows connection table surges dramatically between byte calculations.

## [3.5.0] - 2026-09-28

### Added

- **Native OS Socket Synchronization:** Completely eliminated `netstat.exe` subprocess dependencies. `PeriodicResyncLoopAsync` now queries the Windows kernel directly via P/Invoke `GetExtendedTcpTable`, providing sub-millisecond, dead-lock-free socket baseline resolution to correct ETW telemetry drift.
- **WMI Service Mapping:** Integrated Windows Management Instrumentation (WMI) to automatically map process IDs directly to their registered SCM service names (fixing `sc.exe` start failures on shared host processes like `svchost.exe`).
- **LPE Target Boundary (Anti-Malware):** Introduced `IsTrustedExecutablePath` to secure the standalone executable auto-restart feature. If a leaking non-service executable originates from an unprivileged, user-writable directory (e.g., `AppData`), the Watchdog will kill the process to stop the leak but refuse to relaunch it, completely neutralizing Local Privilege Escalation (LPE) attacks.
- **Log Bounding Limit:** Migrated the recent restart history tracker from a limitless `ConcurrentBag` to a capped `ConcurrentQueue`, automatically dequeuing events older than 50 iterations to prevent long-term memory leaks.
- **Cascade Restart Guards:** Added an atomic `_activeRestarts` dictionary to prevent the watchdog from executing duplicate mitigation loops against the same PID during the 30-second socket flush cooldown.

### Fixed

- **ETW High-Throughput Contention:** Decoupled the `HandleAfdEvent` kernel callback loop using an unbounded `System.Threading.Channels` queue. State aggregation is now safely deferred to the `ProcessEtwChannelAsync` background reader to prevent ETW buffer drops during network spikes.
- **IPC Access Security (CVE Defense):** Stripped `AuthenticatedUserSid` from the `NamedPipeServerStreamAcl`. The IPC stream is now locked exclusively to `BuiltinAdministratorsSid`, preventing unprivileged local users from tampering with network thresholds or exhausting IPC server instances.
- **Unmanaged Handle Leaks:** Wrapped all internal `Process.GetProcessById` diagnostics inside explicit `using` blocks to prevent long-term process handle exhaustion within the worker.
- **Safe Command Line Parsing:** Replaced fragile manual substring parsing of Windows command-line arguments with a native P/Invoke wrapper for `CommandLineToArgvW`. This guarantees that recovered standalone executables retain their exact launch arguments.
- **PID Reuse Race Conditions:** Guarded all `Process.StartTime` property retrievals within explicit `try/catch` boundaries. Recycled PIDs that expire between identification and evaluation phases cleanly abort to prevent `InvalidOperationException` pipeline crashes.
- **WMI Performance Lag:** Implemented `_serviceNameCache` and `_processNameCache` to store WMI and process name mappings locally, permanently bypassing CPU-heavy evaluation overhead on fast-churning processes.
- **TIME_WAIT Collision Crashes:** Extended the asynchronous mitigation delay from 15 seconds to 30 seconds. This allows the OS network stack ample time to clear `TIME_WAIT` orphaned sockets, completely eliminating `WSAEADDRINUSE` port conflict crashes when mitigated applications restart.
- **ETW Teardown Orphans:** Fixed critical ETW unhandled teardown exceptions by invoking `_etwSession.Stop()` precisely prior to trace disposal inside the background cancellation register.

## [3.4.2] - 2026-09-26

### Fixed

- **Critical Fix:** Resolved a severe unmanaged GDI memory leak in `NetworkWatchdog.TrayApp` that caused fatal `Exit Code 1` crashes after ~2 hours of continuous operation due to Win32 USER handle exhaustion.
- Transitioned TrayApp from generating `Bitmap.GetHicon()` dynamically per-tick to caching UI colors and fonts at startup.
- Implemented `user32.dll DestroyIcon` P/Invoke to enforce strict memory teardown of `HICON` pointers.
- Enforced deterministic `.Dispose()` garbage collection on `ContextMenuStrip` structures during UI repaints.

### Security & Resilience

- **Handle Hygiene:** Integrated `user32.dll GetGuiResources` P/Invoke to enforce a strict 200-handle guardrail (`GR_GDIOBJECTS` / `GR_USEROBJECTS`), logging diagnostic warnings before Windows subsystem limits are breached.
- **Named Pipe Resiliency:** Enforced strict 1,000ms and 500ms connection timeouts on all `NamedPipeClientStream` requests to prevent UI thread lockups and orphaned handles.
- **Crash Resilience:** Wired `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` to a global `LogFatalError` handler, capturing complete stack traces to `tray_crash.log` instead of failing with silent exit codes.
- **Anti-Tampering (CLI):** Replaced vulnerable string concatenation in `taskkill` invocations with `ProcessStartInfo.ArgumentList` to completely eliminate parameter injection vectors.
- **Payload Validation:** Clamped the global threshold slider rigidly between 200 and 10,000 via `Math.Clamp`, and enforced strict Regex (`^[a-zA-Z0-9_\-\.]+$`) on process whitelisting to block path traversal and shell-escape characters.

## [3.4.1] - 2026-09-24

### Added

- Embedded asynchronous `NamedPipeServerStream` (`NetworkWatchdogPipe`) directly inside `Worker.cs` to supply live telemetry snapshots and receive dashboard control commands.

### Fixed

- Resolved `CS0649` compiler warning in `NetworkWatchdog.TrayApp` by properly assigning `_csvHealthPath` inline for fallback telemetry polling.
- Removed obsolete unused restart CSV fields from the Tray Application context.
- Resolved git repository index lock contention (`main.lock`) during automated commit and release sequences in cloud-synchronized local folders.
- Fixed inter-process communication failures between the headless Windows Service (`LocalSystem`) and the desktop Tray App by introducing explicit `PipeSecurity` rules allowing `WorldSid` and `AuthenticatedUserSid` access.
- Increased Tray App pipe connection timeout to 1000 ms to ensure smooth handshakes without premature fallback drops.
- Resolved SCM service startup timeout (Error `1053` / `CLR20r3 FileNotFoundException`) by bundling Windows hosting assemblies using `<CopyLocalLockFileAssemblies>` and targeting `-r win-x64` single-file publishing.

## [3.4.0] - 2026-09-24

### Added

- Implemented an asynchronous Named Pipe IPC server (`NetworkWatchdogPipe`) in `Worker.cs` for sub-millisecond, zero-disk I/O telemetry streaming.
- Updated `NetworkWatchdog.TrayApp` to consume live telemetry directly via Named Pipes with transparent CSV file fallback.
- Added a "Settings & Whitelist" tab to `DashboardForm` featuring a real-time `GlobalMaxTcpConnections` threshold slider and dynamic process whitelisting.
- Implemented `SyslogNotifier.cs` to forward socket breach and mitigation alerts via UDP to remote syslog hosts or container instances.

## [3.3.0] - 2026-09-24

### Added

- Created an interactive Windows Forms telemetry dashboard (`DashboardForm`) accessible via tray icon double-click or the "Open Dashboard" context menu.
- Integrated a live data grid displaying monitored processes, PIDs, active connection counts, and status indicators.
- Added a "Terminate Process Tree" action button and dynamic context-menu item for manual kill actions.
- Added a dedicated "Historical Mitigations" tab parsing `RestartEvents_v2.csv` directly in the UI.

## [3.2.0] - 2026-09-24

### Added

- Implemented universal process monitoring (`MonitorAllProcesses` and `GlobalMaxTcpConnections`) in `WatchdogConfig` and `Worker.cs` to safeguard the entire system against rogue socket leaks from any executable.
- Added real-time CSV restart parsing and native Windows balloon notifications in `NetworkWatchdog.TrayApp` when socket thresholds are breached and mitigated.
- Added a toggleable "Silent Mode" option in the system tray context menu to suppress pop-up alerts on demand.

## [3.1.0] - 2026-09-23

### Added

- Introduced `NetworkWatchdog.TrayApp`, a lightweight companion system tray monitor for user sessions.
- Implemented real-time dynamic tray icon coloring (Green for normal, Orange for elevated, Red for critical socket leaks) based on telemetry CSV polling.
- Added interactive tooltips showing live process IDs, service names, and active connection counts upon hovering over the tray icon.

### Fixed

- Resolved `CS0104` compiler ambiguity error between `System.Windows.Forms.Timer` and `System.Threading.Timer` in `src/NetworkWatchdog.TrayApp/Program.cs` by fully qualifying the timer instance.
- Corrected a filename typo, renaming `etworkWatchdog.TrayApp.csproj` to `NetworkWatchdog.TrayApp.csproj`.
- Fully qualified all references to `System.Windows.Forms.Timer` in `TrayApplicationContext` to completely resolve `CS0104` ambiguity errors.

### Changed

- Synchronized full directory trees in `README.md`, to accurately reflect the new multi-project `src/` structure containing both the Worker Service and the Tray App without truncating legacy directories.
- Updated project capability descriptions to officially document the graphical system tray monitoring features.
- Moved `README.md` from the `docs/` directory to the repository root to align with standard GitHub repository practices and enable automatic landing page rendering.
- Removed the obsolete `docs/` folder.
- Optimized the `README.md` Description section by replacing the verbose bullet lists with a condensed `Feature Milestones` summary, perfectly aligning feature additions with their historical release tags (`v2.5.x` through `v3.1.x`) and significantly reducing visual clutter.

## [3.0.1] - 2026-09-21

### Added

- Implemented proactive PID filtering in the ETW `TraceEventSession` callback to discard irrelevant Windows socket events and optimize CPU footprint.
- `TelemetryExporter.cs` to natively export `HealthTrend_v2.csv` and `RestartEvents_v2.csv` snapshots directly from the C# worker, replacing the legacy PowerShell telemetry logic.
- `scripts/Install-Service.ps1` to compile, publish, and register the .NET Worker Service to the Windows Service Control Manager with automatic restart failure recovery.
- `scripts/Uninstall-Service.ps1` to safely stop and remove the Windows Service.
- Fixed a critical ETW tracking bug in `Worker.cs` where the socket counter was artificially inflated by +3 per connection due to matching multiple stages of a single socket's lifecycle (`Create`, `Bind`, `Connect`). The tracker now enforces a strict 1:1 mapping using only `AfdCreate` and `AfdClose`.
- `scripts/Setup-GitHooks.ps1` to automatically generate and install a Git `pre-commit` hook that blocks commits if the C# `.csproj` fails to compile.

### Fixed

- Fixed ETW event name string matching in `Worker.cs` to correctly intercept `AfdCreate/Open`, `AfdBindWithAddress/Open`, and `AfdConnectWithAddress/Bound` kernel events.
- Removed `[DEBUG ETW]` console logger now that Winsock event names are properly resolved.
- Fixed a critical initialization bug where running as a Windows Service caused the application to look in `C:\Windows\System32` for `appsettings.json`. The base path is now explicitly forced to the executable's directory.
- Changed ETW event targets to `AfdConnect` and `AfdAccept` to correctly track real-time socket creation.
- Reordered `Install-Service.ps1` to stop the Windows Service prior to running `dotnet publish`, resolving file lock access violations.
- Added `Directory.SetCurrentDirectory` to `Program.cs` to ensure the Service Control Manager reads `appsettings.json` from the application directory instead of `C:\Windows\System32`.
- **ETW Session Lifecycle:** Resolved a critical bug where premature disposal of the `TraceEventSession` inside a synchronous `using` block blinded the background worker.
- **Thread Starvation:** Unblocked the main health evaluation loop by wrapping ETW event processing in an asynchronous background task (`Task.Run`), ensuring the 1000-socket threshold checker reliably triggers the process recovery and socket flushing sequence.

## [3.0.0] - 2026-09-19

### Added

- Scaffolded the foundation for a C# .NET 8 Worker Service (`NetworkWatchdogService`).
- Created strongly-typed configuration models (`WatchdogConfig`, `MonitoredService`) to map JSON settings.
- Integrated `Microsoft.Extensions.Hosting.WindowsServices` for native Windows Background Service installation.
- Added `ConcurrentDictionary<int, int>` to track real-time per-PID connection metrics.
- Integrated `Microsoft.Diagnostics.Tracing.TraceEvent` for zero-overhead kernel network monitoring via `Microsoft-Windows-Winsock-AFD`.
- Implemented real-time tracking for asynchronous TCP events using dynamic event parsing for raw Winsock socket allocations.
- Implemented a one-time `netstat -ano` baseline initialization for newly discovered PIDs to track pre-existing leaked sockets.
- Added `RestartLeakingService` method to trigger a 15-second socket flush wait and aggressive process tree termination.

### Changed

- Initiated the architectural transition from PowerShell polling to a high-performance, compiled C# service.

### Fixed

- Resolved C# nullable reference (`CS8618`) and delegate mismatch (`CS0123`) compiler constraints for `TraceEventSession`.
- Fixed missing braces in `HandleAfdEvent` causing `CS1513` and `CS1022` compiler errors.

### Removed

- Deleted `NativeMethods.cs` and the legacy WMI polling subprocess to drastically reduce CPU overhead.

## [2.7.3] - 2026-09-06

### Changed

- Reverted default `MaxTcpConnections` threshold for `LightingService` back to `1000` in `config.json` to prevent premature triggers and optimize system resource efficiency.

## [2.7.2] - 2026-09-06

### Fixed

- Enforced array typing in `Test-Quarantine` to prevent `op_Addition` datetime cast errors when processing sequential restart events.

## [2.7.1] - 2026-09-06

### Fixed

- Resolved CSV schema conflicts by versioning export files (`diagnostics_v2.csv`, `HealthTrend_v2.csv`, `RestartEvents_v2.csv`) to avoid ghost process locks from previous watchdog versions.

## [2.7.0] - 2026-09-06

### Added

- Abstracted hardcoded `LightingService` references into a dynamic `MonitoredServices` array in `config.json`.
- Transformed the script into a universal watchdog capable of policing multiple independent process trees simultaneously.
- Updated `Invoke-Diagnostics` to generate a dynamic `ServiceStates` array instead of flat service properties.
- Rewrote the watchdog restart logic to dynamically target the failing service from the new array structure.

## [2.6.1] - 2026-09-05

### Changed

- Removed all GUI popups (`System.Windows.Forms` and `Wscript.Shell`) to prevent AFK thread freezing.
- Routed all alerts exclusively to the PowerShell console (`Write-Host`) and `.log` files for fully autonomous, headless operation.

## [2.6.0] - 2026-09-03

### Added

- Implemented process tree termination (`taskkill /F /T`) to aggressively shut down `LightingService` and any orphaned child processes (e.g., `AuraService`).
- Added a strict 15-second TCP flush wait-loop to ensure the Windows kernel drops `TIME_WAIT` sockets before the service is allowed to restart.

### Fixed

- Resolved a critical pipeline leakage issue causing `Write-Log : Cannot process argument transformation on parameter 'File'` by enforcing strict type handling on the `$logFile` path.
- Prevented false restarts by wrapping the aggressive shutdown sequence inside the proper Cooldown and Quarantine conditional blocks.

---

## [2.5.4] - 2026-08-19

### Fixed

- Corrected watchdog cycle timing by measuring the full interval including the sleep phase.
- Eliminated false CLOCK DRIFT warnings caused by measuring only diagnostic execution time.
- Stabilized module imports in PowerShell 7 using ordered global imports in NetworkDiag.ps1.
- Ensured all modules (Utils, Trends, Diagnostics, Watchdog) load consistently in -File execution mode.
- Fixed Write-Log path handling by replacing incorrect implementation with a safe, positional-parameter-aware version.

### Changed

- Reorganized Start-Watchdog loop structure for predictable timing and stable drift detection.
- Updated NetworkDiag.ps1 to use deterministic module import order and global scope imports.
- Updated Write-Log to correctly resolve relative paths under ..\..\logs and handle parameter swapping.

### Notes

- Logging errors caused by invalid file paths (e.g., "OK") are now resolved.

## [2.5.3] — 2026‑08‑19

### Stability & Path‑Safety Release

This version delivers a full path‑safety refactor across all modules, eliminating
DriveNotFound errors, inconsistent logging behavior, and working‑directory
dependencies. All modules now use `$PSScriptRoot` for deterministic path
resolution, making the entire system stable under Task Scheduler, batch
wrappers, and manual execution.

### Added

- Unified `Write-Log -File -Message` API across all modules.
- Absolute path resolution for:
  - logs/
  - logs/export/
  - logs/heartbeat.json
  - config/config.json
- Hardened module imports using `Join-Path $PSScriptRoot`.

### Changed

- Diagnostics, Watchdog, Trends, and Utils modules rewritten to remove all relative paths.
- NetworkDiag.ps1 updated to use absolute module imports.
- Restart event logging and heartbeat updates now use stable absolute paths.
- Improved consistency of JSON and CSV export behavior.

### Fixed

- `DriveNotFoundException` caused by relative paths resolving incorrectly when the working directory contained prefixes like `OK`.
- CSV append issues under certain execution contexts.
- Watchdog drift detection occasionally reporting incorrect cycle durations.
- Webhook payload inconsistencies for restart events.

### Notes

No configuration changes required. Existing `config.json` remains fully compatible.

## [2.5.2] - 2026-08-19

### Fixed

- Replaced all relative module imports with `$PSScriptRoot` for full path stability.
- Corrected `config.json` resolution using absolute path via `Join-Path`.
- Eliminated all remaining `..\scripts\Modules\...` references.
- Resolved module loading failures when launched from `.bat`, Task Scheduler, or non-root directories.
- Fixed incorrect working directory inheritance inside modules.
- Ensured consistent behavior across PowerShell 5.1 and PowerShell 7+.

### Improved

- Hardened watchdog execution environment.
- Made project fully location-independent.
- Strengthened log and export folder creation logic.

---

## [2.5.1] - 2026-08-19

### Fixed

- Replaced colon-based ISO timestamps with filesystem-safe format.
- Implemented manual standard deviation calculation for PowerShell 5.1.
- Ensured `logs/` folder is always created before writing.

---

## [2.5.0] - 2026-08-19

### Added

- ISO-8601 timestamps with optional UTC mode.
- Heartbeat system (`heartbeat.json`).
- Clock drift detection.
- Quarantine mode for LightingService.
- Auto-kill for runaway processes.
- Watchdog health scoring.
- Predictive anomaly alerts.

### Improved

- Diagnostics export structure.
- Trend analysis stability.

---

## [2.4.1] - 2026-08-19

### Added

- Leak growth rate calculation.
- Restart cooldown logic.
- Webhook notifications.
- Trend analysis improvements.

### Fixed

- Timestamp parsing issues.
- Incorrect leak detection edge cases.

---

## [2.4.0] - 2026-08-19

### Added

- Full JSON diagnostics export.
- Full CSV diagnostics export.
- HealthTrend.csv rolling trend export.
- RestartEvents.csv logging.

### Improved

- Health scoring model.
- Nonpaged pool fallback logic.

---

## [2.3.0] - 2026-08-19

### Added

- Storm detection (TCP/UDP spikes).
- Trend window configuration.
- Rolling average and Z-score anomaly detection.

### Improved

- Leak detection threshold logic.
- Logging format consistency.

---

## [2.2.0] - 2026-08-18

### Added

- Modular architecture (`Utils`, `Diagnostics`, `Trends`, `Watchdog`).
- Config-driven thresholds.
- Popup alert system.

### Improved

- TCP/UDP measurement accuracy.
- LightingService PID resolution.

---

## [2.1.0] - 2026-08-18

### Added

- Basic watchdog loop.
- Automatic restart of LightingService.
- Cooldown between restarts.

### Improved

- Logging timestamps.
- Error handling around service restart.

---

## [2.0.0] - 2026-08-18

### Major Release

- Introduced multi-module design.
- Added structured logging.
- Added nonpaged pool monitoring.
- Added connection leak detection.

---

## [1.5.0] - 2026-08-18

### Added

- First version of health scoring.
- Basic trend tracking.
- Initial CSV export.

---

## [1.2.0] - 2026-08-18

### Added

- LightingService connection counting.
- TCP/UDP baseline metrics.
- Basic leak threshold detection.

---

## [1.1.0] - 2026-08-18

### Added

- Log folder creation.
- Timestamped log files.
- Error-safe Get-NetTCPConnection wrapper.

---

## [1.0.0] - 2026-08-17

### Initial Release

- Basic network diagnostics.
- TCP/UDP connection counting.
- Simple text log output.
- Manual execution only.
