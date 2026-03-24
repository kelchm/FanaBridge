# Steering Wheels & Hubs

Fanatec uses a single `STEERINGWHEEL_TYPE` enum for two physically distinct device categories:

- **Wheels** — Self-contained steering wheel rims with a passive quick-release connection. May have built-in buttons, LEDs, displays, and encoders. Cannot accept button modules. Their capabilities are fixed by the hardware.
- **Hubs** — Mounting platforms with an active PCB/MCU and a quick-release connection. Designed for attaching third-party or custom steering wheels. Have a USB-C interface for connecting a **button module**, which provides LEDs, displays, and additional buttons. A hub's effective capabilities are **compositional** — determined by the combination of the hub's native features plus whatever module is attached. See [Button Modules](button-modules.md) for module capabilities.

## STEERINGWHEEL_TYPE Enum

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

---

## Wheels

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

RevStripe is controlled as a single unit (index 0 only) with RGB333 color encoding (512 possible colors, SDK uses 8). See [RevStripe protocol](../protocol/led-control.md#revstripe).

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
| 9 | CSLRMCL | Small OLED | — | SDK classifies as OLED |
| 10 | CSWRFORMV2 | LED 7-seg | — | |
| 11 | CSLRMCLV1_1 | Small OLED | — | SDK classifies as OLED |
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
| 25 | CSLSWGT3 | Small OLED | — | SDK classifies as OLED; no physical LEDs |

> **Note:** Several display type assignments above (marked tentative) are inferred from SDK bitmask data and may not be fully verified against physical hardware. The SDK's `FSUtilHasWheelRimLedDisplay` and `FSUtilHasWheelRimOLED` native calls determine the classification, but we do not have complete bitmask decodes for these functions.

See [ITM Display Protocol](../protocol/display-itm.md) for page layouts and parameter details.

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

Hubs are active devices with their own PCB and microcontroller. They serve as a mounting platform and provide a USB-C interface for connecting a [button module](button-modules.md). A hub's effective capabilities are the combination of its own native features plus whatever module is attached.

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

When a button module is connected to a hub, the module's capabilities become available on that hub. The capabilities are determined entirely by the module — see [Button Modules](button-modules.md) for the full capability matrix.

For example, any compatible hub with a PBME gains: 9 RGB rev LEDs, 6 RGB flag LEDs, button LEDs, a 7-segment display, ITM display support, and encoder LEDs. The same hub with a PBMR instead gains: button LEDs, encoder LEDs, a 7-segment display, but no rev LEDs, no flag LEDs, and no ITM.

If Fanatec were to release a new button module with different capabilities, any compatible hub would gain those capabilities simply by connecting the new module — the model is compositional, not hardcoded to specific modules.
