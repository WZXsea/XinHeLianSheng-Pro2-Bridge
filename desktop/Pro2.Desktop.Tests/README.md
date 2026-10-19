# Shared desktop UI regression suite

Run from the repository root with the .NET 8 SDK:

```sh
dotnet run --project desktop/Pro2.Desktop.Tests/Pro2.Desktop.Tests.csproj
```

The executable uses Avalonia.Headless 11.3.22 with Skia drawing and runs the
configuration checks on the Headless UI thread. Both platforms compile the same
production UI and validation code. A failed check exits with an exception and a
nonzero exit code, so the same command can be used in CI.

`ConfigurationValidation.cs` retains the original 18 configuration acceptance
cases and covers additional timing/step-count boundaries, invalid targets, the
16-step editor limit, active connection operations, and letter/position layouts
for Pro2, Pro1, Xbox and PS5. Tests use editor commands for draft changes and
reflect only the private validation boundary to inspect its built result. A
rejected save verifies the draft, committed profile references, face layout and
specific error message. Valid configurations are built in memory.

Every model uses `initialize:false`. The suite records the settings file's
existence, length, modification time and SHA-256 and verifies that these remain
unchanged; the native host remains inactive. The Save command is exercised only
after confirming that its draft is invalid. Settings values are kept in memory.

The suite renders the four pages, collapsed key editor and expanded editor to
`artifacts/ui/`, checks required visible labels and root binding properties, and
verifies the embedded regular/bold font selection. `report.json` includes named
configuration checks and the binding/font results. These PNGs are source renders;
hardware and native platform behavior have separate acceptance checks.
