# Taskbar Number Overlay

Small Windows app that draws permanent `1..0` badges on top of taskbar app buttons so you always have a visual reference for `Win + number` shortcuts.

**Website:** [taskbarnumbers.thereminhero.co.uk](https://taskbarnumbers.thereminhero.co.uk)

## Install

Download and run the installer from the [Releases](https://github.com/greigs/taskbarnumbers/releases) page.

- Installs to `Program Files\Taskbar Number Overlay`
- Starts the app immediately after installation
- Adds a startup entry so it runs automatically at login
- Includes .NET 8 Desktop Runtime if not already installed

To uninstall, use **Add or remove programs** in Windows Settings.

## Exit / Tray icon

The app runs silently in the background. To exit it, right-click the icon in the system tray and choose **Exit**.

## Run from source

```powershell
dotnet run --project .\TaskbarNumberOverlay\TaskbarNumberOverlay.csproj
```

## Build framework-dependent publish (used by the installer)

```powershell
dotnet publish .\TaskbarNumberOverlay\TaskbarNumberOverlay.csproj -c Release --self-contained false
```

Published files: `.\TaskbarNumberOverlay\bin\Release\net8.0-windows\publish\`

## Build self-contained single-file EXE

```powershell
dotnet publish .\TaskbarNumberOverlay\TaskbarNumberOverlay.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Published file: `.\TaskbarNumberOverlay\bin\Release\net8.0-windows\win-x64\publish\TaskbarNumberOverlay.exe`

## Build the installer

```powershell
dotnet build .\Installer\Installer.wixproj -c Release
```

MSI output: `.\Installer\bin\x64\Release\TaskbarNumberOverlay-Setup.msi`

## Start automatically at login (manual, without installer)

1. Press `Win + R`, run `shell:startup`.
2. Create a shortcut in that folder pointing to `TaskbarNumberOverlay.exe`.

## Notes

- Labels track taskbar icon movement and only show the first 10 positions (`1..9`, `0`).
- Works with primary and secondary taskbars.
- If Explorer restarts, the overlay refreshes automatically on the next polling cycle.

## Settings

The app creates `settings.json` in the same folder as the running EXE on first launch.

If running via `dotnet run`, edit:

`.\TaskbarNumberOverlay\bin\Release\net8.0-windows\settings.json`

| Setting | Description |
|---|---|
| `RefreshIntervalMs` | Update speed in ms (100–5000) |
| `BadgeWidth`, `BadgeHeight` | Badge size in pixels |
| `VerticalOffsetPx` | Vertical offset from the top of the icon |
| `CornerRadius` | Badge corner rounding |
| `FontSize` | Badge font size |
| `BadgeColorRgba` | Badge background colour as `R,G,B,A` (0–255) |
| `TextColorRgba` | Badge text colour as `R,G,B,A` (0–255) |
| `EmptyScanHoldCount` | How many empty scans to tolerate before hiding badges |
| `TransientRetryIntervalMs` | Retry interval (ms) during transient empty scans |

Restart the app after editing settings.
