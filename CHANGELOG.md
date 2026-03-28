# Changelog

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
