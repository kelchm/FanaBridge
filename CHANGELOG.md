# Changelog

## v0.5.0 - Unreleased

### Fixed
- **SRM Conversion Kit wheels are detected again.** A Fanatec wheel/hub run through an SRM Conversion Kit no longer sits at "Connecting…" — FanaBridge recovers the wheel's identity from the conversion kit when the base doesn't emit the usual Fanatec system report (regressed in v0.4.0). The genuine-hardware detection path is unchanged. ([#47](https://github.com/kelchm/FanaBridge/pull/47), fixes [#52](https://github.com/kelchm/FanaBridge/issues/52))
- **Changing the active game in SimHub no longer kills all LED and display output.** SimHub restarts its plugin manager in-process on every game change; FanaBridge now survives that restart with its hardware connection intact instead of being torn down and rebuilt while SimHub's device instances kept writing into the disposed connection — previously everything went dark (while still showing *Connected*) until SimHub itself was restarted ([#51](https://github.com/kelchm/FanaBridge/issues/51))
- Device instances now rebind to the live hardware core if the plugin is ever replaced in-process (e.g. disabled and re-enabled), instead of silently driving a dead transport
- **Displays no longer keep showing the last telemetry after a game exits.** SimHub keeps the final telemetry values around after a game ends, so the wheel used to hold the last gear/speed (7-segment) or telemetry frame (ITM) indefinitely. FanaBridge now detects the game-running state ending: the 7-segment display is blanked, and the ITM display's fields revert to their placeholders (e.g. `--- / -`) while the session, page, and subscriptions stay up — both retried until the wheel accepts the write. ITM stays active outside of games, so the default-page setting still previews live on the wheel ([#54](https://github.com/kelchm/FanaBridge/issues/54))
- The 7-segment display no longer marks a gear/speed as shown when the write never reached the wheel, so a briefly unavailable transport can't freeze the display on a stale value
- A device's LED module no longer stays permanently unbuilt if SimHub asked for it before FanaBridge finished initializing

## v0.4.0 - 2026-07-01

### Changed
- **Device detection is now handled entirely by FanaBridge over HID** — wheel, hub, and module identity is read straight from the wheelbase (the col03 `FF 08` system report), dropping the dependency on SimHub's Fanatec SDK integration (`SimHub.FanatecManaged.dll`).

### Added
- **Control Mapper Integration (experimental)**: SimHub's Control Mapper can keep separate button mappings per wheel, but only for wheels its built-in support recognizes. FanaBridge now supplies its own wheel identity to Control Mapper, extending that per-rim recognition to Fanatec wheels and bases SimHub can't identify on its own (Podium DD, newer wheels). On by default, but a no-op unless Control Mapper's own "Recognize Individual Wheels" setting is also on (the settings page flags it when it isn't); SimHub still wins for wheels it already recognizes, so existing mappings are untouched.
- **Device Status** now shows the connected hardware as a `wheelbase › wheel/hub › module` chain with friendly names and a live connection indicator; unrecognized hardware shows its raw identity byte.
- **Copy Debug Info** copies a read-only report (HID interfaces, decoded identity, the raw `FF 08` bytes, the DirectInput controllers Control Mapper sees, and a Control Mapper resolution snapshot) to the clipboard for bug reports — no need to close SimHub or run a separate tool.
- A disconnected device now shows *why* (no device found, interface in use, lost connection, …).

### Fixed
- Detection now covers hardware SimHub's Fanatec SDK couldn't identify — Podium DD wheelbases and newer wheels such as the ClubSport Formula V3 are now recognized, both for FanaBridge's own device matching and for the Control Mapper integration.

## v0.3.2 - 2026-06-22

### Fixed
- Button LEDs on the GT Steering Wheel Extreme (GTSWX) now work when "Individual LEDs profiles" is disabled. The button LED region was mapped with the wrong offset, so button lighting only worked in individual-LEDs mode ([#34](https://github.com/kelchm/FanaBridge/pull/34), fixes [#29](https://github.com/kelchm/FanaBridge/issues/29))

### Changed
- GT Steering Wheel Extreme (GTSWX) profile marked as verified

## v0.3.1 - 2026-06-18

### Fixed
- Speed display now follows the km/h vs mph unit set in SimHub instead of always showing km/h, by reading `SpeedLocal` rather than `SpeedKmh` ([#27](https://github.com/kelchm/FanaBridge/issues/27))

## v0.3.0 - 2026-03-28

### Added
- **Legacy LED support (col01)**: wheels with on/off rev LEDs, 3-bit color rev LEDs, RevStripe, and legacy flag LEDs are now controllable via SimHub
- Device logos for all newly profiled wheels
- Wheel type alias mapping to handle SDK naming divergence (e.g., BENTLEY → PSWBENT)
- Settings UI shows detected hardware capabilities for the connected wheel
- Restart prompt when a profile change requires it
- [Supported Devices](docs/supported-devices.md) documentation page

### New Wheel Support
- ClubSport Steering Wheel BMW M3 GT2
- ClubSport Steering Wheel BMW M3 GT2 V2
- ClubSport Steering Wheel F1 Esports V2
- ClubSport Steering Wheel Formula Carbon
- ClubSport Steering Wheel Formula V3
- ClubSport Steering Wheel Porsche 918 RSR
- ClubSport Steering Wheel RS
- CSL Elite Steering Wheel McLaren GT3 V1.0
- CSL Elite Steering Wheel McLaren GT3 V2
- CSL Elite Steering Wheel P1 for Xbox One
- CSL Elite Steering Wheel P1 for PlayStation 4
- CSL Elite Steering Wheel Porsche Vision GT
- CSL Elite Steering Wheel WRC
- CSL Steering Wheel GT3
- GT Steering Wheel PRO
- GT Steering Wheel Extreme
- Podium Steering Wheel Bentley GT3

### Changed
- LED channel naming reworked for clarity (`revRgb`, `flagRgb`, `buttonRgb`, `legacyRevOnOff`, `legacyRev3Bit`, `legacyRevStripe`)
- Profile schema updated to v2
- Documentation reorganized into `docs/reference/`

## v0.2.1 - 2026-03-18

### New Wheel Support
- ClubSport Steering Wheel Formula V2.5 (CSSWFORMV2)

## v0.2.0 - 2026-03-16

### Added
- **Wheel Profile Wizard**: 8-step dialog that probes hardware to generate custom profiles for unsupported wheels
- **JSON Wheel Profiles**: device capabilities are now defined in editable JSON files; user-created profiles are supported alongside built-ins
- **Encoder tuning** (experimental): read and set encoder mode (Encoder / Pulse / Constant / Auto) from plugin settings
- Profile picker in settings when multiple profiles match the connected wheel
- Settings UI actions: delete custom profile, "Open Profiles Folder", "Contribute to GitHub"

### Fixed
- Green color corruption on Button Module Rally hardware
- `GearAndSpeed` display mode: gear now shows as a 2-second overlay after each shift, then reverts to speed
- LED settings not persisting between sessions

### New Wheel Support
- Podium Hub + Button Module Endurance

## v0.1.0 - 2026-02-23 (beta)

### Added
- Initial public beta release
- Fanatec wheel detection via SDK (wheel type + button module identification)
- Button LED control: full RGB565 color and 8-level intensity per LED
- 7-segment display control: gear, speed, and custom text modes
- SimHub Devices integration: each wheel type appears as a separate device with LED profile support
- Automatic device reconnection on disconnect/hot-plug
- Settings UI with connection status, wheel info, and reconnect button
- Device logo support for recognized wheels

### Supported Wheels
- Podium Steering Wheel BMW M4 GT3
- Podium Hub + Button Module Rally
