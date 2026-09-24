# Vincel (深澈)

**A Windows cleanup utility focused on persistence and leftover removal — the category general-purpose uninstallers don't cover.**

[中文文档](README.zh-CN.md) · Official site: https://vincel.netlify.app/ · [Download](https://github.com/metacoves/vincel/releases)

![License](https://img.shields.io/badge/license-GPLv3-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey.svg)
![.NET](https://img.shields.io/badge/.NET-Framework%204.7.2-informational.svg)

## Download

- **GitHub Releases** (installer + portable zip): https://github.com/metacoves/vincel/releases
- Official site: https://vincel.netlify.app/ (installer / portable, WebView2 bootstrapper included)

## Why Vincel exists

General-purpose uninstallers (Geek Uninstaller, Bulk Crap Uninstaller, Revo, etc.) are built around the registry "installed programs" list. They remove applications well, but they do not systematically cover **persistence**:

- startup entries, services, scheduled tasks
- browser hijack points and shortcut hijacking
- context-menu remnants
- leftovers left behind after a partial or failed uninstall

In the Chinese Windows ecosystem a large share of unwanted software arrives as bundled or pre-installed components, and it persists through startup entries and services. Off-the-shelf uninstallers — and their signature databases — are not tuned for that ecosystem.

Vincel targets this gap: it scans persistence points and known bundled/pre-installed software families, cleans them with administrator privileges, and **backs everything up automatically so changes can be undone**.

## Interface

![Screenshot](screenshot.png)

## What it does (current, v1.0)

**Scanning**

- Installed programs (registry `Uninstall` entries), matched against a bundled signature list (231 entries)
- Startup entries: `HKCU/HKLM Run` / `RunOnce` + Startup folders
- Windows services and scheduled tasks
- Browser hijack points (IE / Chromium homepage & search settings)
- Browser shortcut hijacking
- Context-menu remnants
- AppData leftovers

**Cleaning**

- Removes the items above (force-delete fallback), running as administrator
- For **self-protected software families** that guard their files/registry with drivers (common among Chinese security suites), it launches the vendor's own uninstaller instead of deleting files directly, waits for the wizard to finish, then re-scans and removes leftovers automatically
- **Automatic backup before cleaning**: files, directories, registry values/keys, scheduled tasks — one-click restore of the last cleanup
- Ultra-large files are skipped during backup to bound disk usage

**Monitoring (notification only)**

- Installer monitor: detects when an installer starts (Windows process-start events) and notifies the user. It observes; it does not block.

## What it does NOT do

- It is **not** an antivirus and provides **no real-time protection**
- Installer monitoring is **notification-only** — it does not block or intercept installations
- The paid feature set ("Pro": installation interception, popup blocking, homepage protection, cloud signature updates) is **planned and not implemented**. The GPLv3 core is free and fully open source.

## Architecture

| Layer | Implementation |
|---|---|
| UI | `wwwroot/index.html` rendered in WebView2 (plain HTML/JS) |
| Shell | C# WinForms, .NET Framework 4.7.2, AnyCPU |
| Front-end ⇄ back-end | `JsBridge.cs` over `LocalWebServer.cs` (local HTTP) |
| Scanning / cleaning | `Scanner.cs`, `Cleaner.cs`, `Signatures.cs`, `BackupManager.cs` |
| Monitoring | `InstallWatcher.cs`, `PopupCounter.cs`, `FloatingBall.cs`, `AutoStartHelper.cs` |
| Telemetry / sample upload | Tencent CloudBase (cloud functions + document DB), anonymous device id stored locally |
| Updates | `UpdateService.cs` — checks `update.json`, verifies SHA-256, silent install via Inno Setup |

## Build

**Requirements**: Visual Studio 2022 **17.10+** (the solution uses the `.slnx` format), .NET Framework 4.7.2 targeting pack.

1. Clone the repository
2. Open `vincel.slnx`
3. Restore NuGet packages
4. Build (F5)

**Release build**: `build-release.ps1` / `发版.bat` — produces the Inno Setup installer and the portable zip.

Prerequisites:
1. Install **Inno Setup 6** (https://jrsoftware.org/isdl.php)
2. Download the **WebView2 Evergreen bootstrapper** from Microsoft, name it `MicrosoftEdgeWebview2Setup.exe`, and place it in `WebView2Runtime\` (not distributed with the repo; `.gitignore`d)

## System requirements

- **Windows 10 / 11** (recommended)
- .NET Framework 4.7.2
- **WebView2 Runtime** (bundled with Windows 11; the installer installs it when missing)
- Note: Microsoft has ended WebView2 Runtime support for Windows 7 / 8.1. The app may still run there, but it is not officially supported.

## Privacy

- Anonymous telemetry (version, feature usage counts) — **can be disabled in settings**, no personal data
- Device id stored locally: `%LOCALAPPDATA%\RogueCleaner\device_id.txt`
- Diagnostic log is **local only** (`%LOCALAPPDATA%\RogueCleaner\clean.log`) and is never uploaded
- Privacy statement: https://vincel.netlify.app/privacy.html

## License

GPLv3 — see [LICENSE](LICENSE).

A separate proprietary plugin ("Pro") is planned. The licensing approach for it is under review and is not final.
