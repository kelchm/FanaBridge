# Button Modules

Button modules attach to [hubs](steering-wheels.md#hubs) via a USB-C interface. They provide LEDs, displays, buttons, and encoders that extend the hub's capabilities. Standalone wheels cannot accept modules — only hubs have the required physical interface.

## Compositional Capability Model

A hub's effective capabilities are the **combination** of its own native features plus whatever module is attached. The module defines what LEDs, displays, and other features become available — the hub serves as the mounting platform and communication bridge.

```
Hub (native features) + Module (provided features) = Effective capabilities
```

This model is not hardcoded to specific modules. If a new module were released with different capabilities, any compatible hub would gain those capabilities by connecting it.

### Effective Capability Matrix

| Feature | Hub Alone | Hub + PBME | Hub + PBMR |
|---------|-----------|------------|------------|
| Rev LEDs | None | 9 (RGB565, col03) | None |
| Flag LEDs | None | 6 (RGB565, col03) | None |
| Button LEDs | None | Yes (RGB565, staged) | 9 (RGB555, col03) |
| Encoder LEDs | None | Yes (intensity, col03) | 3 (intensity, col03) |
| Display | Hub-dependent | 2.7" OLED (ITM + legacy) | ~1" OLED (7-seg protocol only) |
| ITM | None | Yes (Device ID 3, col03) | None |
| col03 Input Reports | None | Yes | None |

> **Note:** Some hubs (CSWRUH, CSWRUHX) have a native 7-segment display. How this interacts with a module's display when both are present is [unverified](steering-wheels.md#native-hub-capabilities).

## Module Types

| ID | Enum Name | Display Name |
|----|-----------|-------------|
| 0 | (none) | No module attached |
| 1 | PBME | Podium Button Module Endurance |
| 2 | PBMR | Podium Button Module Rally |

## Podium Button Module Endurance (PBME)

The PBME is the more capable of the two modules, featuring a 2.7" 256x64 OLED display and full LED support.

### Capabilities

| Feature | Details | Protocol |
|---------|---------|----------|
| Rev LEDs | 9 LEDs, per-LED RGB565 color | Modern (col03) |
| Flag LEDs | 6 LEDs, per-LED RGB565 color | Modern (col03) |
| Button LEDs | RGB565 color + per-button intensity (staged commit) | Modern (col03) |
| Encoder LEDs | Intensity-only, part of button intensity payload | Modern (col03) |
| Display | 2.7" 256x64 OLED — ITM mode + legacy mode | col03 (ITM) / col01 (legacy) |

### Display

The PBME has a single OLED display that operates in two modes:

- **ITM mode** — Full telemetry dashboards via the col03 ITM protocol (pages 1–5). Uses Device ID 3 on the wire. See [ITM Display Protocol](../protocol/display-itm.md) for page layouts.
- **Legacy mode** — Page 6 (the last ITM page). Renders 7-segment-style content and is addressed via the same col01 7-segment commands used for physical LED 7-segment displays. This mode is active when no ITM telemetry is being sent.

### Display Ownership

The PBME's OLED display supports the `SevenSegmentModeEnable` command for explicit display ownership control:

```
Host takes control:   [RID, F8, 09, 01, 18, 02, 00, 00]
Release to firmware:  [RID, F8, 09, 01, 18, 01, 00, 00]
```

This is important during CBP adjustments and tuning menu navigation, where the firmware needs to show its own content. See [7-Segment Display — Display Ownership](../protocol/display-7seg.md#display-ownership).

> **SDK note:** The native SDK checks `IsSevenSegmentOLED` (a flag set in the native DLL constructor) before sending this command. The managed SDK declares `FSSevenSegmentModeEnable` but never calls it directly — display ownership appears to be managed entirely in the native layer.

### col03 Input Reports

The PBME sends col03 input reports (device → host) for events like analysis page changes. This enables the firmware-driven notification system for page changes:

- `AnalysisPageChanged` — fires when the user navigates tuning/analysis pages
- Used by the SDK to detect when to yield or reclaim display control

## Podium Button Module Rally (PBMR)

The PBMR is a simpler module focused on rally-style controls. It provides button LEDs and a small display but lacks rev LEDs, flag LEDs, and ITM support.

### Capabilities

| Feature | Details | Protocol |
|---------|---------|----------|
| Rev LEDs | **None** | — |
| Flag LEDs | **None** | — |
| Button LEDs | 9 LEDs, RGB555 color (5-5-5 bit) | Modern (col03) |
| Encoder LEDs | 3 LEDs, intensity-only | Modern (col03) |
| Display | ~1" OLED (resolution unknown) | col01 (7-seg protocol only) |
| ITM Display | **None** | — |

### Display

The PBMR has a small ~1" OLED display. Despite being a dot-matrix OLED capable of arbitrary rendering, all research to date indicates it is only addressable via the same col01 7-segment commands used for physical LED 7-segment displays. It does not support ITM mode.

### Color Format Difference

The PBMR uses **RGB555** (5-5-5 bit) color encoding instead of the standard RGB565. This means the green channel has 5 bits of precision (0–31) instead of 6 bits (0–63), resulting in a slightly reduced color range.

### No col03 Input Reports

Unlike the PBME, the PBMR does **not** send col03 input reports. This means:

- No `AnalysisPageChanged` notifications
- No firmware-driven page change detection
- CBP mode detection must rely on alternative methods (e.g., registry monitoring on Windows)

### Display Ownership

The `SevenSegmentModeEnable` command is a **no-op** on the PBMR. Despite having an OLED panel, the PBMR does not support display ownership handoff — display conflict management (e.g., during CBP adjustment) must be handled by pausing host display writes.

## Compatible Hubs

See [Hubs](steering-wheels.md#hubs) for the full list of module-compatible hubs.

| Hub ID | Hub Name |
|--------|----------|
| 5 | CSWRUH |
| 6 | CSWRUHX |
| 12 | PHUB |
| 14 | CSLUHUB |
| 18 | CSUHV2 |

SIDESWIPE (26) is classified as a hub but does **not** support module attachment. See [Hub Types](steering-wheels.md#hub-types).
