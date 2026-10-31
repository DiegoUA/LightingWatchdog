# LightingWatchdog

Legacy PowerShell diagnostic and watchdog toolkit for the ASUS `LightingService` socket leak (v1.0.0 to v2.7.3).

> **Status: legacy.** This toolkit has been superseded by **[NetworkWatchdog](https://github.com/DiegoUA/NetworkWatchdog)**, the compiled C# application (Windows service, tray dashboard, installer) that grew out of it. This repository keeps the PowerShell original for ad-hoc diagnosis and for machines where the service is not installed. It is started manually only.

---

## 🧬 Lineage

* **v1.x and v2.x** (this repository): the PowerShell toolkit.
* **v3.0.0 onward**: the C# application, developed and released in [DiegoUA/NetworkWatchdog](https://github.com/DiegoUA/NetworkWatchdog). The version numbers continue (they were not restarted) so that installed copies of the application can compare versions when they check for updates.
* The last PowerShell release is **v2.7.3**. The history of the C# application is documented in the NetworkWatchdog repository.

---

## ⚙️ Description

**LightingWatchdog** was built to tame `LightingService.exe` (ASUS Armoury Crate), which leaks TCP/UDP sockets. The toolkit measures connections, nonpaged pool pressure and connection storms, scores the health of the monitored service and, in watchdog mode, terminates the leaking process tree, waits for the kernel to drop its sockets and restarts the service.

Since v2.7.0 it is not tied to one service: the `MonitoredServices` array in `config/config.json` describes any number of independent services and process trees.

---

## 🚀 Usage

Requirements: Windows, PowerShell 5.1 or PowerShell 7+. Diagnostics and the watchdog's detection run in a normal PowerShell, but stopping, killing and restarting the service needs an **elevated** PowerShell: without it the watchdog prints `Could not kill ... Run as Administrator`.

```powershell
# One-off diagnostic (single run, writes the exports described below)
.\scripts\NetworkDiag.ps1

# Watchdog loop (runs until stopped)
.\scripts\NetworkDiag.ps1 -Watchdog
```

The script resolves its own folder, so it can be started from any working directory, from `.bat` wrappers or from Task Scheduler (`-File` execution is supported). If script execution is blocked on your machine, start it with `powershell -ExecutionPolicy Bypass -File .\scripts\NetworkDiag.ps1 -Watchdog`.

> **Known limitation:** when the kill fails because PowerShell is not elevated, the script reports the error but still continues the restart sequence and prints a "restarted" message even though the leak is still there. Always start the watchdog elevated.

> **Do not run the watchdog loop at the same time as the NetworkWatchdog service against the same process**: two mitigators would compete.

---

## 🔧 Configuration

All settings live in `config/config.json`.

| Group | Keys |
| :--- | :--- |
| Detection | `LeakThreshold`, `NonPagedPoolThreshold`, `StormThreshold`, `TrendWindow`, `EnablePredictiveAlerts` |
| Watchdog timing | `WatchdogIntervalSeconds`, `CooldownSeconds`, `ClockDriftThresholdSeconds` |
| Quarantine | `EnableQuarantine`, `QuarantineRestartLimit`, `QuarantineWindowMinutes`, `QuarantineDurationMinutes` |
| Auto-kill of runaway processes | `EnableAutoKill`, `AutoKillProcesses`, `AutoKillCpuThreshold`, `AutoKillMemThresholdMB` |
| Webhooks | `EnableWebhooks`, `WebhookUrl`, `WebhookMinHealthScore` |
| Exports and logging | `EnableJsonExport`, `EnableCsvExport`, `MaxLogFiles`, `TimestampFormat`, `UseUtc` |
| Health score | `Weights` (`ServiceLeak`, `NonPagedPressure`, `Storm`, `TcpLoad`, `UdpLoad`) |
| Monitored services | `MonitoredServices[]` with `ServiceName`, `ProcessTree`, `MaxTcpConnections` (default `1000`), `EnableRestart` |

GUI popups were removed in v2.6.1: alerts go to the PowerShell console and to the log files, so the toolkit can run unattended. `EnablePopups` is still present in `config.json` for backward compatibility and is ignored.

The shipped default monitors `LightingService` with the process tree `LightingService` + `AuraService`.

---

## 📊 Output

Everything is written under `logs/` (created at runtime, not tracked by git). The exports are in `logs/export/`: `diag_*.json`, `diagnostics_v2.csv` and `HealthTrend_v2.csv`. Restarts are recorded in `RestartEvents_v2.csv`, and `logs/heartbeat.json` and the log files are written there too. The `_v2` suffix was introduced in v2.7.1 to avoid file-lock and schema conflicts with files left by older versions.

---

## 📂 Project Structure

```text
LightingWatchdog/
    ├── config/
    │   └── config.json
    ├── scripts/
    │   ├── Modules/
    │   │   ├── Diagnostics.psm1
    │   │   ├── Trends.psm1
    │   │   ├── Utils.psm1
    │   │   └── Watchdog.psm1
    │   └── NetworkDiag.ps1
    ├── .gitignore
    ├── CHANGELOG.md
    ├── LICENSE
    └── README.md
```

---

## 🧩 Feature Milestones

* **v2.5.x**: Configurable thresholds, JSON/CSV exports, health scoring, leak growth tracking, webhook notifications, quarantine mode, and auto-kill for runaway processes.
* **v2.6.x**: Aggressive process tree termination (`taskkill`) and safe TCP `TIME_WAIT` kernel flush loops.
* **v2.7.x**: Dynamic multi-service monitoring arrays and fully headless execution.

| Version | Date | Description |
| :--- | :--- | :--- |
| **v1.0.0** | 2026‑08‑17 | Baseline diagnostic script. |
| **v2.0.0** | 2026‑08‑18 | Modular architecture, config file, unified logging, service abstraction. |
| **v2.5.0** | 2026‑08‑19 | Heartbeat, clock drift detection, quarantine mode, auto-kill, health scoring, predictive alerts. |
| **v2.6.0** | 2026‑09‑03 | Process tree termination and a 15-second TCP flush wait before restart. |
| **v2.7.0** | 2026‑09‑06 | Universal watchdog: dynamic `MonitoredServices` array. |
| **v2.7.3** | 2026‑09‑06 | Last PowerShell release. |

*(For full patch details, view `CHANGELOG.md`. For the C# application, see [NetworkWatchdog](https://github.com/DiegoUA/NetworkWatchdog).)*

---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.
