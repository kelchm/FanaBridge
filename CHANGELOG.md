# Changelog

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
