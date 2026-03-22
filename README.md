# PC Guardian

A portable Windows security scanner that checks if anyone can remotely access your PC. Built in C# with WinForms — single `.exe`, no install required.

Designed so anyone can use it, even if they've never touched a terminal.

![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4)

---

## What It Does

PC Guardian runs **13 security checks** using native Windows APIs — no PowerShell scripts, no cloud, no telemetry. Everything stays on your machine.

| Check | What It Looks For |
|-------|-------------------|
| Screen Control | Is Remote Desktop (RDP) enabled? |
| Remote Access Apps | TeamViewer, AnyDesk, VNC, Parsec, RustDesk installed or running |
| Open Doors | Network-exposed ports (RDP, VNC, SSH, Telnet, SMB) |
| Active Connections | Who your PC is talking to right now |
| Shared Folders | SMB shares and active sessions |
| Remote Services | WinRM, SSH, Remote Registry status |
| Firewall | Profile status + inbound remote access rules |
| Who's Logged In | Local vs remote user sessions |
| Startup Programs | What runs when Windows boots |
| Scheduled Tasks | Suspicious or unfamiliar scheduled jobs |
| Antivirus Status | Is Windows Defender active and up to date? |
| DNS Settings | Check for DNS hijacking |
| USB Devices | Recently connected USB devices |

Each check returns a clear status: **All Good**, **Worth Checking**, or **Needs Attention** — with plain-English explanations and tips on what to do.

---

## Features

### Security Scanner
- 13 scan categories covering remote access, network, system services, and more
- One-click scan with real-time progress
- Color-coded results (green/yellow/red) with expandable details
- Risk score (0-100) that tracks over time

### One-Click Fixes
- Disable Remote Desktop
- Stop remote services (WinRM, SSH, Remote Registry)
- Kill suspicious processes
- Block/unblock specific ports via Windows Firewall

### Quarantine Mode
- One toggle to block ALL remote access ports and kill remote access apps
- Useful on public WiFi or when you suspect unauthorized access

### Process Activity Logger
- Background monitoring of every process start/stop
- SQLite database for structured, queryable history
- Categorization: Browser, Communication, Gaming, Development, Remote Access, System, etc.
- Activity Log viewer with timeline, active processes, and program history

### Real-Time Alerts
- Instant notification when a remote access app starts
- USB device insertion detection
- New listening port detection
- Tray notifications for background alerts

### Network Monitor
- Live view of all TCP connections with process names
- Reverse DNS resolution for remote IPs
- Color-coded by category (red for remote access, yellow for unknown)

### Periodic Scanning
- Configurable scan interval (5 min to 2 hours)
- Silent background scans — only alerts when things get worse
- Scan history stored in SQLite

### Start with Windows
- One-time admin elevation — creates a scheduled task to run at login
- No UAC prompt on subsequent boots
- Toggle on/off from settings

### Export & Share
- **HTML Report** — beautiful, self-contained report you can email to IT
- **PDF Export** — pure C# PDF generation, no browser dependency
- **IT Server** — built-in web server so your IT team can view results by opening a URL on your network

### Scan Comparison
- Diff any two scans side-by-side
- See what improved, what got worse, what's new

### Settings
- Dedicated settings page with sections: Scanning, Behavior, Process Monitor, Data, IT Sharing
- Tooltips on every control explaining what it does in plain English
- All preferences persisted to JSON

### Other
- Dark theme throughout
- Custom app icon
- Keyboard shortcuts (Ctrl+S scan, Ctrl+E export, Ctrl+L activity, Ctrl+N network, F5 refresh)
- First-run onboarding wizard
- System tray with minimize-to-tray
- Sound cues (configurable)
- Auto-update checker

---

## Getting Started

### Option A: Download the portable `.exe`

Go to [Releases](../../releases) and download `PCGuardian.exe`. Double-click to run. That's it.

### Option B: Build from source

**Requirements:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
git clone https://github.com/rps321321/pc-guardian.git
cd pc-guardian
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
```

The `.exe` will be at `bin/Release/net8.0-windows/win-x64/publish/PCGuardian.exe`.

Or just run `build.bat`.

---

## Architecture

```
PCGuardian.csproj          Project file (.NET 8, WinForms)
Program.cs                 Entry point, single-instance mutex
Models.cs                  Status, Finding, Category, Report records
Theme.cs                   Colors, fonts, dark theme constants
ScanEngine.cs              13 scan checks using native APIs (Registry, WMI, P/Invoke, ServiceController)
MainForm.cs                Main window — home dashboard, scan results, navigation
SettingsForm.cs            Dedicated settings page
ActivityForm.cs            Process history viewer (SQLite-backed)
NetworkForm.cs             Live TCP connection monitor
OnboardingForm.cs          First-run wizard
Database.cs                SQLite schema, queries, process/scan history
ProcessMonitor.cs          Background process start/stop tracking
RealTimeMonitor.cs         WMI event watchers (process, USB, port)
SettingsManager.cs         JSON settings persistence + Windows startup
AdminHelper.cs             Admin detection + elevation + scheduled task
FixActions.cs              One-click security fixes
QuarantineManager.cs       Block/unblock all remote access
RiskScore.cs               0-100 security score calculation
ScanComparer.cs            Diff two scan reports
ReportGenerator.cs         HTML report generation
PdfExporter.cs             Pure C# PDF generation
ITServer.cs                Built-in HTTP server for IT access
UpdateChecker.cs           Version check against GitHub releases
SoundManager.cs            Audio cues for scan events
DarkTooltip.cs             Custom dark-themed tooltips
app.manifest               UAC configuration
build.bat                  One-click build script
```

### Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8 (self-contained — no install needed) |
| UI | WinForms with custom dark theme |
| Database | SQLite via Microsoft.Data.Sqlite |
| System Access | Registry, WMI, P/Invoke (GetExtendedTcpTable), ServiceController |
| PDF | Pure C# — System.Drawing + custom layout engine |
| Networking | HttpListener (IT server), HttpClient (update checker) |

### Why These Choices

- **C# / .NET 8** — Full native Windows API access (Registry, WMI, Win32), memory-safe, compiles to a single portable `.exe`
- **WinForms** — Lightweight, no web runtime overhead, works on every Windows 10/11 machine
- **SQLite** — Embedded database, zero setup, queryable process history
- **No PowerShell dependency** — All checks use direct API calls for speed and reliability

---

## Permissions

PC Guardian works without admin, but some checks are limited:

| Feature | Standard User | Admin |
|---------|:---:|:---:|
| Remote Desktop check | Yes | Yes |
| Installed software scan | Yes | Yes |
| Open ports | Yes | Yes |
| Active connections | Yes | Yes |
| Shared folders | Yes | Yes |
| Service status | Yes | Yes |
| Firewall profile status | Yes | Yes |
| Firewall rules (detailed) | Limited | Yes |
| Process start events (real-time) | No | Yes |
| One-click fixes | No | Yes |
| Quarantine mode | No | Yes |
| Start with Windows | No | Yes |

Click **"Unlock Full Access"** on the home screen to elevate once. If you enable "Start with Windows", the app runs elevated automatically on every boot via a scheduled task — no repeat UAC prompts.

---

## Data & Privacy

- **100% local** — no data leaves your computer, ever
- **No telemetry** — no analytics, no tracking, no phone-home
- **No cloud** — no accounts, no sign-up, no internet required
- **SQLite database** stored at `%AppData%\PCGuardian\activity.db`
- **Settings** stored at `%AppData%\PCGuardian\settings.json`
- Clear all data anytime from Settings

---

## Contributing

PRs welcome. Please:

1. Keep it simple — this is a utility app, not a framework
2. Test on Windows 10 and 11
3. Don't add external UI libraries — we're sticking with WinForms
4. Every new feature needs a tooltip explaining what it does in plain English

---

## License

[MIT](LICENSE) — do whatever you want with it.
