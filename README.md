# NetworkWatchdogService

Advanced Windows network diagnostic and self-healing watchdog for socket leaks, runaway connection storms, and kernel memory pressure. (Formerly LightingWatchdog).

---

## 📂 Project Structure

```text
NetworkWatchdogService/
    ├── .github/
    │   └── workflows/
    │       └── release.yml
    ├── installer/
    │   └── NetworkWatchdogInstaller.iss
    ├── logs/
    │   ├── export/
    │   ├── .gitkeep
    │   └── heartbeat.json
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
    ├── src/
    │   ├── NetworkWatchdogService/
    │   │   ├── Models/
    │   │   │   └── WatchdogConfig.cs
    │   │   ├── Properties/
    │   │   ├── appsettings.json
    │   │   ├── NetworkWatchdogService.csproj
    │   │   ├── Program.cs
    │   │   ├── SyslogNotifier.cs
    │   │   ├── TelemetryExporter.cs
    │   │   └── Worker.cs
    │   └── NetworkWatchdog.TrayApp/
    │       ├── NetworkWatchdog.TrayApp.csproj
    │       └── Program.cs
    ├── .gitignore
    ├── CHANGELOG.md
    ├── LICENSE
    └── README.md
```

## ⚙️️ Description

**NetworkWatchdogService** is a universal Windows diagnostic and watchdog system designed to detect and mitigate TCP/UDP socket leaks, runaway connection storms, and memory exhaustion across any running system process.

While originally built to tame `LightingService.exe`, it has evolved into a system-wide safety net. If any application on your PC opens too many network connections and forgets to close them, this service will automatically detect the anomaly, terminate the misbehaving app, clear the locked connections, and safely restart the application without interrupting your workflow.

---

### Feature Milestones

* **v2.5.x**: Configurable thresholds, JSON/CSV exports, health scoring, leak growth tracking, webhook notifications, quarantine mode, and auto-kill for runaway processes.
* **v2.6.x**: Aggressive process tree termination (`taskkill`) and safe TCP `TIME_WAIT` kernel flush loops.
* **v2.7.x**: Dynamic multi-service monitoring arrays and fully headless execution.
* **v3.0.x**: Native C# .NET 8 Worker Service using zero-overhead Event Tracing for Windows (ETW) for kernel-level socket tracking.
* **v3.1.x**: `NetworkWatchdog.TrayApp` graphical companion for real-time system tray monitoring (color-coded health icons and interactive tooltips).
* **v3.2.x**: Universal system-wide process monitoring (`MonitorAllProcesses`) and tray balloon alerts with a toggleable Silent Mode.
* **v3.3.x**: Interactive Live Process Inspector Dashboard Form, context-menu manual process termination, and historical mitigation log viewer.
* **v3.4.x**: Inter-Process Communication (IPC) via Named Pipes, runtime settings slider with whitelisting, UDP Syslog alert forwarding, GDI Handle hygiene guards, global unhandled exception capture, and CLI anti-tampering limits.
* **v3.5.x**: Production Readiness — Asynchronous ETW event channels, native OS TCP synchronization, Local Privilege Escalation (LPE) boundary guards, PID reuse collision prevention, `BuiltinAdministratorsSid` IPC lockdown, IPC pipe segregation, and atomic secure-ACL JSON persistence.
* **v3.6.x**: Major Lifecycle Update — Implemented Inno Setup installer for unified deployment, and embedded the `GitHubAutoUpdater` module in the Tray App for seamless OTA updates.

---

## ⚡ Core Architecture: Zero-Overhead Monitoring

`NetworkWatchdogService` (v3.x+) utilizes a hybrid state-tracking architecture:

1. **Initial Baseline:** Upon discovering an active process, the service executes a native snapshot using the IP Helper API to capture pre-existing socket leaks.
2. **Zero-Overhead Tracking:** Ongoing monitoring integrates directly into the Windows Kernel via Event Tracing for Windows (ETW). The service listens asynchronously to raw socket allocations in real-time, adding them to the baseline via high-throughput memory channels without slowing down your PC.
3. **Inter-Process Streaming:** Telemetry is broadcast through a zero-disk I/O asynchronous Named Pipe server directly to the user-space tray companion.

---

## 🧩 Version History

| Version | Date | Description |
| :--- | :--- | :--- |
| **v1.0.0** | 2026‑08‑17 | Baseline diagnostic script. |
| **v2.0.0** | 2026‑08‑18 | Modular architecture, config file, unified logging, service abstraction. |
| **v3.0.0** | 2026‑09‑19 | **Architectural shift:** Scaffolded C# .NET 8 Worker Service for native Windows Service integration. |
| **v3.4.0** | 2026‑09‑24 | Added Named Pipe IPC streaming, runtime threshold slider with process whitelisting. |
| **v3.5.0** | 2026‑09‑28 | Base production milestone introducing asynchronous ETW channels, native OS TCP synchronization. |
| **v3.6.0** | 2026-10-02 | **Major Lifecycle & Stability Update:** Inno Setup installer, OTA auto-updater, UDP socket resolution (`ERR_NO_BUFFER_SPACE` fix), UI noise filtration, and persistent threshold saving. |

*(For full patch details, view `CHANGELOG.md`)*

---

## Key Features

* **Universal Monitoring:** Tracks socket leaks across all applications on the PC.
* **Zero-Overhead:** Kernel ETW tracking uses virtually 0% CPU.
* **IPC Dashboard:** Real-time system tray visualization and control panel.
* **Malware Resistant:** LPE, Binary Hijacking, & PID reuse defenses built-in.
* **Auto-Update Engine:** Checks GitHub for updates and installs them silently.
* **Headless Automation:** Runs invisibly as a Windows Service.

---

## 🚀 Usage

Download and run the provided `NetworkWatchdog_Installer.exe`. The installer will automatically register the background service and launch the Tray Dashboard.

> **Note:** The NetworkWatchdog Dashboard (Tray App) requires Administrator privileges only if you intend to adjust threshold settings or modify the active process whitelist. Standard users may still launch the Tray App to view live socket telemetry and historical mitigation logs.
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

`NetworkWatchdogService` utilizes `appsettings.json` for core service intervals and settings:

```json
src/NetworkWatchdogService/appsettings.json
```

Thresholds and process whitelists can be adjusted dynamically in real-time using the Settings & Whitelist tab inside the desktop Tray Dashboard.

---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.
