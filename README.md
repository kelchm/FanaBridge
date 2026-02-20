# Fanatec M4 GT3 LED Control - C# PoC

A minimal proof-of-concept for controlling Fanatec M4 GT3 wheel LEDs via USB HID on Windows.

## Prerequisites

- **.NET 8 SDK** (or later)
  - Download: https://dotnet.microsoft.com/download
  - Or: `winget install Microsoft.DotNet.SDK.8`

## Project Setup

### 1. Install .NET SDK

**Windows (via winget):**
```powershell
winget install Microsoft.DotNet.SDK.8
```

**Or download from:** https://dotnet.microsoft.com/download/dotnet/8.0

Verify installation:
```powershell
dotnet --version
```

### 2. Restore dependencies

```powershell
cd c:\Users\kelchm\Development\simhub-fanatec-plugin
dotnet restore
```

### 3. Build

```powershell
dotnet build
```

### 4. Run

```powershell
dotnet run
```

## Project Structure

- **FanatecHidDevice.cs** – Core HID communication layer
  - `Connect()` – Find and open the wheel device
  - `SetIntensity()` – Control per-LED brightness (0-7 scale)
  - `SetColors()` – Control per-LED colors (RGB565)
  - `SetLed()` – Convenience method to set color + intensity together

- **ColorHelper.cs** – Color utilities
  - RGB → RGB565 conversion
  - Predefined color constants

- **Program.cs** – Demo code showing:
  - Single LED control
  - Multi-LED control
  - Brightness sweeps
  - Pulse effect

## Key Protocol Details

### Intensity Report (0xFF 0x01 0x03)
- Offset 0–2: Command
- Offset 3–18: 16 intensity values (I0–I15, scale 0-7)
- Offset 18: Apply flag (0x00 = stage, 0x01 = commit)

### Color Report (0xFF 0x01 0x02)
- Offset 0–2: Command
- Offset 3–26: 12 × RGB565 colors (S0–S11, big-endian)
- Offset 27: Apply flag (0x00 = stage, 0x01 = commit)

### Button → LED Mapping
- Buttons 1–6: Left side (indices 0–5)
- Buttons 7–12: Right side (indices 6–11)

## Troubleshooting

### Device not found
- Verify wheel is connected and USB driver is installed
- Check Device Manager: Look for "HID-compliant device"
- Try replugging the USB

### Permission denied
- Run as Administrator (right-click PowerShell → Run as administrator)

### Build errors
- Ensure .NET 8 SDK is installed: `dotnet --version`
- Clean and rebuild: `dotnet clean && dotnet build`

## Next Steps

- Integrate into SimHub plugin
- Add animation engine
- Add configuration UI
- Handle device hotplug
