# Floating panel and Usage window smoke checks

Run on Windows with the .NET 10 SDK:

```powershell
dotnet run --project tests/costats.App.SmokeTests/costats.App.SmokeTests.csproj -c Release
```

This standalone WPF check creates off-screen windows with sample provider rows
and a Usage window over fake analytics services. It does not start the tray app,
load user settings, read agent logs, or contact providers. It is
kept separate from the core test project because it requires a Windows desktop.

Checks cover the two-column default without truncated status text, narrow/wide
reflow, size stability across refreshes, all eight native resize targets, the
close button winning over the right resize strip, automatic re-sizing when the
number of accounts changes, the user-chosen size surviving account changes and
being persisted exactly once, manual positioning, saved dimensions, invalid
dimensions, empty-state recovery, and the absence of a trailing text gutter.
Usage window checks cover loads that finish out of order (an older failure or
success never replaces a newer view), a comparison that stays visible when the
current period is empty, opening a day while another load is still running,
comparisons of different lengths, the two-period comparison popup driven
through its real day buttons, the change direction from the earlier period to
the later one, and the chart's label placement. They also render the window and
both popups in the light and dark themes and at the minimum width.

PNGs are saved beside the smoke-check executable for visual inspection. Set
`SMOKE_OUTPUT_DIR` to write them to another folder instead.
Dragging over text, clicking X, and scrolling overflow should also be checked
interactively before release.

The project is part of `costats.sln`, so the solution build keeps it compiling;
`dotnet test` does not execute it because it is a plain executable, which is why
it must be run with `dotnet run` as above.
