# Changelog

## 0.1.0 — 2026-09-27

- First public release.
- Windows tray guard for same-key false repeats below 49 ms.
- Blocks the matching release as well, preventing half-events.
- Does not alter normal key holds or Windows auto-repeat.
- Explicitly excludes modifier keys so shortcut combinations remain untouched.
- Records only blocked suspected bounce events in a local CSV log.
- Single-file installer with optional start at user sign-in.

## Known limitations

- This is a temporary user-mode workaround, not a hardware repair.
- It cannot filter the protected Windows password screen before sign-in.
- Some games using Raw Input or anti-cheat may bypass the standard Windows input route; test game behavior separately.
