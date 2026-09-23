# LightingWatchdog

Advanced Windows network diagnostic and self-healing watchdog for ASUS LightingService leaks.

---

## 📂 Project Structure

```text
LightingWatchdog/
    │
    ├── .github/
    │   └── workflows/
    │       └── release.yml
    │
    ├── bin/
    │
    ├── config/
    │   └── config.json
    │
    ├── logs/
    │   ├── export/
    │   ├── .gitkeep
    │   └── heartbeat.json
    │
    ├── scripts/
    │   ├── Modules/
    │   │   ├── Diagnostics.psm1
    │   │   ├── Trends.psm1
    │   │   ├── Utils.psm1
    │   │   └── Watchdog.psm1
    │   ├── Install-Service.ps1
    │   ├── NetworkDiag.ps1
    │   ├── Setup-GitHooks.ps1
    │   └── Uninstall-Service.ps1
    │
    ├── src/
    │   ├── NetworkWatchdogService/
    │   │   ├── bin/
    │   │   ├── Models/
    │   │   │   └── WatchdogConfig.cs
    │   │   ├── obj/
    │   │   ├── Properties/
    │   │   ├── appsettings.Development.json
    │   │   ├── appsettings.json
    │   │   ├── NetworkWatchdogService.csproj
    │   │   ├── Program.cs
    │   │   ├── TelemetryExporter.cs
    │   │   └── Worker.cs
    │   │
    │   └── NetworkWatchdog.TrayApp/
    │       ├── NetworkWatchdog.TrayApp.csproj
    │       └── Program.cs
    │
    ├── .gitignore
    ├── CHANGELOG.md
    ├── LICENSE
    └── README.md
```

## ⚙️ Description

LightingWatchdog is a modular Windows diagnostic and watchdog system designed to detect and mitigate TCP/UDP socket leaks, runaway connection storms, nonpaged pool exhaustion, and kernel memory pressure.

**Feature Milestones:**

- **v2.5.x:** Configurable thresholds, JSON/CSV exports, health scoring, leak growth tracking, webhook notifications, quarantine mode, and auto-kill for runaway processes.
- **v2.6.x:** Aggressive process tree termination (taskkill) and safe TCP TIME_WAIT kernel flush loops.
- **v2.7.x:** Dynamic multi-service monitoring arrays and fully headless execution.
- **v3.0.x:** Native C# .NET 8 Worker Service using zero-overhead Event Tracing for Windows (ETW) for kernel-level socket tracking.
- **v3.1.x:** `NetworkWatchdog.TrayApp` graphical companion for real-time system tray monitoring (color-coded health icons and interactive tooltips).

### ⚡ Core Architecture: Zero-Overhead Monitoring

LightingWatchdog (v3.x+) utilizes a hybrid state-tracking architecture:

1. **Initial Baseline:** Upon discovering a monitored process, the service executes a single, lightweight `netstat` snapshot to capture pre-existing socket leaks.
2. **Zero-Overhead Tracking:** Ongoing monitoring integrates directly into the Windows Kernel via **Event Tracing for Windows (ETW)**. By subscribing to the `Microsoft-Windows-Winsock-AFD` provider, the service listens asynchronously to raw socket allocations in real-time, adding them to the baseline. This guarantees zero CPU overhead while ensuring rogue local handles are tracked accurately.

---

## 🧩 Version History

| Version | Date | Description |
| --- | --- | --- |
| **v1.0** | 2026‑08‑17 | Baseline diagnostic script. |
| **v1.1** | 2026‑08‑18 | LightingService leak detection. |
| **v1.2** | 2026‑08‑18 | Auto-restart logic. |
| **v1.3** | 2026‑08‑18 | Popup notifications. |
| **v1.4** | 2026‑08‑18 | Nonpaged pool monitoring. |
| **v1.5** | 2026‑08‑18 | WebSocket storm detection. |
| **v1.6** | 2026‑08‑18 | Continuous watchdog loop + log rotation. |
| **v2.0** | 2026‑08‑18 | Modular architecture, config file, unified logging, service abstraction. |
| **v2.1** | 2026‑08‑18 | Added JSON and CSV export of diagnostic runs. |
| **v2.2** | 2026‑08‑18 | Service Health Score (0–100). |
| **v2.3** | 2026‑08‑19 | Approved verbs, trend engine, anomaly detection, HealthTrend export. |
| **v2.4** | 2026‑08‑19 | Restart tracking, leak growth rate, kernel trend, cooldown, webhook notifications. |
| **v2.4.1** | 2026‑08‑19 | Fixed timestamp parsing using ParseExact; stabilized leak growth rate; added full restart logic, cooldown, webhook support. |
| **v2.5** | 2026‑08‑19 | ISO‑8601 timestamps, UTC mode, heartbeat, clock drift detection, quarantine, auto‑kill, watchdog health. |
| **v2.5.1** | 2026‑08‑19 | Filesystem-safe timestamps, PS 5.1 stddev fix, ensured logs folder creation. |
| **v2.5.2** | 2026‑08‑19 | **Absolute path stability**, `$PSScriptRoot` module imports, correct config resolution, fully location‑independent execution, hardened module loading. |
| **v2.5.3** | 2026‑08‑19 | Unified `Write-Log -File -Message`, full path‑safety rewrite, stable heartbeat/export paths, corrected webhook payloads, eliminated DriveNotFound errors. |
| **v2.5.4** | 2026‑08‑19 | Fixed watchdog cycle timing, stabilized module imports, corrected Write-Log path handling, improved continuous-mode reliability. |
| **v2.6.0** | 2026‑09‑03 | Process tree termination (taskkill), TIME_WAIT socket flush cooldown loop, and pipeline type-safety fixes for logging module. |
| **v2.6.1** | 2026‑09‑05 | Removed GUI popups for fully autonomous headless operation. |
| **v2.7.0** | 2026‑09‑06 | Abstracted configuration to support dynamic multi-service monitoring arrays. |
| **v2.7.1** | 2026‑09‑06 | Resolved CSV schema conflicts by versioning export files. |
| **v2.7.2** | 2026‑09‑06 | Fixed op_Addition crash during sequential leak restarts. |
| **v2.7.3** | 2026‑09‑06 | Restored optimal MaxTcpConnections threshold to 1000. |
| **v3.0.0** | 2026‑09‑19 | Architectural shift: Scaffolded C# .NET 8 Worker Service for native Windows Service integration. Replaced legacy polling with zero-overhead ETW kernel tracing, baseline socket initialization, and aggressive taskkill restart loop. |
| **v3.0.1** | 2026‑09‑21 | Fixed SCM working directory pathing, corrected ETW Winsock AFD event mappings (AfdConnect/AfdAccept), and resolved Install-Service.ps1 file locking issues. |
| **v3.1.0** | 2026‑09‑23 | Added NetworkWatchdog.TrayApp companion for user session tray monitoring, real-time status coloring, and synchronized directory trees across documentation. |

## Key Features

- Leak detection and restart with cooldown for any configured service
- Kernel nonpaged pool monitoring
- WebSocket storm detection
- Health scoring and trend analysis
- Leak growth rate
- Nonpaged trend
- ISO-like timestamps (filesystem-safe)
- Optional UTC mode
- Watchdog heartbeat (`logs/heartbeat.json`)
- Clock drift detection
- Quarantine mode for all monitored services
- Auto-kill for runaway processes
- JSON + CSV exports
- Optional webhook notifications
- Headless autonomous console execution

---

## 🚀 Usage

Single diagnostic pass:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/NetworkDiag.ps1
```

Continuous watchdog mode:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/NetworkDiag.ps1 -Watchdog
```

---

## 📊 Exports

LightingWatchdog automatically creates the following files during operation:

JSON diagnostics: logs/export/diag_*.json

Diagnostics CSV: logs/export/diagnostics.csv

Health trend CSV: logs/export/HealthTrend.csv

Restart events CSV: logs/export/RestartEvents.csv

Watchdog heartbeat: logs/heartbeat.json

---

## ⚙️ Configuration

LightingWatchdog utilizes specific configuration files depending on the active engine:

### 1. PowerShell Watchdog (v2.x)

Edit thresholds, webhooks, and cooldown logic for the script-based monitor here:

```json
config/config.json.
```

### 2. .NET Background Service (v3.x+)

The native C# service relies on the app settings to define process trees, TCP limits, and cooldown intervals natively in .NET. Edit targets here:

```json
src/NetworkWatchdogService/appsettings.json
```

---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.
