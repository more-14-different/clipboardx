# Alt-V Paste Heuristics

This note documents the current `Alt+V` text paste dispatch rules so future changes do not need to be rediscovered from runtime logs.

## Scope

This document only covers the `Alt+V` text paste session:

- popup stays `no-activate`
- text is prepared through the clipboard provider session
- a standard paste shortcut is dispatched to the original target window
- clipboard provider is held briefly, then released

It does not describe image/file paste behavior.

## Main Flow

Current text paste flow is:

1. `PopupWindow` creates an `AltVTextPasteSession`
2. `AltVTextPasteSession` prepares clipboard text
   - preferred path: `AltVClipboardProvider`
   - fallback path: local clipboard write
   - last-resort fallback: direct insert, then Unicode typing
3. `AltVTextPasteSession` asks `PasteTargetHeuristics` for the effective shortcut
4. one standard paste shortcut is sent
5. provider settle window completes, then the provider process is disposed

Relevant code:

- [AltVTextPasteSession.cs](E:\MCP\Projecto\clipboardx\Services\AltVTextPasteSession.cs)
- [AltVClipboardProvider.cs](E:\MCP\Projecto\clipboardx\Services\AltVClipboardProvider.cs)
- [PasteTargetHeuristics.cs](E:\MCP\Projecto\clipboardx\PasteTargetHeuristics.cs)

## Target Classification

`PasteTargetHeuristics` classifies targets through ordered `PasteTargetProfile` entries.

Each profile contains:

- target mode: `CtrlV` or `ShiftInsert`
- reason: short diagnostic tag written to the clipboard log
- process name set
- window class set

The matcher builds a `TargetSnapshot` from:

- process name
- window class
- window title

Then profiles are checked in order. The first matching profile wins.

If nothing matches, the configured user default is used.

## Current Profiles

### `terminal`

Dispatch mode: `ShiftInsert`

Why:

- terminal hosts are historically less consistent with `Ctrl+V`
- `Shift+Insert` matches native terminal expectations better

Current examples:

- process names: `cmd`, `powershell`, `pwsh`, `windowsterminal`, `conhost`, `wezterm-gui`
- window classes: `ConsoleWindowClass`, `CASCADIA_HOSTING_WINDOW_CLASS`

### `ctrlv-preferred-host`

Dispatch mode: `CtrlV`

Why:

- browsers, Chromium/Electron hosts, and Explorer are more naturally aligned with native `Ctrl+V` paste semantics

Current examples:

- process names: `chrome`, `msedge`, `firefox`, `explorer`, `cursor`, `slack`, `discord`, `notion`
- window classes: `Chrome_WidgetWin_1`, `MozillaWindowClass`, `CabinetWClass`, `ExploreWClass`

## Order Matters

Profile order is intentional.

- `terminal` is checked before `ctrlv-preferred-host`
- more specific or more restrictive profiles should stay earlier
- broader host buckets should stay later

If a new target family overlaps with an existing one, prefer solving it by adding a new profile above the broader rule rather than adding more special cases into the matching code.

## How To Extend

When adding a new rule:

1. identify the stable signal first
   - prefer window class or process name
   - use window title only as supporting context, not as the primary classifier
2. decide whether the new rule is:
   - more specific than an existing profile
   - broader than an existing profile
3. place the profile accordingly in `DispatchProfiles`
4. keep the `reason` string short and stable for log search
5. verify the new rule in `clipboard_diagnostics.log`

Recommended verification log lines:

- `paste text clipboardProvider ok=True`
- `paste success path=clipboardProvider shortcut=... reason=...`
- `paste providerSettle elapsedMs=...`

## What Not To Do

- do not reintroduce UI Automation text write as a normal text paste path
- do not move Unicode typing ahead of standard paste dispatch
- do not mix target classification with popup positioning logic
- do not put per-app logic directly into `PopupWindow` unless the rule cannot live in a profile
