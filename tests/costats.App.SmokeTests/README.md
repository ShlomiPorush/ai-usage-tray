# Floating panel smoke checks

Run on Windows with the .NET 10 SDK:

```powershell
dotnet run --project tests/costats.App.SmokeTests/costats.App.SmokeTests.csproj -c Release
```

This standalone WPF check creates off-screen windows with sample provider rows.
It does not start the tray app, load user settings, or contact providers. It is
kept separate from the core test project because it requires a Windows desktop.

Checks cover the two-column default without truncated status text, narrow/wide
reflow, size stability across refreshes, all eight native resize targets, manual
positioning, saved dimensions, invalid dimensions, empty-state recovery, and the
absence of a trailing text gutter. PNGs are saved beside the smoke-check executable
for visual inspection. Dragging over text, clicking X, and scrolling overflow
should also be checked interactively before release.
