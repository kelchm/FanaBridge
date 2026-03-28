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
  - [Wheel Type Identifiers](#wheel-type-identifiers)
  - [Rev LEDs](#rev-leds)
  - [Flag LEDs](#flag-leds)
  - [RGB LED Support](#rgb-led-support)
  - [Button LEDs](#button-leds)
  - [Display Capabilities](#display-capabilities)
  - [APM (Advanced Paddle Mode)](#apm-advanced-paddle-mode)
  - [Wheel Protocol Summary](#wheel-protocol-summary)
- [Hubs](#hubs)
  - [Hub Types](#hub-types)
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
| 3 | CSLE_1_0 | CSL Elite Wheel Base | Yes | Yes | No |
| 4 | CSLE_1_1 | CSL Elite Wheel Base | Yes | Yes | No |
| 5 | CSLEPS4 | CSL Elite Wheel Base+ (PS4) | Yes | Yes | No |
| 6 | PDD1 | Podium Wheel Base DD1 | Yes | Yes | **Yes** |
| 7 | PDD1_PS4 | Podium Wheel Base DD1 | Yes | Yes | **Yes** |
| 8 | PDD2 | Podium Wheel Base DD2 | Yes | Yes | **Yes** |
| 9 | GTDDPRO | GT DD PRO Wheel Base | Yes | Yes | No |
| 10 | CSLDD | CSL DD Wheel Base | Yes | Yes | No |
| 11 | CSDD | ClubSport DD Wheel Base | Yes | Yes | No |
| 12 | CSDDPlus | ClubSport DD+ Wheel Base | Yes | Yes | No |
| 13 | PDD25 | Podium Wheel Base DD | Yes | Yes | No |
| 14 | PDD25PLUS | Podium Wheel Base DD+ | Yes | Yes | No |

> **Note:** The SDK returns abbreviated names for some wheelbases (e.g., "CSL DD" without "Wheel Base"). We normalize all names to include "Wheel Base" for consistency. The SDK also does not distinguish CSLE_1_0/CSLE_1_1 or PDD1/PDD1_PS4 by display name — the enum names preserve the hardware revision distinction.

### USB Product IDs

| Product ID | Wheelbase |
|------------|-----------|
| `0x0005` | CSL Elite series |
| `0x0006` | ClubSport V2 / V2.5 |
| `0x0020` | ClubSport DD+ |

> **Note:** The complete USB PID mapping is incomplete. The table above includes confirmed values only.

### Base ITM Display

Only three wheelbases have a built-in ITM display:

- **PDD1** (Podium Wheel Base DD1)
- **PDD1_PS4** (Podium Wheel Base DD1 for PS4)
- **PDD2** (Podium Wheel Base DD2)

These use **Device ID 1** for ITM commands. See the [ITM display protocol](protocol.md#0x05--itm-display) section for details.

Other wheelbases (CSDD, CSDDPlus, GTDDPRO, CSLDD, etc.) do not have a base display, but ITM is still available through compatible steering wheels or button modules.

### col03 Capability

All current-generation wheelbases support col03 (64-byte reports). Whether col03 is actually used for a given session depends on the **steering wheel** attached — some older rims only support col01.

The wheelbase opens the col03 endpoint at initialization based on the connected wheel's device ID. See the [collection routing](protocol.md#collection-routing) section for the routing mechanism.

---

## Wheels

Fanatec uses a single `STEERINGWHEEL_TYPE` enum for both wheels and hubs. See [Hubs](#hubs) for the hub entries.

### STEERINGWHEEL_TYPE Enum

| ID | FanaBridge Profile | Display Name | Category |
|----|-------------------|--------------|----------|
| 0 | — | (not connected) | — |
| 1 | — | Unknown | — |
| 2 | CSSWBMW | ClubSport Steering Wheel BMW M3 GT2 | Wheel |
| 3 | CSSWFORM | ClubSport Steering Wheel Formula Carbon | Wheel |
| 4 | CSSWPORSCHE | ClubSport Steering Wheel Porsche 918 RSR | Wheel |
| 5 | — | ClubSport Universal Hub | **Hub** |
| 6 | — | ClubSport Universal Hub for Xbox One | **Hub** |
| 7 | CSLESWP1X | CSL Elite Steering Wheel P1 for Xbox One | Wheel |
| 8 | CSLESWP1PS4 | CSL Elite Steering Wheel P1 for PlayStation 4 | Wheel |
| 9 | CSLESWMCL | CSL Elite Steering Wheel McLaren GT3 V1.0 | Wheel |
| 10 | CSSWFORMV2 | ClubSport Steering Wheel Formula V2 | Wheel |
| 11 | CSLESWMCLV2 | CSL Elite Steering Wheel McLaren GT3 V2 | Wheel |
| 12 | PHUB | Podium Hub | **Hub** |
| 13 | GTSWPRO | GT DD PRO Steering Wheel | Wheel |
| 14 | — | CSL Universal Hub | **Hub** |
| 15 | CSLESWWRC | CSL Elite Steering Wheel WRC | Wheel |
| 16 | CSSWBMWV2 | ClubSport Steering Wheel BMW M3 GT2 V2 | Wheel |
| 17 | CSSWRS | ClubSport Steering Wheel RS | Wheel |
| 18 | — | ClubSport Universal Hub V2 | **Hub** |
| 19 | CSSWF1ESV2 | ClubSport Steering Wheel F1 Esports V2 | Wheel |
| 20 | PSWBMW | Podium Steering Wheel BMW M4 GT3 | Wheel |
| 21 | PSWBENT | Podium Steering Wheel Bentley GT3 | Wheel |
| 22 | GTSWX | GT Steering Wheel Extreme | Wheel |
| 23 | CSSWPVGT | CSL Elite Steering Wheel Porsche Vision GT | Wheel |
| 24 | CSSWFORMV3 | ClubSport Steering Wheel Formula V3 | Wheel |
| 25 | CSLSWGT3 | CSL Steering Wheel GT3 | Wheel |
| 26 | — | Sideswipe | **Hub** |

> **Note:** The ClubSport Steering Wheel Formula V2.5 is a variant of ID 10, distinguished by `RIM_FORMV2_TYPE.V25`. The FanatecLib enum has `P2111 = 27` for this variant, but the GameControlService treats it as a sub-type of ID 10. The SDK display name is "ClubSport Steering Wheel Formula V2.5".

Wheels are self-contained rims with fixed hardware. Their capabilities are determined entirely by their built-in components — they cannot be extended with modules.

### Wheel Type Identifiers

The integer ID is the stable identifier for a wheel type. However, the **string names** for these IDs vary across different Fanatec SDK versions and the SimHub managed DLL. FanaBridge profile IDs do not always match the string the SDK reports at runtime.

#### Naming Divergence

Three naming systems exist across SDK sources:

| Convention | Pattern | Example (ID 2) | Used By |
|-----------|---------|-----------------|---------|
| CSSW / CSLESW | "Steering Wheel" abbreviation | CSSWBMW, CSLESWP1X | SimHub DLL, GCS Enum, Fanatec UI |
| CSWR / CSLR | "Wheel Rim" abbreviation | CSWRBMW, CSLRP1X | GCS Constants (older) |
| Product name | Marketing/product name | DDRGT, BENTLEY | Mixed across sources |

FanaBridge profiles primarily use the CSSW/CSLESW convention (matching the SimHub DLL and GCS Enum). One profile retains an older name that is handled via alias mapping at runtime:

| FanaBridge Profile | SimHub DLL Reports | Resolved By |
|-------------------|-------------------|-------------|
| PSWBENT | BENTLEY | Alias in `WheelProfileStore.NormalizeWheelType()` |

#### Cross-Reference Table

For wheels where the identifier differs across sources:

| ID | FanaBridge | SimHub DLL | GCS Enum | GCS Constants | Fanatec UI Asset |
|----|-----------|-----------|----------|---------------|------------------|
| 2 | CSSWBMW | CSSWBMW | CSSWBMW | CSWRBMW | CSSWBMW |
| 3 | CSSWFORM | CSSWFORM | CSSWFORM | CSWRFORM | CSSWFORM |
| 4 | CSSWPORSCHE | CSSWPORSCHE | CSSWPORSCHE | CSWRPORSCHE | CSSWPORSCHE |
| 7 | CSLESWP1X | CSLESWP1X | CSLESWP1X | CSLRP1X | CSLESWP1X |
| 8 | CSLESWP1PS4 | CSLESWP1PS4 | CSLESWP1PS4 | CSLRP1PS4 | CSLESWP1PS4 |
| 9 | CSLESWMCL | CSLESWMCL | CSLESWMCL | CSLRMCL | CSLRMCL |
| 11 | CSLESWMCLV2 | CSLESWMCLV2 | CSLESWMCLV2 | CSLRMCLV1_1 | CSLRMCL_V2 |
| 13 | GTSWPRO | GTSWPRO | GTSWPRO | DDRGT | GTSWPRO |
| 15 | CSLESWWRC | CSLESWWRC | CSLESWWRC | CSLRWRC | CSLESWWRC |
| 21 | PSWBENT | BENTLEY | PSWBENT | BENTLEY | PSWBENT |

IDs not listed (5, 6, 10, 12, 14, 16-20, 22) have consistent names across all sources.

#### Wheels Not in SimHub DLL

The following wheels are defined in newer SDK versions but absent from the current SimHub managed DLL. Profiles exist for these wheels but will not auto-match until SimHub updates its DLL:

| ID | FanaBridge Profile | Wheel |
|----|-------------------|-------|
| 23 | CSSWPVGT | CSL Elite Steering Wheel Porsche Vision GT |
| 24 | CSSWFORMV3 | ClubSport Steering Wheel Formula V3 |
| 25 | CSLSWGT3 | CSL Steering Wheel GT3 |

> **Note:** ID 24 is assigned to `CSLUHUBV2` (CSL Universal Hub V2) in the current SimHub DLL, but to `CSSWFORMV3` (Formula V3) in the newer GCS Enum. This indicates the ID was reassigned between SDK versions.

### Rev LEDs

Rev LEDs are the RPM/shift indicator strip, typically 9 LEDs across the top of the wheel.

#### Individually-Addressable Rev LEDs

| ID | Wheel | LED Count | Color | Protocol |
|----|-------|-----------|-------|----------|
| 2 | CSWRBMW | 9 | Non-RGB | Legacy (col01) |
| 3 | CSWRFORM | 9 | Non-RGB | Legacy (col01) |
| 4 | CSWRPORSCHE | 9 | Non-RGB | Legacy (col01) |
| 13 | DDRGT | 9 | Non-RGB | Legacy (col01) |
| 16 | CSSWBMWV2 | 9 | Non-RGB | Legacy (col01) |
| 17 | CSSWRS | 9 | Non-RGB | Legacy (col01) |
| 10 | CSWRFORMV2 | 9 | **RGB** | Modern (col03) |
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

RevStripe is controlled as a single unit (index 0 only) with RGB333 color encoding (512 possible colors, SDK uses 8). See [RevStripe protocol](protocol.md#0x06--revstripe-enabledisable).

#### No Rev LEDs

| ID | Wheel | Notes |
|----|-------|-------|
| 9 | CSLRMCL | |
| 11 | CSLRMCLV1_1 | |
| 20 | PSWBMW | Has RGB button LEDs but no rev LED strip |
| 23 | CSSWPVGT | No rev, flag, or button LEDs |
| 25 | CSLSWGT3 | No rev, flag, or button LEDs |

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
| OLED (Basic) | Dot-matrix OLED, typically ~1" | col01 7-seg only | No |
| OLED (ITM) | Larger dot-matrix OLED (e.g., PBME: 2.7" 256x64) | col01 7-seg + col03 ITM | Yes |
| LCD | Graphical LCD (e.g., 3.4" 800x800) | col03 ITM | Yes |

**OLED (Basic)** displays render 7-segment-style content and are addressed with the same col01 commands as physical LED 7-segment displays. The SDK planned a dedicated "SmallOLED" ITM mode (Device ID 2, with its own 11-page layout) but this feature is disabled in current firmware.

**OLED (ITM)** and **LCD** displays support full telemetry dashboards via the col03 ITM protocol. They can also operate in **legacy mode** — the last ITM page (page 6 for most devices, page 5 for Bentley), which renders 7-segment-style content when no telemetry data is being sent.

#### Per-Wheel Display Matrix

| ID | Wheel | Display Type | ITM Device ID | Notes |
|----|-------|-------------|---------------|-------|
| 2 | CSWRBMW | LED 7-seg | — | |
| 3 | CSWRFORM | LED 7-seg | — | |
| 4 | CSWRPORSCHE | LED 7-seg | — | |
| 7 | CSLRP1X | LED 7-seg | — | |
| 8 | CSLRP1PS4 | LED 7-seg | — | |
| 15 | CSLRWRC | LED 7-seg | — | |
| 16 | CSSWBMWV2 | LED 7-seg | — | |
| 17 | CSSWRS | LED 7-seg | — | |
| 19 | CSSWF1ESV2 | LED 7-seg | — | |
| 9 | CSLRMCL | OLED (Basic) | — | |
| 10 | CSWRFORMV2 | OLED (Basic) | — | |
| 11 | CSLRMCLV1_1 | OLED (Basic) | — | |
| 13 | DDRGT | OLED (Basic) | — | |
| 20 | PSWBMW | OLED (Basic) | — | |
| 23 | CSSWPVGT | OLED (Basic) | — | Round display; ITM planned but disabled in SDK |
| 24 | CSSWFORMV3 | OLED (Basic) | — | |
| 25 | CSLSWGT3 | OLED (Basic) | — | |
| 22 | GTSWX | OLED (ITM) | 3 | Dedicated GTSWX ITM pages |
| 21 | PSWBENT | LCD | 4 | 3.4" 800x800, dedicated Bentley ITM pages |

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

| ID | Enum Name | Display Name | Module Compatible | Native 7-Seg |
|----|-----------|-------------|-------------------|--------------|
| 5 | CSWRUH | ClubSport Universal Hub | Yes | **Yes** |
| 6 | CSWRUHX | ClubSport Universal Hub for Xbox One | Yes | **Yes** |
| 12 | PHUB | Podium Hub | Yes | No |
| 14 | CSLUHUB | CSL Universal Hub | Yes | No |
| 18 | CSUHV2 | ClubSport Universal Hub V2 | Yes | No |
| 26 | SIDESWIPE | Sideswipe | **No** | No |

> **Note:** SIDESWIPE (26) is unreleased. Its classification as a hub and its capabilities are inferred from SDK data only and should be considered tentative.

> **Unverified:** How the built-in 7-segment display on CSWRUH/CSWRUHX interacts with a module's display (if a module is connected simultaneously) is not yet confirmed.

> **SDK note:** The SDK names these hubs with a "Steering Wheel" prefix (e.g., "ClubSport Steering Wheel Universal Hub") because they share the `STEERINGWHEEL_TYPE` enum with wheels. We use shortened names here for clarity.

### Module Capabilities

When a button module is connected to a hub, the module's capabilities become available on that hub. The capabilities are determined entirely by the module — see [Button Modules](#button-modules) for the full capability matrix.

For example, any compatible hub with a PBME gains: 9 RGB rev LEDs, 6 RGB flag LEDs, a 2.7" OLED with ITM support, and display ownership control. The same hub with a PBMR instead gains: button LEDs, encoder LEDs, and a small OLED display, but no rev LEDs, no flag LEDs, and no ITM.

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


> **Note:** Some hubs (CSWRUH, CSWRUHX) have a native 7-segment display. How this interacts with a module's display when both are present is [unverified](#hub-types).

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
| Display | OLED (ITM) — 2.7" 256x64, ITM mode + legacy mode | col03 (ITM) / col01 (legacy) |

#### Device-Specific Notes

- The OLED display operates in two modes: **ITM mode** (telemetry dashboards, pages 1–5, Device ID 3) and **legacy mode** (page 6, 7-segment-style content via col01). See [ITM Display](protocol.md#0x05--itm-display).
- Supports display ownership control via subcmd `0x18`. See [Display Ownership](protocol.md#0x18--display-ownership).

### PBMR (Podium Button Module Rally)

The PBMR is a simpler module focused on rally-style controls with button and encoder LEDs.

#### Capabilities

| Feature | Details | Protocol |
|---------|---------|----------|
| Button LEDs | 7 LEDs, RGB555 color (5-5-5 bit) | Modern (col03) |
| Encoder LEDs | 3 LEDs, RGB555 color | Modern (col03) |
| Display | OLED (Basic) — ~1", 7-seg protocol only | col01 |

#### Device-Specific Notes

- Uses **RGB555** color encoding (5 bits per channel) instead of the standard RGB565, resulting in a slightly reduced color range.
- The OLED display is only addressable via col01 7-segment commands despite being a dot-matrix display.
- Display ownership (subcmd `0x18`) is a no-op. Display conflict management must be handled by pausing host writes.
