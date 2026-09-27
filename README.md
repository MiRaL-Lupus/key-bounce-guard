# Key Bounce Guard

Free, temporary Windows tray workaround for keyboard bounce / false double presses.

**Status:** v0.1.0 — independent community utility, not affiliated with any keyboard manufacturer.

## What it does

- Blocks a second **physical press of the same key** only when it starts less than 49 ms after the preceding accepted key release. The default threshold is **49 ms**; confirmed faulty events in the original investigation were approximately **38–39 ms** apart.
- Blocks the matching release too, so Windows never receives half of the false second press.
- Leaves ordinary key holds and Windows auto-repeat alone.
- Never filters modifier keys (`Ctrl`, `Shift`, `Alt`, or Windows keys), so common shortcuts stay untouched.
- Records only blocked suspected false repeats in `Logs\\blocked_key_events_YYYY-MM-DD.csv`; it does not log ordinary typing.
- Does not show a popup for each blocked event. The tray menu shows the count and can open the log folder.

## Run

Run `KeyBounceGuard-Setup.exe`, choose an install location, and optionally select automatic start when you sign in to Windows. The setup is a single executable and installs no driver.

The tray utility stays in the notification area. Right-click its shield icon to toggle protection, see the number blocked today, open the log folder, or exit.

Windows keeps the password screen on a protected desktop, so no ordinary user-mode utility can filter keyboard events before sign-in. The optional startup setting runs the utility immediately after sign-in.

The utility does not use the network, send telemetry, install a driver, change firmware, or modify keyboard settings. It only creates a per-user Windows startup entry if the installer option is selected.

## Scope

It uses the Windows low-level keyboard hook. It protects ordinary desktop input. Some games that read a keyboard through a direct/raw input path or anti-cheat layer may bypass it, so game behavior must be verified separately.

## For warranty/support reports

Attach the relevant `blocked_key_events_YYYY-MM-DD.csv` file. Each entry records the local timestamp, virtual key, scan code, and the measured interval. The original incident pattern that motivated this utility produced complete false key pairs approximately **38–39 ms** apart; the default guard threshold is **49 ms**.

## Build from source

On a 64-bit Windows computer with .NET SDK installed, run:

```powershell
.\Build-Release.ps1
```

It produces a self-contained `KeyBounceGuard-Setup.exe` and `SHA256SUMS.txt` under `artifacts\installer`.
