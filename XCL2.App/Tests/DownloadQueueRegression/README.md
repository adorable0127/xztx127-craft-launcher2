# Download queue regression

Run from the repository root:

```powershell
dotnet run --project Tests\DownloadQueueRegression\DownloadQueueRegression.csproj -c Release
```

This links the production queue and Java metadata parser with minimal UI stubs. It checks awaited success/failure, cancellation cleanup reservations, duplicate version and dynamically resolved target rejection, safe installer controls, pause/resume serialization, and separate verified ZIP/MSI metadata.

Windows manual verification is still required for the title-bar UI, Windows Update COM integration, UAC cancellation, installer exit codes and the real Java sources.

Entry coverage:

- Launch integrity repair, including missing client/JSON/parent/libraries/assets: one queue item, strict recheck, no launch after failure/cancellation.
- Launch-time missing-library recovery: one queue item and awaited repair.
- Automatic launch Java, Settings Java, both first-run Java actions: queued and awaited. Existing Quick Start and Create Server queues remain single entries.
- Settings and crash-report driver actions: the same Windows Update queue item; search/download/install stages, no-match and reboot results, actual process failure. Confirmation and UAC remain required.
- External browser links to manufacturer pages are not tracked as downloads because XCL does not own those transfers.

Awaited prerequisites intentionally have no pause/background retry. Windows Update and Java system installers cannot be forcibly canceled through queue controls. Canceling a launch may stop Java download before MSI begins; if MSI is already running, XCL waits for it to exit and then does not launch the game.
