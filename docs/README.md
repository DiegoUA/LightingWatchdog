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
│   └── .gitkeep
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

From v2.x, it supports configuration, modular diagnostics, watchdog mode, structured log export, Service Health Scoring, trend/anomaly analysis, and from v2.4:

- Restart reason tracking

- Leak growth rate measurement

- Kernel memory pressure trend

- Cooldown logic to avoid restart storms

- Optional webhook notifications (Telegram/Discord via HTTP POST)

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

## Key v2.5 Features

- ISO‑8601 timestamps (`yyyy-MM-ddTHH:mm:ssZ`)
- Optional UTC mode
- Watchdog heartbeat (`logs/heartbeat.json`)
- Clock drift detection
- Quarantine mode for LightingService
- Auto‑kill for runaway processes
- Watchdog health score
- Existing leak, trend, health scoring, restart logic, webhooks, JSON/CSV exports.

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

JSON diagnostics: logs/export/diag_*.json

Diagnostics CSV: logs/export/diagnostics.csv

Health trend CSV: logs/export/HealthTrend.csv

Restart events CSV: logs/export/RestartEvents.csv

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