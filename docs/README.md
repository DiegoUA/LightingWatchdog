# LightingWatchdog

Advanced Windows network diagnostic and self-healing watchdog for ASUS LightingService leaks.

---

## 📂 Project Structure
```text
LightingWatchdog/
│
├── scripts/
│   ├── NetworkDiag.ps1
│   ├── Modules/
│   │   ├── Diagnostics.psm1
│   │   ├── Watchdog.psm1
│   │   ├── Utils.psm1
│   │   └── Trends.psm1
│
├── config/
│   └── config.json
│
├── logs/
│   ├── export/
│   │   ├── diag_*.json
│   │   ├── diagnostics.csv
│   │   ├── HealthTrend.csv
│   │   └── RestartEvents.csv
│   ├── heartbeat.json
│   └── .gitkeep
│
├── .github/
│   └── workflows/
│       └── release.yml
│
├── docs/
│   └── README.md
│
├── .gitignore
└── LICENSE

```

## ⚙️ Description

LightingWatchdog is a modular Windows diagnostic and watchdog system designed to detect and mitigate:

- TCP/UDP socket leaks

- LightingService runaway connection storms

- Nonpaged pool exhaustion

- WebSocket storms

- Kernel memory pressure

From v2.x, the system supports:

- Modular diagnostics

- Configurable thresholds

- Continuous watchdog mode

- Structured JSON/CSV exports

- Service Health Scoring

- Trend and anomaly analysis

From v2.4+, the system adds:

- Restart reason tracking

- Leak growth rate measurement

- Kernel memory pressure trend

- Cooldown logic to avoid restart storms

- Optional webhook notifications (Telegram/Discord via HTTP POST)

From v2.5+, the system includes:

- ISO‑8601 timestamps

- Optional UTC mode

- Heartbeat file (logs/heartbeat.json)

- Clock drift detection

- Quarantine mode

- Auto‑kill for runaway processes

- Watchdog health scoring

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
| **v2.5.2** | 2026‑08‑19 | **Absolute path stability**, ``$PSScriptRoot`` module imports, correct config resolution, fully location‑independent execution, hardened module loading. |
| **v2.5.3** | 2026‑08‑19 | Unified ``Write-Log ``-File ``-Message``, full path‑safety rewrite, stable heartbeat/export paths, corrected webhook payloads, eliminated DriveNotFound errors. |
| **v2.5.4** | 2026‑08‑19 | Fixed watchdog cycle timing, stabilized module imports, corrected Write-Log path handling, improved continuous-mode reliability. |

## Key Features

- Leak detection and restart with cooldown
- Kernel nonpaged pool monitoring
- WebSocket storm detection
- Health scoring and trend analysis
- Leak growth rate
- Nonpaged trend
- ISO-like timestamps (filesystem-safe)
- Optional UTC mode
- Watchdog heartbeat (`logs/heartbeat.json`)
- Clock drift detection
- Quarantine mode for LightingService
- Auto-kill for runaway processes
- JSON + CSV exports
- Optional webhook notifications

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

Edit thresholds in:

```json
config/config.json
```

---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.