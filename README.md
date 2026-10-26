# DamnWindows11

A tray app that keeps window positions when a monitor disappears and comes back, for example after a KVM switch.

## Why
The Dell P2419H is connected through an HDMI→VGA adapter and a KVM. When the KVM switches away, the adapter shows Windows its own fake monitor (`AGO0001`) instead of the Dell (`DELD0D8`). When it switches back, Windows sees a new monitor setup and moves every window to the laptop screen.

## How it works
- Each monitor setup gets a **topology key**: the hardware ID and bounds of every connected monitor, e.g. `AUO45A8@0,0,2560x1600 | DELD0D8@2560,0,1920x1080`.
- Every 3 s, and after each manual move or resize, the app saves a snapshot of all top-level windows (position, size, maximized/minimized) under the current key. It only does this once the setup has been stable for 10 s.
- When the displays change, snapshots stop. Once the setup has been stable for 2 s, the app restores that setup's snapshot if it has one. It does this in three passes (0 s, 1.5 s, 4 s), because apps rescale after DPI changes and Windows' own restore can fight it.
- It is per-monitor-DPI-aware v2, so coordinates are correct across the 2560x1600 panel and the 1080p Dell.

## Build
```
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
```
Run `publish\DamnWindows11.exe`. Tray menu: **Restore now** (or double-click the icon), **Snapshot now**, **Pause**, **Start with Windows**, **Open log**, **Exit**.

Log file: `%LOCALAPPDATA%\DamnWindows11\log.txt`

## Start with Windows
Tick **Start with Windows** in the tray menu. This adds `DamnWindows11` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pointing at the exe that is currently running. Exit the app from the tray before re-publishing, because the running exe is locked. If you move the exe, tick the option again so the path is updated.

## Limitations
- Snapshots are kept in memory only. After a reboot, the app needs one stable period (about 10 s) to learn the layout.
- Windows of apps running **as administrator** can't be moved unless DamnWindows11 also runs elevated. They show up as `failed` in the log.
