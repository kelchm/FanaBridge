# Steering Wheels

Fanatec steering wheels (rims) attach to the wheelbase via a quick release mechanism. Each wheel type has a unique identifier and a specific set of LED, display, and protocol capabilities.

## Steering Wheel Types

| ID | Enum Name | Display Name |
|----|-----------|-------------|
| 0 | UNINITIALIZED | (not connected) |
| 1 | UNKNOWN | Unknown wheel |
| 2 | CSWRBMW | ClubSport BMW GT2 |
| 3 | CSWRFORM | ClubSport Formula |
| 4 | CSWRPORSCHE | ClubSport Porsche |
| 5 | CSWRUH | ClubSport Universal Hub |
| 6 | CSWRUHX | ClubSport Universal Hub X |
| 7 | CSLRP1X | CSL Elite P1 (Xbox) |
| 8 | CSLRP1PS4 | CSL Elite P1 (PS4) |
| 9 | CSLRMCL | CSL Elite McLaren GT3 |
| 10 | CSWRFORMV2 | ClubSport Formula V2 |
| 11 | CSLRMCLV1_1 | CSL Elite McLaren GT3 V1.1 |
| 12 | PHUB | Podium Hub |
| 13 | DDRGT | Podium Racing Wheel (DD) |
| 14 | CSLUHUB | CSL Universal Hub |
| 15 | CSLRWRC | CSL WRC |
| 16 | CSSWBMWV2 | ClubSport BMW M3 GT2 V2 |
| 17 | CSSWRS | ClubSport RS |
| 18 | CSUHV2 | ClubSport Universal Hub V2 |
| 19 | CSSWF1ESV2 | ClubSport F1 Esports V2 |
| 20 | PSWBMW | Podium BMW M4 GT3 |
| 21 | PSWBENT | Podium Bentley GT3 |
| 22 | GTSWX | GT Steering Wheel X |
| 23 | CSSWPVGT | ClubSport PVGT |
| 24 | CSSWFORMV3 | ClubSport Formula V3 |
| 25 | CSLSWGT3 | CSL Steering Wheel GT3 |
| 26 | SIDESWIPE | Sideswipe |

## Rev LED Capabilities

Rev LEDs are the RPM/shift indicator strip, typically 9 LEDs across the top of the wheel.

### Native Rev LEDs (Built-in)

These wheels have rev LEDs that work without a button module:

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

### Rev LEDs via PBME Module Only

These wheels have no native rev LEDs but gain rev LED support when paired with a Podium Button Module Endurance (PBME):

| ID | Wheel |
|----|-------|
| 0 | UNINITIALIZED |
| 5 | CSWRUH |
| 6 | CSWRUHX |
| 9 | CSLRMCL |
| 11 | CSLRMCLV1_1 |
| 12 | PHUB |
| 14 | CSLUHUB |
| 18 | CSUHV2 |
| 20 | PSWBMW |
| 23 | CSSWPVGT |
| 25 | CSLSWGT3 |

### RevStripe Wheels

These wheels have a single-color LED strip instead of individually-addressable rev LEDs:

| ID | Wheel | Color | Protocol |
|----|-------|-------|----------|
| 7 | CSLRP1X | RGB333 | Legacy (col01) |
| 8 | CSLRP1PS4 | RGB333 | Legacy (col01) |
| 15 | CSLRWRC | RGB333 | Legacy (col01) |

RevStripe is controlled as a single LED (index 0 only) with RGB333 color encoding (512 possible colors, SDK uses 8). See [RevStripe protocol](../protocol/led-control.md#revstripe).

### No Rev LEDs

| ID | Wheel |
|----|-------|
| 26 | SIDESWIPE |

## RGB LED Support

Wheels classified as RGB-capable can display per-LED colors via the modern col03 protocol:

| ID | Wheel | Rev RGB | Flag RGB |
|----|-------|---------|----------|
| 10 | CSWRFORMV2 | Yes | Yes |
| 19 | CSSWF1ESV2 | Yes | Yes |
| 21 | PSWBENT | Yes | Yes |
| 22 | GTSWX | Yes | Yes |
| 23 | CSSWPVGT | — | — |
| 24 | CSSWFORMV3 | Yes | Yes |
| 25 | CSLSWGT3 | — | — |

> **Note:** PSWBMW (Podium BMW M4 GT3) is RGB-capable but is forced to use the non-RGB `FSCmdLedRevsWheelRim` class in the native SDK. This may indicate a hardware limitation or firmware quirk.

## Flag LED Support

Flag LEDs are status/warning indicators. Not all wheels have them:

### Native Flag LEDs

| ID | Wheel |
|----|-------|
| 10 | CSWRFORMV2 |
| 19 | CSSWF1ESV2 |
| 21 | PSWBENT |
| 22 | GTSWX |
| 24 | CSSWFORMV3 |

### Flag LEDs via Module Only

| Module | Protocol |
|--------|----------|
| PBME | Modern (col03) |
| PBMR | Modern (col03) |

Wheels without native flag LEDs and without a button module have **no flag LED support**.

## ITM Display Support

See [ITM Display Protocol — Supported Devices](../protocol/display-itm.md#supported-devices).

| ID | Wheel | ITM Support |
|----|-------|-------------|
| 21 | PSWBENT | Yes (Device ID 4, dedicated Bentley pages) |
| 22 | GTSWX | Yes (Device ID 3, multi-param detection) |

The Podium Hub (12) gains ITM support when paired with PBME (Device ID 3).

## APM (Advanced Paddle Mode) Support

Only wheels with a rotary encoder support the APM tuning parameter:

| ID | Wheel |
|----|-------|
| 9 | CSLRMCL |
| 10 | CSWRFORMV2 |
| 11 | CSLRMCLV1_1 |
| 25 | CSLSWGT3 |

## Protocol Capability Summary

| Protocol | Collection | Wheels |
|----------|-----------|--------|
| Modern (col03, RGB565) | col03 64B | CSWRFORMV2, CSSWF1ESV2, PSWBENT, GTSWX, CSSWFORMV3, + any with PBME |
| Legacy Non-RGB (bitmask) | col01 8B | CSWRBMW, CSWRFORM, CSWRPORSCHE, DDRGT, CSSWBMWV2, CSSWRS |
| Legacy RGB (RGB333) | col01 8B | RGB wheels without col03 support |
| RevStripe (RGB333) | col01 8B | CSLRP1X, CSLRP1PS4, CSLRWRC |
