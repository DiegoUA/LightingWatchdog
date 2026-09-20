# Changelog

All notable changes to LightingWatchdog are documented here.

---
## [Unreleased]
### Added
- Implemented proactive PID filtering in the ETW `TraceEventSession` callback to discard irrelevant Windows socket events and optimize CPU footprint.
- `TelemetryExporter.cs` to natively export `HealthTrend_v2.csv` and `RestartEvents_v2.csv` snapshots directly from the C# worker, replacing the legacy PowerShell telemetry logic.

### Fixed
    - Fixed ETW event name string matching in `Worker.cs` to correctly intercept `AfdCreate/Open`, `AfdBindWithAddress/Open`, and `AfdConnectWithAddress/Bound` kernel events.
    - Removed `[DEBUG ETW]` console logger now that Winsock event names are properly resolved.

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