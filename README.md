# GoatMeter

GoatMeter is a passive AION 2 DPS meter for Windows, verified against regression tests, reviewed captures and observed dungeon runs. Independent party-total comparison and broader protocol coverage remain outstanding.

## Build
GitHub Actions publishes a self-contained Windows x64 build. Run the **Build Windows EXE** workflow, then download the `GoatMeter-win-x64` artifact.

Extract the entire build ZIP and run `GoatMeter.exe`. Existing settings and history remain in the original Aion2DPSPro data folder.

## Live capture
Requires Npcap on the test PC. The application observes TCP/13328 passively and does not inject into or modify the game process.

See [accuracy and reliability status](docs/accuracy-reliability.md) for implemented features, timing rules, validation commands and remaining live-protocol work.


## Overlay designs
Open the overlay gear menu and choose **Details Inspired** for full-width class-colored ranked bars, or **Kagerou Inspired** for translucent compact rows with thin contribution bars. These are independent row designs; color themes can be used with either style. Classic and the existing layout presets remain available.

The compact category selector supports all eight categories, and the encounter selector supports current, previous, and overall. Double-click a player in either design to open the detailed report. Drag the header to move the overlay. Style, theme, size, position, opacity, detail visibility, and always-on-top preferences are saved on closing settings, hiding the overlay, or exiting.

Validation logs now include `resolvedCombat` records showing the exact actor name delivered from capture to aggregation, alongside `captureIdentity` records. This helps distinguish missing live identity data from display or routing issues.
