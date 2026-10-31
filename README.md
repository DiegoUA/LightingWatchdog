# LightingWatchdog

PowerShell diagnostic and watchdog toolkit for the ASUS `LightingService` socket leak (current version: v2.7.4).

> **Status: maintained.** This is the lightweight option for a PC where only `LightingService.exe` (ASUS Armoury Crate) needs to be handled: no installation, started by double-clicking `Start-LightingWatchdog.bat`. It keeps receiving fixes and occasional improvements. For general, always-on protection of every application on the machine, see its sibling project **[NetworkWatchdog](https://github.com/DiegoUA/NetworkWatchdog)**, the compiled C# application (Windows service, tray dashboard, installer) that grew out of this toolkit and is the primary project.

---

## 🧬 Lineage

* **v1.x and v2.x** (this repository): the PowerShell toolkit. It stays on the 2.x line and is developed independently of the C# application.
* **v3.0.0 onward**: the C# application, developed and released in [DiegoUA/NetworkWatchdog](https://github.com/DiegoUA/NetworkWatchdog). Its version numbers continued from this project (they were not restarted) so that installed copies of the application could compare versions when they check for updates.
* The current PowerShell version is **v2.7.4**. The history of the C# application is documented in the NetworkWatchdog repository.

---

## ⚙️ Description

**LightingWatchdog** was built to tame `LightingService.exe` (ASUS Armoury Crate), which leaks TCP/UDP sockets. The toolkit measures connections, nonpaged pool pressure and connection storms, scores the health of the monitored service and, in watchdog mode, terminates the leaking process tree, waits for the kernel to drop its sockets and restarts the service.

Since v2.7.0 it is not tied to one service: the `MonitoredServices` array in `config/config.json` describes any number of independent services and process trees.

---

## 🚀 Usage

Requirements: Windows, PowerShell 5.1 or PowerShell 7+.

### The easy way: `Start-LightingWatchdog.bat`

Double-click it and choose:

| Choice | What it does | Rights |
| :--- | :--- | :--- |
| **1. Monitor only** | Watches the service, reports and logs a leak once per episode, **never stops, kills or restarts anything**. | Normal user |
| **2. Monitor and manage** | Also kills the leaking process tree, waits for the kernel to drop its sockets, restarts the service, and runs auto-kill. | Administrator (the launcher asks Windows for it) |
| **3. One diagnostic scan** | A single scan with the exports described below, then exits. | Normal user |

The launcher also accepts a mode on the command line, which is handy for Task Scheduler: `Start-LightingWatchdog.bat monitor`, `... manage` or `... scan`. For `manage` in a scheduled task, enable *Run with highest privileges*; no prompt appears when it is already elevated.

> **Do not click inside the watchdog window.** Windows then pauses the console (QuickEdit mode) until you press Enter or Esc.

### Windows shows a security warning when I start the launcher

When you run `Start-LightingWatchdog.bat` for the first time, Windows may show *"Open File - Security Warning: the publisher could not be verified"*. This is expected and does not mean the file is dangerous.

* **Why it appears.** Windows marks everything you download from the internet (including this ZIP and the files extracted from it) as "from the internet", and asks before running unsigned programs from there. A batch file cannot carry a digital signature at all, so no certificate, paid or free, could remove this prompt for the launcher. Signing the PowerShell scripts would cost a certificate every year, and the free signing programs for open-source projects require an OSI-approved license, which this non-commercial license (CC BY-NC 4.0) is not. LightingWatchdog is freeware, so it is shipped unsigned.
* **Why it is safe to run.** The launcher and every script are plain, readable text: open them in Notepad or VS Code before running anything. The launcher is about 100 lines and only starts the PowerShell scripts in the `scripts` folder next to it. It installs nothing, changes no Windows setting and contacts no server (a webhook is only called if you enable `EnableWebhooks`). It starts PowerShell with `-ExecutionPolicy Bypass` for that one process only, so your system-wide execution policy stays untouched. Administrator rights are requested only for option 2, and Windows shows its own confirmation first.
* **How to avoid the prompt.** Before extracting, right-click the downloaded ZIP, choose *Properties*, tick *Unblock* and click OK; files extracted afterwards carry no mark. For an already extracted folder run `Get-ChildItem -Recurse -File "C:\path\to\LightingWatchdog" | Unblock-File` in PowerShell. A copy obtained with `git clone` has no mark either. Or simply click **Run**.

### Starting the script directly

```powershell
# One-off diagnostic (single run, writes the exports described below)
.\scripts\NetworkDiag.ps1

# Watchdog loop that only reports and logs (no elevation needed)
.\scripts\NetworkDiag.ps1 -Watchdog -MonitorOnly

# Watchdog loop that also restarts a leaking service (needs an elevated PowerShell)
.\scripts\NetworkDiag.ps1 -Watchdog
```

Stopping, killing and restarting a service needs an **elevated** PowerShell. When the full watchdog starts without it, it prints a warning, and if a kill then fails it reports `RESTART FAILED`, starts nothing, records no restart and tries again after the cooldown.

The script resolves its own folder, so it can be started from any working directory, from `.bat` wrappers or from Task Scheduler (`-File` execution is supported). If script execution is blocked on your machine, start it with `powershell -ExecutionPolicy Bypass -File .\scripts\NetworkDiag.ps1 -Watchdog`.

> **Do not run the watchdog loop at the same time as the NetworkWatchdog service against the same process**: two mitigators would compete.

---

## 🔧 Configuration

All settings live in `config/config.json`.

| Group | Keys |
| :--- | :--- |
| Detection | `LeakThreshold`, `NonPagedPoolThreshold`, `StormThreshold`, `TrendWindow`, `EnablePredictiveAlerts` |
| Watchdog timing | `WatchdogIntervalSeconds`, `CooldownSeconds`, `ClockDriftThresholdSeconds` |
| Quarantine | `EnableQuarantine`, `QuarantineRestartLimit`, `QuarantineWindowMinutes` |
| Auto-kill of runaway processes | `EnableAutoKill`, `AutoKillProcesses`, `AutoKillMemThresholdMB` |
| Webhooks | `EnableWebhooks`, `WebhookUrl`, `WebhookMinHealthScore` |
| Exports and logging | `EnableJsonExport`, `EnableCsvExport`, `TimestampFormat`, `UseUtc` |
| Health score | `Weights` (`ServiceLeak`, `NonPagedPressure`, `Storm`, `TcpLoad`, `UdpLoad`) |
| Monitored services | `MonitoredServices[]` with `ServiceName`, `ProcessTree`, `MaxTcpConnections` (default `1000`), `EnableRestart` |

GUI popups were removed in v2.6.1: alerts go to the PowerShell console and to the log files, so the toolkit can run unattended. The obsolete `EnablePopups` key was removed from `config.json` in v2.7.4; a copy of the file that still contains it keeps working, the key is ignored.

**Quarantine:** after `QuarantineRestartLimit` successful restarts within `QuarantineWindowMinutes`, further restarts are blocked until the oldest one leaves that window. Failed restart attempts do not count.

**Auto-kill:** every process whose name is in `AutoKillProcesses` (`Name` or `Name.exe`, exact names only, no wildcards) is checked on every scan. Each running instance whose working set exceeds `AutoKillMemThresholdMB` is terminated and the result is reported (`AUTO-KILL OK` / `AUTO-KILL FAILED`). Protected system names and the watchdog's own process are never touched. Without administrator rights it can only end processes your own user owns, and a process it cannot end is reported once. It does not run in monitor-only mode.

**Exports:** `EnableJsonExport` and `EnableCsvExport` only control whether the files are written; the watchdog works the same with both switched off.

The shipped default monitors `LightingService` with the process tree `LightingService` + `AuraService`.

---

## 📊 Output

Everything is written under `logs/` (created at runtime, not tracked by git):

* `logs/NetworkDiag_<timestamp>.txt`: one log per scan. Watchdog actions (leak, restart, `RESTART FAILED`, quarantine, auto-kill) are appended to the log of the scan during which they happened. If no scan log is available the actions go to `logs/watchdog_fallback.log`.
* `logs/application.log`: configuration and webhook problems.
* `logs/heartbeat.json`: written periodically while the watchdog runs.
* `logs/export/`: `diag_*.json`, `diagnostics_v2.csv`, `HealthTrend_v2.csv` and, for every successful restart, a row in `RestartEvents_v2.csv`. The `_v2` suffix was introduced in v2.7.1 to avoid file-lock and schema conflicts with files left by older versions.

Logs and exports are **not rotated or deleted automatically**. At the default 60-second interval a running watchdog adds about 1,440 scan logs (and as many `diag_*.json` files) per day, so clean the folder from time to time or schedule that.

---

## ⚠️ Known Limitations

* **Non-paged pool pressure is always 0 %.** The metric reads `NonPagedPoolSize` and `NonPagedPoolQuota` from `Win32_OperatingSystem`, which do not provide those properties, so `NonPagedPoolThreshold` and the `NonPagedPressure` weight currently have no effect, and each scan logs a "NonPagedPoolQuota invalid" warning. Leak detection by connection count is not affected.
* Logs and exports are not rotated (see Output).

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
    ├── .gitattributes
    ├── .gitignore
    ├── CHANGELOG.md
    ├── LICENSE
    ├── README.md
    └── Start-LightingWatchdog.bat
```

---

## 🧩 Feature Milestones

* **v2.5.x**: Configurable thresholds, JSON/CSV exports, health scoring, leak growth tracking, webhook notifications, quarantine mode, and auto-kill for runaway processes.
* **v2.6.x**: Aggressive process tree termination (`taskkill`) and safe TCP `TIME_WAIT` kernel flush loops.
* **v2.7.x**: Dynamic multi-service monitoring arrays, fully headless execution, truthful restart handling, a rewritten auto-kill, monitor-only mode and a one-click launcher.

| Version | Date | Description |
| :--- | :--- | :--- |
| **v1.0.0** | 2026‑08‑17 | Baseline diagnostic script. |
| **v2.0.0** | 2026‑08‑18 | Modular architecture, config file, unified logging, service abstraction. |
| **v2.5.0** | 2026‑08‑19 | Heartbeat, clock drift detection, quarantine mode, auto-kill, health scoring, predictive alerts. |
| **v2.6.0** | 2026‑09‑03 | Process tree termination and a 15-second TCP flush wait before restart. |
| **v2.7.0** | 2026‑09‑06 | Universal watchdog: dynamic `MonitoredServices` array. |
| **v2.7.3** | 2026‑09‑06 | Default `MaxTcpConnections` back to 1000. |
| **v2.7.4** | 2026‑10‑31 | Honest restart reporting, rewritten auto-kill, watchdog independent of JSON export, monitor-only mode, `Start-LightingWatchdog.bat` launcher, unused config keys removed. |

*(For full patch details, view `CHANGELOG.md`. For the C# application, see [NetworkWatchdog](https://github.com/DiegoUA/NetworkWatchdog).)*

---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.
