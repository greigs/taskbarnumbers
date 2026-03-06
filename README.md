# Taskbar Number Overlay

Small Windows app that draws permanent `1..0` badges on top of taskbar app buttons so you always have a visual reference for `Win + number` shortcuts.

## Project Website

This repo includes a static website at `index.html`.

Open it directly in a browser, or serve it locally:

```powershell
python -m http.server 8080
```

Then visit `http://localhost:8080`.

## Run

```powershell
dotnet run --project .\TaskbarNumberOverlay\TaskbarNumberOverlay.csproj
```

## Build EXE

```powershell
dotnet publish .\TaskbarNumberOverlay\TaskbarNumberOverlay.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Published file:

`.\TaskbarNumberOverlay\bin\Release\net8.0-windows\win-x64\publish\TaskbarNumberOverlay.exe`

## Start automatically at login

1. Press `Win + R`, run `shell:startup`.
2. Create a shortcut in that folder that points to `TaskbarNumberOverlay.exe`.
3. Sign out and back in (or run the EXE once now).

## Notes

- Labels track taskbar icon movement and only show the first 10 positions (`1..9`, `0`).
- Works with primary and secondary taskbars.
- If Explorer restarts, the overlay refreshes automatically on the next polling cycle.

## Settings

The app creates `settings.json` in the same folder as the running EXE on first launch.

If you run via `dotnet run`, edit:

`.\TaskbarNumberOverlay\bin\Release\net8.0-windows\settings.json` (or `Debug` if you run debug build).

Common settings:

- `RefreshIntervalMs`: update speed (100 to 5000)
- `BadgeWidth`, `BadgeHeight`: badge size
- `VerticalOffsetPx`: vertical badge offset from top of icon
- `CornerRadius`, `FontSize`: badge style
- `BadgeColorRgba`, `TextColorRgba`: `R,G,B,A` (0 to 255)
- `ShowDiagnosticWhenNoButtons`: show/hide diagnostic banner
- `DiagnosticBadgeColorRgba`, `DiagnosticTextColorRgba`: diagnostic banner colors

Restart the app after editing settings.
