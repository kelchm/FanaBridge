# Fanatec Devices

The Fanatec ecosystem consists of four hardware categories:

- **Wheelbases** — The motor unit that connects to the PC via USB and provides force feedback. All HID communication flows through the wheelbase.
- **Wheels** — Self-contained steering wheel rims with a passive quick-release connection to the wheelbase. Have built-in buttons and may have LEDs, displays, and encoders. Their capabilities are fixed by the hardware.
- **Hubs** — Active mounting platforms with their own PCB/MCU and a quick-release connection to the wheelbase. Designed for attaching third-party or custom steering wheels. Have a USB-C interface for connecting a button module.
- **Button modules** — Attach to hubs via USB-C. Provide LEDs, displays, and additional buttons. A hub's effective capabilities are **compositional** — determined by the hub's native features plus the attached module's capabilities.

> **Note:** The Fanatec SDK uses a single `STEERINGWHEEL_TYPE` enum for both wheels and hubs, and a separate `BUTTON_MODULE_TYPE` enum for modules. The SDK does not enforce the physical constraint that only hubs can accept modules.

## Table of Contents

- [Device Identification](#device-identification)
- [Wheelbases](#wheelbases)
- [Wheels](#wheels)
  - [Rev LEDs](#rev-leds)
  - [Flag LEDs](#flag-leds)
  - [RGB LED Support](#rgb-led-support)
  - [Button LEDs](#button-leds)
  - [Display Capabilities](#display-capabilities)
  - [APM (Advanced Paddle Mode)](#apm-advanced-paddle-mode)
  - [Wheel Protocol Summary](#wheel-protocol-summary)
- [Hubs](#hubs)
  - [Hub Types](#hub-types)
  - [Native Hub Capabilities](#native-hub-capabilities)
  - [Module Capabilities](#module-capabilities)
- [Button Modules](#button-modules)
  - [Compositional Capability Model](#compositional-capability-model)
  - [PBME (Podium Button Module Endurance)](#pbme-podium-button-module-endurance)
  - [PBMR (Podium Button Module Rally)](#pbmr-podium-button-module-rally)

---

## Device Identification

All Fanatec wheelbases share a common USB **Vendor ID**: `0x0EB7` (Endor AG).

The **Product ID** varies by wheelbase model. Within a session, the wheelbase reports the connected wheel/hub type and button module type, which determine the available features and protocol capabilities.

### Identification Hierarchy

```
USB Device (VID=0x0EB7, PID=wheelbase-specific)
  └─ Wheelbase (BASE_TYPE enum)
      └─ Wheel or Hub (STEERINGWHEEL_TYPE enum)
          └─ Button Module (BUTTON_MODULE_TYPE enum, hubs only)
```

The wheelbase acts as the communication hub — all HID reports are sent to/from the wheelbase, which routes commands internally to the attached peripherals.

### Feature Capabilities

| Feature | Depends On | Protocol |
|---------|-----------|----------|
| Rev LEDs | Wheel type or attached module | col03 (modern) or col01 (legacy) |
| Flag LEDs | Wheel type or attached module | col03 or col01 |
| Button LEDs | Wheel type or attached module | col03 |
| RevStripe | Specific wheels only | col01 |
| 7-Segment Display | Wheel, hub, or module | col01 |
| ITM Display | Specific bases, wheels, or modules | col03 |
| Tuning Menu | All supported bases | col03 |
| Clutch Bite Point | Wheels/hubs with clutch paddles | col01 |

---

## Wheelbases

All Fanatec wheelbases connect to the host PC via USB and expose HID endpoints for control. The wheelbase handles force feedback and acts as the communication hub for attached steering wheels and button modules.

### Known Wheelbases

| ID | Enum Name | Display Name | col03 Support | Tuning Menu | Base ITM |
|----|-----------|-------------|---------------|-------------|----------|
| 1 | CSWV2 | ClubSport Wheel Base V2 | Yes | Yes | No |
| 2 | CSWV25 | ClubSport Wheel Base V2.5 | Yes | Yes | No |
| 3 | CSLE_1_0 | CSL Elite Wheel Base 1.0 | Yes | Yes | No |
| 4 | CSLE_1_1 | CSL Elite Wheel Base 1.1 | Yes | Yes | No |
| 5 | CSLEPS4 | CSL Elite Wheel Base+ (PS4) | Yes | Yes | No |
| 6 | PDD1 | Podium Wheel Base DD1 | Yes | Yes | **Yes** |
| 7 | PDD1_PS4 | Podium Wheel Base DD1 (PS4) | Yes | Yes | **Yes** |
| 8 | PDD2 | Podium Wheel Base DD2 | Yes | Yes | **Yes** |
| 9 | GTDDPRO | GT DD PRO Wheel Base | Yes | Yes | No |
| 10 | CSLDD | CSL DD Wheel Base | Yes | Yes | No |
| 11 | CSDD | ClubSport DD Wheel Base | Yes | Yes | No |
| 12 | CSDDPlus | ClubSport DD+ Wheel Base | Yes | Yes | No |
| 13 | PDD25 | Podium Wheel Base DD | Yes | Yes | No |
| 14 | PDD25PLUS | Podium Wheel Base DD+ | Yes | Yes | No |

### USB Product IDs

| Product ID | Wheelbase |
|------------|-----------|
| `0x0005` | CSL Elite series |
| `0x0006` | ClubSport V2 / V2.5 |
| `0x0020` | ClubSport DD+ |
| Others | TBD — additional product IDs not yet cataloged |

> **Note:** The complete USB PID mapping is incomplete. The table above includes confirmed values.

### Base ITM Display

Only three wheelbases have a built-in ITM display:

- **PDD1** (Podium Wheel Base DD1)
- **PDD1_PS4** (Podium Wheel Base DD1 for PS4)
- **PDD2** (Podium Wheel Base DD2)

These use **Device ID 1** for ITM commands. See the [ITM display protocol](#itm-display) section for details.

Other wheelbases (CSDD, CSDDPlus, GTDDPRO, CSLDD, etc.) do not have a base display, but ITM is still available through compatible steering wheels or button modules.

### col03 Capability

All current-generation wheelbases support col03 (64-byte reports). Whether col03 is actually used for a given session depends on the **steering wheel** attached — some older rims only support col01.

The wheelbase opens the col03 endpoint at initialization based on the connected wheel's device ID. See the [collection routing](protocol.md#collection-routing) section for the routing mechanism.

---

## Wheels

Fanatec uses a single `STEERINGWHEEL_TYPE` enum for both wheels and hubs. See [Hubs](#hubs) for the hub entries.

### STEERINGWHEEL_TYPE Enum

| ID | Enum Name | Display Name | Category |
|----|-----------|-------------|----------|
| 0 | UNINITIALIZED | (not connected) | — |
| 1 | UNKNOWN | Unknown | Wheel |
| 2 | CSWRBMW | ClubSport BMW GT2 | Wheel |
| 3 | CSWRFORM | ClubSport Formula | Wheel |
| 4 | CSWRPORSCHE | ClubSport Porsche | Wheel |
| 5 | CSWRUH | ClubSport Universal Hub | **Hub** |
| 6 | CSWRUHX | ClubSport Universal Hub X | **Hub** |
| 7 | CSLRP1X | CSL Elite P1 (Xbox) | Wheel |
| 8 | CSLRP1PS4 | CSL Elite P1 (PS4) | Wheel |
| 9 | CSLRMCL | CSL Elite McLaren GT3 | Wheel |
| 10 | CSWRFORMV2 | ClubSport Formula V2 | Wheel |
| 11 | CSLRMCLV1_1 | CSL Elite McLaren GT3 V1.1 | Wheel |
| 12 | PHUB | Podium Hub | **Hub** |
| 13 | DDRGT | Podium Racing Wheel (DD) | Wheel |
| 14 | CSLUHUB | CSL Universal Hub | **Hub** |
| 15 | CSLRWRC | CSL WRC | Wheel |
| 16 | CSSWBMWV2 | ClubSport BMW M3 GT2 V2 | Wheel |
| 17 | CSSWRS | ClubSport RS | Wheel |
| 18 | CSUHV2 | ClubSport Universal Hub V2 | **Hub** |
| 19 | CSSWF1ESV2 | ClubSport F1 Esports V2 | Wheel |
| 20 | PSWBMW | Podium BMW M4 GT3 | Wheel |
| 21 | PSWBENT | Podium Bentley GT3 | Wheel |
| 22 | GTSWX | GT Steering Wheel X | Wheel |
| 23 | CSSWPVGT | ClubSport PVGT | Wheel |
| 24 | CSSWFORMV3 | ClubSport Formula V3 | Wheel |
| 25 | CSLSWGT3 | CSL Steering Wheel GT3 | Wheel |
| 26 | SIDESWIPE | Sideswipe | **Hub** |
| 27 | CSSWFORMV2 | ClubSport Formula V2.5 | Wheel |

> **Note:** The Formula V2.5 (ID 27) reports `wheelType` as `CSSWFORMV2` — the same enum name as the Formula V2 (ID 10). The SDK distinguishes them by numeric ID, not by name.

Wheels are self-contained rims with fixed hardware. Their capabilities are determined entirely by their built-in components — they cannot be extended with modules.

### Rev LEDs

Rev LEDs are the RPM/shift indicator strip, typically 9 LEDs across the top of the wheel.

#### Individually-Addressable Rev LEDs

| ID | Wheel | LED Count | Color | Protocol |
|----|-------|-----------|-------|----------|
| 1 | UNKNOWN | 9 | Non-RGB | Legacy (col01) |
| 2 | CSWRBMW | 9 | Non-RGB | Legacy (col01) |
| 3 | CSWRFORM | 9 | Non-RGB | Legacy (col01) |
| 4 | CSWRPORSCHE | 9 | Non-RGB | Legacy (col01) |
| 10 | CSWRFORMV2 | 9 | **RGB** | Modern (col03) |
| 13 | DDRGT | 9 | Non-RGB | Legacy (col01) |
| 16 | CSSWBMWV2 | 9 | Non-RGB | Legacy (col01) |
| 17 | CSSWRS | 9 | Non-RGB | Legacy (col01) |
| 19 | CSSWF1ESV2 | 9 | **RGB** | Modern (col03) |
| 21 | PSWBENT | 9 | **RGB** | Modern (col03) |
| 22 | GTSWX | 9 | **RGB** | Modern (col03) |
| 24 | CSSWFORMV3 | 9 | **RGB** | Modern (col03) |

#### RevStripe

These wheels have a single-color LED strip instead of individually-addressable rev LEDs:

| ID | Wheel | Color | Protocol |
|----|-------|-------|----------|
| 7 | CSLRP1X | RGB333 | Legacy (col01) |
| 8 | CSLRP1PS4 | RGB333 | Legacy (col01) |
| 15 | CSLRWRC | RGB333 | Legacy (col01) |

RevStripe is controlled as a single unit (index 0 only) with RGB333 color encoding (512 possible colors, SDK uses 8). See [RevStripe protocol](protocol.md#revstripe).

#### No Rev LEDs

| ID | Wheel | Notes |
|----|-------|-------|
| 9 | CSLRMCL | |
| 11 | CSLRMCLV1_1 | |
| 20 | PSWBMW | Has RGB button LEDs but no rev LED strip |
| 23 | CSSWPVGT | No LEDs of any kind |
| 25 | CSLSWGT3 | No LEDs of any kind |

> **SDK note:** The native bitmask for `FSUtilHasWheelRimRevLeds` includes PSWBMW(20). This likely reflects internal SDK routing where button LEDs are reused for rev LED-like functionality, not a physical rev LED strip. CSSWPVGT(23) and CSLSWGT3(25) also appear in some managed SDK code paths with a module fallback, but neither wheel has physical LEDs or a module attachment point.

### Flag LEDs

Flag LEDs are status/warning indicators. Only these wheels have native flag LEDs:

| ID | Wheel |
|----|-------|
| 10 | CSWRFORMV2 |
| 19 | CSSWF1ESV2 |
| 21 | PSWBENT |
| 22 | GTSWX |
| 24 | CSSWFORMV3 |

### RGB LED Support

Wheels with per-LED RGB color support via the modern col03 protocol:

| ID | Wheel | Rev RGB | Flag RGB |
|----|-------|---------|----------|
| 10 | CSWRFORMV2 | Yes | Yes |
| 19 | CSSWF1ESV2 | Yes | Yes |
| 21 | PSWBENT | Yes | Yes |
| 22 | GTSWX | Yes | Yes |
| 24 | CSSWFORMV3 | Yes | Yes |

> **SDK note:** The native bitmask (`FSUtilIsWheelRimRGBLedsSupported`, mask `0x1780400`) also includes bit 20 (PSWBMW). The PSWBMW has no rev LED strip — this bit likely enables the RGB **button LED** code path. CSSWPVGT(23) and CSLSWGT3(25) are **not** set in the native bitmask; these wheels have no physical LEDs.

### Button LEDs

Some wheels have built-in button backlighting:

| ID | Wheel | Protocol | Notes |
|----|-------|----------|-------|
| 20 | PSWBMW | Modern (col03) | RGB button LEDs |
| 22 | GTSWX | Modern (col03) | RGB button LEDs |

### Display Capabilities

Wheels have several distinct display technologies. The display type determines which protocol features are available.

#### Display Types

| Display Type | Technology | Protocol | ITM Capable |
|-------------|-----------|----------|-------------|
| LED 7-segment | Physical LED segments, 3 digits | col01 7-seg only | No |
| Small OLED | ~1" dot-matrix OLED | col01 7-seg protocol only | No |
| Large OLED | 2.7" 256x64 OLED | col01 7-seg + col03 ITM | Yes |
| LCD | 3.4" 800x800 LCD | col03 ITM | Yes |

All display types that support ITM can also operate in a **legacy mode** — this is the last ITM page (page 6 for most devices, page 5 for Bentley), which renders 7-segment-style content. The legacy page is not a separate protocol — it is an ITM page that the firmware uses to display basic information (gear, speed) when no telemetry data is being sent.

The col01 7-segment protocol works identically across LED 7-segment and small OLED displays — the host sends the same segment-encoded bytes regardless of the underlying hardware.

#### Per-Wheel Display Matrix

| ID | Wheel | Display Type | ITM Device ID | Notes |
|----|-------|-------------|---------------|-------|
| 2 | CSWRBMW | LED 7-seg | — | |
| 3 | CSWRFORM | LED 7-seg | — | |
| 4 | CSWRPORSCHE | LED 7-seg | — | |
| 9 | CSLRMCL | Small OLED | — | |
| 10 | CSWRFORMV2 | LED 7-seg | — | |
| 11 | CSLRMCLV1_1 | Small OLED | — | |
| 13 | DDRGT | LED 7-seg | — | |
| 15 | CSLRWRC | LED 7-seg | — | |
| 16 | CSSWBMWV2 | LED 7-seg | — | |
| 17 | CSSWRS | LED 7-seg | — | |
| 19 | CSSWF1ESV2 | LED 7-seg | — | Tentative — needs verification |
| 20 | PSWBMW | Small OLED | — | ~1" OLED, same form factor as PBMR |
| 21 | PSWBENT | LCD | 4 | 3.4" 800x800, dedicated Bentley ITM pages |
| 22 | GTSWX | Unknown | 3 | ITM-capable; display technology unconfirmed |
| 23 | CSSWPVGT | None | — | |
| 24 | CSSWFORMV3 | LED 7-seg | — | Tentative — needs verification |
| 25 | CSLSWGT3 | Small OLED | — | No physical LEDs |

> **Note:** Several display type assignments above (marked tentative) are inferred from SDK data and may not be fully verified against physical hardware.

### APM (Advanced Paddle Mode)

Only wheels with a rotary encoder support the APM tuning parameter:

| ID | Wheel |
|----|-------|
| 9 | CSLRMCL |
| 10 | CSWRFORMV2 |
| 11 | CSLRMCLV1_1 |
| 25 | CSLSWGT3 |

### Wheel Protocol Summary

| Protocol | Collection | Wheels |
|----------|-----------|--------|
| Modern (col03, RGB565) | col03 64B | CSWRFORMV2, CSSWF1ESV2, PSWBENT, GTSWX, CSSWFORMV3 |
| Legacy Non-RGB (bitmask) | col01 8B | CSWRBMW, CSWRFORM, CSWRPORSCHE, DDRGT, CSSWBMWV2, CSSWRS |
| RevStripe (RGB333) | col01 8B | CSLRP1X, CSLRP1PS4, CSLRWRC |
| No rev LED protocol | — | CSLRMCL, CSLRMCLV1_1, PSWBMW, CSSWPVGT, CSLSWGT3 |

---

## Hubs

Hubs are active devices with their own PCB and microcontroller. They serve as a mounting platform and provide a USB-C interface for connecting a [button module](#button-modules). A hub's effective capabilities are the combination of its own native features plus whatever module is attached.

### Hub Types

| ID | Hub | Module Compatible | Notes |
|----|-----|-------------------|-------|
| 5 | CSWRUH | Yes | ClubSport Universal Hub |
| 6 | CSWRUHX | Yes | ClubSport Universal Hub X |
| 12 | PHUB | Yes | Podium Hub — no native LEDs or display |
| 14 | CSLUHUB | Yes | CSL Universal Hub |
| 18 | CSUHV2 | Yes | ClubSport Universal Hub V2 |
| 26 | SIDESWIPE | **No** | Appears designed to adapt third-party wheels to Fanatec wheelbases; no module interface |

> **Note:** SIDESWIPE (26) is unreleased. Its classification as a hub and its capabilities are inferred from SDK data only and should be considered tentative.

### Native Hub Capabilities

Hubs generally have no native LEDs or displays — visual feedback comes from the attached module. However, some older hubs have built-in features:

| ID | Hub | 7-Segment Display | Other Native Features |
|----|-----|-------------------|----------------------|
| 5 | CSWRUH | **Yes** | Built-in 7-seg display |
| 6 | CSWRUHX | **Yes** | Built-in 7-seg display |
| 12 | PHUB | No | — |
| 14 | CSLUHUB | No | — |
| 18 | CSUHV2 | No | — |

> **Unverified:** How the built-in 7-segment display on CSWRUH/CSWRUHX interacts with a module's display (if a module is connected simultaneously) is not yet confirmed. Further SDK research may be needed.

### Module Capabilities

When a button module is connected to a hub, the module's capabilities become available on that hub. The capabilities are determined entirely by the module — see [Button Modules](#button-modules) for the full capability matrix.

For example, any compatible hub with a PBME gains: 9 RGB rev LEDs, 6 RGB flag LEDs, button LEDs, ITM display support, and encoder LEDs. The same hub with a PBMR instead gains: button LEDs, encoder LEDs, and a small display, but no rev LEDs, no flag LEDs, and no ITM.

If Fanatec were to release a new button module with different capabilities, any compatible hub would gain those capabilities simply by connecting the new module — the model is compositional, not hardcoded to specific modules.

---

## Button Modules

Button modules attach to [hubs](#hubs) via a USB-C interface. They provide LEDs, displays, buttons, and encoders that extend the hub's capabilities. Standalone wheels cannot accept modules — only hubs have the required physical interface.

### Compositional Capability Model

A hub's effective capabilities are the **combination** of its own native features plus whatever module is attached. The module defines what LEDs, displays, and other features become available — the hub serves as the mounting platform and communication bridge.

```
Hub (native features) + Module (provided features) = Effective capabilities
```

This model is not hardcoded to specific modules. If a new module were released with different capabilities, any compatible hub would gain those capabilities by connecting it.

#### Effective Capability Matrix

| Feature | Hub Alone | Hub + PBME | Hub + PBMR |
|---------|-----------|------------|------------|
| Rev LEDs | None | 9 (RGB565, col03) | None |
| Flag LEDs | None | 6 (RGB565, col03) | None |
| Button LEDs | None | Yes (RGB565, staged) | 9 (RGB555, col03) |
| Encoder LEDs | None | Yes (intensity, col03) | 3 (intensity, col03) |
| Display | Hub-dependent | 2.7" OLED (ITM + legacy) | ~1" OLED (7-seg protocol only) |
| ITM | None | Yes (Device ID 3, col03) | None |
| col03 Input Reports | None | Yes | None |

> **Note:** Some hubs (CSWRUH, CSWRUHX) have a native 7-segment display. How this interacts with a module's display when both are present is [unverified](#native-hub-capabilities).

### Module Types

| ID | Enum Name | Display Name |
|----|-----------|-------------|
| 0 | (none) | No module attached |
| 1 | PBME | Podium Button Module Endurance |
| 2 | PBMR | Podium Button Module Rally |

### PBME (Podium Button Module Endurance)

The PBME is the more capable of the two modules, featuring a 2.7" 256x64 OLED display and full LED support.

#### Capabilities

| Feature | Details | Protocol |
|---------|---------|----------|
| Rev LEDs | 9 LEDs, per-LED RGB565 color | Modern (col03) |
| Flag LEDs | 6 LEDs, per-LED RGB565 color | Modern (col03) |
| Button LEDs | RGB565 color + per-button intensity (staged commit) | Modern (col03) |
| Encoder LEDs | Intensity-only, part of button intensity payload | Modern (col03) |
| Display | 2.7" 256x64 OLED — ITM mode + legacy mode | col03 (ITM) / col01 (legacy) |

#### Display

The PBME has a single OLED display that operates in two modes:

- **ITM mode** — Full telemetry dashboards via the col03 ITM protocol (pages 1–5). Uses Device ID 3 on the wire. See [ITM Display Protocol](protocol.md#itm-display) for page layouts.
- **Legacy mode** — Page 6 (the last ITM page). Renders 7-segment-style content and is addressed via the same col01 7-segment commands used for physical LED 7-segment displays. This mode is active when no ITM telemetry is being sent.

#### Display Ownership

The PBME's OLED display supports the `SevenSegmentModeEnable` command for explicit display ownership control:

```
Host takes control:   [RID, F8, 09, 01, 18, 02, 00, 00]
Release to firmware:  [RID, F8, 09, 01, 18, 01, 00, 00]
```

This is important during CBP adjustments and tuning menu navigation, where the firmware needs to show its own content. See [Display Ownership](protocol.md#display-ownership).

> **SDK note:** The native SDK checks `IsSevenSegmentOLED` (a flag set in the native DLL constructor) before sending this command. The managed SDK declares `FSSevenSegmentModeEnable` but never calls it directly — display ownership appears to be managed entirely in the native layer.

#### col03 Input Reports

The PBME sends col03 input reports (device → host) for events like analysis page changes. This enables the firmware-driven notification system for page changes:

- `AnalysisPageChanged` — fires when the user navigates tuning/analysis pages
- Used by the SDK to detect when to yield or reclaim display control

### PBMR (Podium Button Module Rally)

The PBMR is a simpler module focused on rally-style controls. It provides button LEDs and a small display but lacks rev LEDs, flag LEDs, and ITM support.

#### Capabilities

| Feature | Details | Protocol |
|---------|---------|----------|
| Rev LEDs | **None** | — |
| Flag LEDs | **None** | — |
| Button LEDs | 9 LEDs, RGB555 color (5-5-5 bit) | Modern (col03) |
| Encoder LEDs | 3 LEDs, intensity-only | Modern (col03) |
| Display | ~1" OLED (resolution unknown) | col01 (7-seg protocol only) |
| ITM Display | **None** | — |

#### Display

The PBMR has a small ~1" OLED display. Despite being a dot-matrix OLED capable of arbitrary rendering, all research to date indicates it is only addressable via the same col01 7-segment commands used for physical LED 7-segment displays. It does not support ITM mode.

#### Color Format Difference

The PBMR uses **RGB555** (5-5-5 bit) color encoding instead of the standard RGB565. This means the green channel has 5 bits of precision (0–31) instead of 6 bits (0–63), resulting in a slightly reduced color range.

#### No col03 Input Reports

Unlike the PBME, the PBMR does **not** send col03 input reports. This means:

- No `AnalysisPageChanged` notifications
- No firmware-driven page change detection
- CBP mode detection must rely on alternative methods (e.g., registry monitoring on Windows)

#### Display Ownership

The `SevenSegmentModeEnable` command is a **no-op** on the PBMR. Despite having an OLED panel, the PBMR does not support display ownership handoff — display conflict management (e.g., during CBP adjustment) must be handled by pausing host display writes.

### Compatible Hubs

See [Hubs](#hubs) for the full list of module-compatible hubs.

| Hub ID | Hub Name |
|--------|----------|
| 5 | CSWRUH |
| 6 | CSWRUHX |
| 12 | PHUB |
| 14 | CSLUHUB |
| 18 | CSUHV2 |

SIDESWIPE (26) is classified as a hub but does **not** support module attachment. See [Hub Types](#hub-types).
