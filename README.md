# FanaBridge

> **This is beta software.** Expect rough edges. Bug reports and feedback are welcome via [Issues](../../issues).

A [SimHub](https://www.simhubdash.com/) plugin that provides native LED and display control for Fanatec steering wheels via HID.

FanaBridge communicates directly with Fanatec wheel hardware, enabling SimHub to drive the RGB LEDs and basic OLED displays on supported wheels.

## Features

- **RGB button and encoder LEDs** — Full RGB color and 8-level intensity per LED
- **Basic OLED display** — Wheels with a non-ITM OLED act as a 3-digit 7-segment style display (gear, speed, etc.)
- **SimHub LED profiles** — Wheels appear in SimHub's Devices view with standard LED Editor support
- **Compatible with LED profile plugins** — Works with [ATSR Hub EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO) and other SimHub LED profile plugins
- **Automatic wheel detection** — Detects connected wheels via the Fanatec SDK and adapts to capabilities
- **Hot-plug support** — Reconnects gracefully when devices are plugged/unplugged or wheels are swapped

### Planned

- Support for additional wheels and hub/module combinations
- Customizable telemetry display and arbitrary text/messages (e.g., function layer activation)
- Encoder mode configuration

## Supported Wheels

- **Podium Steering Wheel BMW M4 GT3** — 12 button LEDs, 3-digit display
- **Podium Hub + Button Module Rally** — 9 button LEDs, 3 encoder LEDs, 3-digit display

> The **Button Module Endurance** is a work in progress. Other Fanatec wheels do not work yet — support for additional wheels and hub/module combinations is planned.

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
3. In SimHub, go to **Devices**, click **Add** (+), then **Add New Device**, and choose your supported wheel or hub/module combo
4. Configure LED profiles using SimHub's built-in LED Editor, or with a third-party LED profile plugin like [ATSR Hub EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO)

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
