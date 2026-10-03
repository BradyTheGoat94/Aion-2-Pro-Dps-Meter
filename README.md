# Aion2DPSPro

Experimental passive AION 2 DPS meter for Windows. The live decoder is under validation; the current opcode profile is intentionally marked unverified.

## Build
GitHub Actions publishes a self-contained Windows x64 build. Run the **Build Windows EXE** workflow, then download the `Aion2DPSPro-win-x64` artifact.

## Live capture
Requires Npcap on the test PC. The application observes TCP/13328 passively and does not inject into or modify the game process.

See `docs/FIRST-LIVE-TEST.md` before testing.
