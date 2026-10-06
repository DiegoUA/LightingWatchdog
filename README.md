# NetworkWatchdogService

Advanced Windows network diagnostic and self-healing watchdog for socket leaks, runaway connection storms, and kernel memory pressure. (Formerly LightingWatchdog)

---

## ⚠️ Troubleshooting "Windows Protected Your PC"

Because this tool interacts with native system services and ETW telemetry, Microsoft Defender SmartScreen may flag the `.exe` as an "Unknown Publisher" upon your first download.

To permanently allow the installer to run:

1. Right-click `NetworkWatchdog_Installer.exe` and select **Properties**.
2. At the bottom of the **General** tab, check the box that says **Unblock**.
3. Click **Apply** and **OK**.
4. You can now run the installer normally.

---

## 📂 Project Structure

```text
NetworkWatchdogService/
    ├── .github/
    │   └── workflows/
    │       └── release.yml
    ├── config/
    │   └── config.json                 (legacy PowerShell toolkit only)
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
    │   ├── Test-NativeInterop.ps1
    │   └── Uninstall-Service.ps1
    ├── src/
    │   ├── NetworkWatchdogService/
    │   │   ├── Models/
    │   │   │   └── WatchdogConfig.cs
    │   │   ├── Properties/
    │   │   ├── appsettings.json
    │   │   ├── EvidenceCollector.cs
    │   │   ├── NetworkWatchdogService.csproj
    │   │   ├── Program.cs
    │   │   ├── SyslogNotifier.cs
    │   │   ├── TelemetryExporter.cs
    │   │   └── Worker.cs
    │   └── NetworkWatchdog.TrayApp/
    │       ├── app.ico
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

**Evidence for the vendor, in one click:** Before it stops a leaking application, the service captures a complete evidence case (socket tables, kernel counters, event logs, a memory dump and a README written for engineers) while the leak is still happening, and the dashboard saves it as a single `.zip` you can send to the product's R&D team. No other tools to download, install or configure.

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
* **v3.7.x**: Fully wired `appsettings.json`, native Windows Restart Manager interop, Quarantine/Cooldown lifecycles, Syslog forwarding, UI/updater stabilization and persistent Historical Mitigation logging.
* **v3.8.x**: R&D Diagnostic Evidence Suite (user-initiated and automatic evidence bundles with native memory minidumps).
* **v3.9.x**: Evidence is captured by the service *before* a leaking process is killed (one-click export from the dashboard), related-process discovery and restart (dependent services, file locks, `ProcessTree`), a working Historical Archive, threshold-relative health colours, consolidated syslog/telemetry export, restart-pipeline hardening and TrayApp security hardening.

---

## ⚡ Core Architecture: Zero-Overhead Monitoring

`NetworkWatchdogService` (v3.x+) utilizes a hybrid state-tracking architecture:

1. **Authoritative Count:** Every 15 seconds (`WatchdogIntervalSeconds`) the service reads the socket table from the same Windows network provider that PowerShell's `Get-NetTCPConnection` uses (CIM/NSI). Unlike the older IP Helper table, it sees sockets that were reserved with `bind()` but never connected, which is exactly how many leaks look.
2. **Zero-Overhead Live Tracking:** Between those checks, Event Tracing for Windows (ETW) streams raw socket allocations in real time through high-throughput memory channels, so the count stays current without slowing down your PC.
3. **Inter-Process Streaming:** Telemetry is broadcast through a zero-disk I/O asynchronous Named Pipe server directly to the user-space tray companion.
4. **Evidence, Then Mitigation:** When a process crosses its limit the service first captures evidence, then stops the process together with the services and programs that depend on it, waits for Windows to release the sockets and brings everything back through the Service Control Manager (never by launching files directly).

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
| **v3.6.1** | 2026-10-03 | **UI Decoupling:** Separated dashboard display thresholds from the system-wide mitigation engine to eliminate noise without blinding the 1000-socket global guardrail. |
| **v3.7.0** | 2026-10-06 | **Configuration Automation:** Fully wired `appsettings.json`, added native Windows Restart Manager interop for file-lock dependency termination, and introduced Quarantine/Cooldown lifecycles with Syslog forwarding. |
| **v3.7.1** | 2026-10-06 | **UI & Updater Stabilization:** Fixed auto-updater silent respawning context and resolved UI grid scrolling, WinForms thread-safety, and context menu refresh glitches. |
| **v3.7.2** | 2026-10-06 | **Updater SemVer & Log Persistence:** Fixed self-updater infinite loop via dynamic assembly version resolution and introduced persistent Historical Mitigation logging. |
| **v3.8.0** | 2026-10-06 | **R&D Diagnostic Evidence Suite:** Implemented one-click and automated evidence bundle generation (.zip) containing system port baselines, CIM socket dumps, process table telemetry, historical audit trails, event logs, and native memory minidumps via `DbgHelp.dll`. |
| **v3.9.0** | Unreleased | **Evidence Before Mitigation:** the service captures a complete R&D evidence case (sockets, counters, event logs, memory dump) *before* killing a leaking process; one-click export from the dashboard. Related-process discovery/restart, working Historical Archive, threshold-relative health colours, consolidated syslog/telemetry export, restart-pipeline and TrayApp hardening. |

*(For full patch details, view `CHANGELOG.md`)*

---

## Key Features

* **Universal Monitoring:** Tracks socket leaks across all applications on the PC.
* **Zero-Overhead:** Kernel ETW tracking uses virtually 0% CPU.
* **IPC Dashboard:** Real-time system tray visualization and control panel.
* **Malware Resistant:** LPE, Binary Hijacking, & PID reuse defenses built-in.
* **Auto-Update Engine:** Checks GitHub for updates and installs them silently.
* **Headless Automation:** Runs invisibly as a Windows Service.
* **One-Click R&D Evidence:** Evidence is captured before the leaking process is killed and exported as a single `.zip` with a README for engineers.
* **Smart Restarts:** Dependent services and blocking processes are restarted together with the leaking one.

---

## 🚀 Usage

Download and run the provided `NetworkWatchdog_Installer.exe`. The installer will automatically register the background service and launch the Tray Dashboard.

> **Note:** The NetworkWatchdog Dashboard (Tray App) requires Administrator privileges to adjust threshold settings, modify the process whitelist, or capture/export evidence. Standard users may still launch the Tray App to view live socket telemetry and historical mitigation logs.

### Collecting evidence for a vendor (1 click)

1. Open the dashboard (double-click the tray icon). Select the leaking process on **Live Processes** (or leave nothing selected to use the process closest to its limit).
2. Press **Collect Evidence (1 click)**, confirm the privacy notice (the file contains a memory dump) and choose where to save the `.zip`. If the dashboard is not elevated it offers to restart itself as Administrator.
3. Send the single `.zip` to the vendor's R&D team. `README_FOR_RD.txt` inside explains every file.

The same evidence is created **automatically** right before a leaking process is restarted (switch: *Settings & Whitelist* > *Capture R&D evidence automatically*). Stored cases appear on the **Evidence Cases** tab and can be exported any time.

---

## 📊 Exports and Data Locations

The service (v3.x) keeps its data in `%ProgramData%\NetworkWatchdogService\`, a folder restricted to Administrators and SYSTEM:

* `Evidence\Case_*.zip` (+ `.json` metadata): R&D evidence cases (newest `Evidence.MaxCases` are kept).
* `MitigationHistory.csv`: every automatic restart (served to the dashboard's Historical Archive over IPC).
* `HealthTrend_v2.csv` / `RestartEvents_v2.csv`: connection-count snapshots and restarts (growth timeline source).
* `whitelist.json` and `runtime-overrides.json`: whitelist and settings saved from the dashboard.

Dashboard diagnostics are written per user to `%LocalAppData%\NetworkWatchdog\tray.log`.

The legacy PowerShell toolkit (see below) writes its own files under `logs/` (`logs/export/diag_*.json`, `diagnostics.csv`, `HealthTrend_v2.csv`, `RestartEvents_v2.csv`, `logs/heartbeat.json`).

---

## ⚙️ Configuration

`NetworkWatchdogService` utilizes `appsettings.json` for core service intervals and settings:

```json
src/NetworkWatchdogService/appsettings.json
```

It natively obeys all defined `WatchdogConfig` variables, including per-service thresholds (`MaxTcpConnections`), cooldown windows, Syslog forwarding, and global monitoring toggles (`MonitorAllProcesses`).

Thresholds, the whitelist and the evidence switch can also be adjusted in real time on the **Settings & Whitelist** tab of the Tray Dashboard. Clicking "Save Configuration" saves the whitelist to `%ProgramData%\NetworkWatchdogService\whitelist.json` and the threshold and evidence switch to `runtime-overrides.json` (written atomically), which are applied on top of `appsettings.json` at the next start.

The `Evidence` section controls automatic capture: `Enabled`, `CaptureBeforeMitigation`, `DumpMode` (`Full`, `Standard`, `None`), `MaxCases`, `MinIntervalMinutes` and `CaptureTimeoutSeconds`. A `MonitoredServices[].ProcessTree` lists programs that are restarted together with the leaking process.

---

## 🧰 Legacy PowerShell Toolkit (v2.x)

The `scripts/` folder also contains the original PowerShell watchdog and diagnostics toolkit (`NetworkDiag.ps1`, `scripts/Modules/*.psm1`) with its own `config/config.json`. It is independent of the Windows service, uses a different configuration schema, and is useful for ad-hoc diagnosis or where the service is not installed. **Do not run its watchdog loop at the same time as the service against the same process**: two mitigators would compete. `Install-Service.ps1` / `Uninstall-Service.ps1` build and register the C# service for development; `Setup-GitHooks.ps1` installs a pre-commit build check; `Test-NativeInterop.ps1` verifies the native calls used by v3.9.0 on a real machine.

---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.
