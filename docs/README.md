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
│   │   └── Utils.psm1
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

From v2.0, the project becomes modular, configurable, and ready for long‑term expansion.

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

---

## 🚀 Usage

Run the baseline diagnostic script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/NetworkDiag.ps1
```
Run continuous watchdog mode (v1.6):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/NetworkDiag.ps1 -Watchdog
```
Logs will appear in the logs/ folder.
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