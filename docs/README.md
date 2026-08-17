# LightingWatchdog

Advanced Windows network diagnostic and self-healing watchdog for ASUS LightingService leaks.

---

## 📂 Project Structure
```text
LightingWatchdog/
│
├── scripts/
│   └── NetworkDiag.ps1
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

LightingWatchdog is a Windows diagnostic tool designed to detect and analyze
network buffer exhaustion, TCP/UDP socket leaks, and specifically the known
LightingService leak affecting ASUS systems.

This repository contains:

- Baseline diagnostic script (v1.0)
- Future versions will include:
  - Leak detection
  - Auto-restart logic
  - WebSocket storm detection
  - Nonpaged pool monitoring
  - Full watchdog automation

---

## 🚀 Usage

Run the baseline diagnostic script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/NetworkDiag.ps1
```
---

## 🧾 License

This project is licensed under the **Creative Commons Attribution–NonCommercial 4.0 International License (CC BY‑NC 4.0)**.

You may use, modify, and share this project freely for non‑commercial purposes.  
Commercial use is strictly prohibited unless explicit permission is granted by the copyright holder.
