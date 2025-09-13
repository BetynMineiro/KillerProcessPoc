# KillerProcessPoC

A cross-platform proof-of-concept for spawning and terminating large process trees on **macOS**, **Linux**, and **Windows**.  
The project demonstrates how to start a tree of child processes, monitor them, and ensure all descendants are terminated correctly after a timeout.

## 📋 Features
- **Cross-platform runners**:
  - `UnixProcessRunner` for macOS/Linux (uses `setsid` or Python shim).
  - `WindowsProcessRunner` using Windows Jobs (`KillOnClose`) with `taskkill` fallback.
- Spawns trees of processes (`TreeProcessApp`) to simulate children, grandchildren, and deeper descendants.
- Automatically verifies that no processes remain after termination.
- Detailed logs and JSON metrics (opened/closed processes, timings, and verification status).

## 🛠 Prerequisites
- [.NET SDK 9.0](https://dotnet.microsoft.com/) (or compatible version).
- **macOS/Linux**: `setsid` or `python3` available in your PATH.  
- **Windows**: PowerShell available (`Get-CimInstance`) for descendant discovery.

## ▶️ How to Build and Run
1. **Clone the repository**:
   ```bash
   git clone https://github.com/your-username/KillerProcessPoC.git
   cd KillerProcessPoC
   ```
2. **Build all projects**:
   ```bash
   dotnet build
   ```
3. **Run the runner**:
   ```bash
   cd Runner/bin/Debug/net9.0
   dotnet Runner.dll
   ```
   Optional environment variables:
   - `DEPTH` – Levels of the process tree (default `3`).
   - `BREADTH` – Number of children per process (default `5`).
   - `TIMEOUTMs` – Timeout in ms before killing the tree (default `5000`).
   - `TAG` – Custom tag to mark spawned processes.
   - `TREE_DLL` – Override path to `TreeProcessApp.dll`.

   Example:
   ```bash
   DEPTH=4 BREADTH=3 TIMEOUTMs=8000 dotnet Runner.dll
   ```

## 📊 Example Output
```
2025-09-12 22:26:46.612 info: Infra.UnixProcessRunner[0]
      Started Unix process PID=8655 setsid=True Path=dotnet Args=TreeProcessApp.dll --depth 5 --breadth 6 --sleepMs 300000 --tag TEST_daeeab85
=== METRICS ===
{
  "started_at": "2025-09-12T22:26:50.377Z",
  "os": "Darwin 24.6.0",
  "opened_total": 156,
  "killed_tree_confirmed": true
}
info: Runner[0]
      Verification passed: no leftover processes for tag TEST_daeeab85
```

## 🧪 Testing
The project includes unit tests using **xUnit** for:
- `UnixProcessRunner`
- `WindowsProcessRunner`
- `ProcessRunnerFactory`

Run tests:
```bash
dotnet test
```

## 📄 License
MIT License – feel free to use and adapt.
