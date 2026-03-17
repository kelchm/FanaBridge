# FanaBridge

> **This is beta software.** Expect rough edges. Bug reports and feedback are welcome via [Issues](../../issues).

A [SimHub](https://www.simhubdash.com/) plugin that provides native LED and display control for Fanatec steering wheels via HID.

FanaBridge communicates directly with Fanatec wheel hardware, enabling SimHub to drive the RGB LEDs and displays on supported wheels.

## Features

- **RGB button and encoder LEDs** — Full RGB color and 8-level intensity per LED, including Rev/RPM and Flag/status LED strips where present
- **3-digit display** — Wheels with a basic display show gear, speed, or gear-then-speed overlay
- **SimHub LED profiles** — Wheels appear in SimHub's Devices view with standard LED Editor support
- **Compatible with LED profile plugins** — Works with [ATSR Hub EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO) and other SimHub LED profile plugins
- **Automatic wheel detection** — Detects connected wheels via the Fanatec SDK and matches to a profile
- **Hot-plug support** — Reconnects gracefully when devices are plugged/unplugged or wheels are swapped
- **Custom wheel profiles** — JSON-based profiles mean unsupported wheels can be added without waiting for a plugin update
- **Wheel Profile Wizard** — Guided in-app wizard probes your hardware to generate a profile for any unsupported wheel
- **Experimental: Encoder mode** — Configure encoder behavior (relative, pulse, constant, auto) directly on supported devices

### Planned

- Customizable telemetry display and arbitrary text/messages (e.g., function layer activation)
- ITM display support

## Supported Wheels

Built-in profiles are included for:

| Wheel / Configuration | LEDs | Display |
|---|---|---|
| Podium Steering Wheel BMW M4 GT3 | 12 color + 3 mono (encoders) | 3-digit |
| Podium Hub + Button Module Rally (BMR) | 12 color LEDs | 3-digit |
| Podium Hub + Button Module Endurance (BME) | 9 rev + 6 flag LEDs | ITM (basic support) |

### Unsupported wheels

If your wheel isn't listed, use the **Wheel Profile Wizard** (see [Usage](#usage)) to generate a profile. Created profiles can be [shared on GitHub](../../issues) to benefit other users.

## Requirements

- [SimHub](https://www.simhubdash.com/) (latest version recommended)
- A Fanatec wheelbase connected via USB
- A supported Fanatec steering wheel

## Installation

1. Download the latest release `.zip` from the [Releases](../../releases) page
2. Close SimHub if it's running
3. Extract the archive directly into your SimHub installation directory (e.g., `C:\Program Files (x86)\SimHub\`)
   - `FanaBridge.dll` goes in the SimHub root
   - `DevicesLogos\` files go into the `DevicesLogos\` subdirectory
4. Start SimHub and enable the FanaBridge plugin

## Usage

1. In the Fanatec software, set **Fanatec App LED/Display output** to **Disabled** (otherwise the Fanatec driver and FanaBridge will fight over control of the LEDs and display)
2. Connect your Fanatec wheelbase and attach a supported wheel
3. In SimHub, go to **Devices**, click **Add** (+), then **Add New Device**, and choose your wheel or hub/module combo
4. Configure LED profiles using SimHub's built-in LED Editor, or with a third-party LED profile plugin like [ATSR Hub EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO)

### Adding an unsupported wheel

If your wheel doesn't appear in the Devices list, you can create a profile for it:

1. Open the FanaBridge plugin settings tab in SimHub
2. Connect your wheel — it will show as "connected" even without a profile
3. Click **New Profile Wizard** and follow the steps
   - The wizard sends test signals to your wheel (LEDs light up, display shows a test pattern) and asks what you observe
   - It uses your answers to build a profile describing the hardware layout
4. Once saved, restart SimHub so the new device appears in the Devices list
5. Optionally, share your profile via the **Share it on GitHub** link in the settings — this helps other users with the same wheel

## Building from Source

### Prerequisites

- .NET SDK (includes .NET Framework 4.8 targeting pack)
- SimHub installed (the project references DLLs from the SimHub directory)

### Build

```powershell
dotnet build FanaBridge\FanaBridge.csproj
```

To install directly to your local SimHub (for development):

```powershell
dotnet build FanaBridge\FanaBridge.csproj -p:InstallToSimHub=true
```

The `SimHubDir` property defaults to `C:\Program Files (x86)\SimHub\`. Override it in `Directory.Build.props.user` if your install is elsewhere.

## Disclaimer

FanaBridge is an independent, open-source project and is not affiliated with, endorsed by, or sponsored by Fanatec, Endor AG, Corsair, or any of their affiliated companies. The name “Fanatec” is used solely to describe hardware compatibility. Product images are used only for device identification purposes and remain the property of their respective owners. All trademarks and copyrights are the property of their respective owners. This software is provided “as is,” without warranty of any kind.

## License

[MIT](LICENSE)
