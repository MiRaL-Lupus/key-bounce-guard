# Changelog

## 1.1 — 2026-09-29

- Added an on-screen blocked-event notification, enabled by default and controllable from the tray menu.
- Added a readable local HTML log with bold key names and labelled columns.
- Extended CSV entries with separate key name, virtual-key code, scan code, and interval fields.
- Improved tray layout with a separated blocked-event counter.
- Added Updates and repository information in the tray menu.

## 0.1.0 — 2026-09-27

- First public release.
- Windows tray guard for same-key false repeats below 49 ms.
- Blocks the matching release as well, preventing half-events.
- Does not alter normal key holds or Windows auto-repeat.
- Matches false repeats only to the same physical key identity; shortcut combinations remain independent.
- Records only blocked suspected bounce events in a local CSV log.
- Single-file installer with optional start at user sign-in.

## Known limitations

- This is a temporary user-mode workaround, not a hardware repair.
- It cannot filter the protected Windows password screen before sign-in.
- Some games using Raw Input or anti-cheat may bypass the standard Windows input route; test game behavior separately.
