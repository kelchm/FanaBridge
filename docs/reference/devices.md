# Fanatec Devices

The Fanatec ecosystem consists of four hardware categories:

- **Wheelbases** — The motor unit that connects to the PC via USB and provides force feedback. All HID communication flows through the wheelbase.
- **Wheels** — Self-contained steering wheel rims with a passive quick-release connection to the wheelbase. Have built-in buttons and may have LEDs, displays, and encoders. Their capabilities are fixed by the hardware.
- **Hubs** — Active mounting platforms with their own PCB/MCU and a quick-release connection to the wheelbase. Designed for attaching third-party or custom steering wheels. **Module-capable** hubs expose a USB-C interface for a button module — but being a hub does not by itself imply module support (e.g. the Wheel Hub / Sideswipe accepts no module).
- **Button modules** — Attach to hubs via USB-C. Provide LEDs, displays, and additional buttons. A hub's effective capabilities are **compositional** — determined by the hub's native features plus the attached module's capabilities.

> **Note:** Wheels and hubs share one identity code space (the byte-`0x18` wire code); modules use a separate space (byte `0x1F`). The rule that only certain hubs accept a module is physical — it is not encoded in the identity bytes themselves.

## Table of Contents

- [Device Identification](#device-identification)
  - [Identification Hierarchy](#identification-hierarchy)
  - [Feature Capabilities](#feature-capabilities)
- [Wheelbases](#wheelbases)
  - [Known Wheelbases](#known-wheelbases)
  - [USB Product IDs](#usb-product-ids)
  - [Base ITM Display](#base-itm-display)
  - [col03 Capability](#col03-capability)
  - [Base Rev LEDs](#base-rev-leds)
- [Wheels](#wheels)
  - [Known Wheels](#known-wheels)
  - [Naming Conventions](#naming-conventions)
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
  - [Module Types](#module-types)
  - [PBME (Podium Button Module Endurance)](#pbme-podium-button-module-endurance)
  - [PBMR (Podium Button Module Rally)](#pbmr-podium-button-module-rally)

---

## Device Identification

All Fanatec wheelbases share a common USB **Vendor ID**: `0x0EB7` (Endor AG).

The **Product ID** varies by wheelbase model. Within a session, the wheelbase reports the connected wheel/hub type and button module type, which determine the available features and protocol capabilities.

### Identification Hierarchy

```
USB Device (VID=0x0EB7, PID=wheelbase-specific)
  └─ Wheelbase (BaseType, byte 0x02)
      └─ Wheel or Hub (wire code, byte 0x18)
          └─ Button Module (module byte 0x1F; module-capable hubs only)
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

> **Identity codes are a finite, evolving set.** A connected wheel/hub is identified by a single-byte code in the [system report](protocol.md#0x08--system-report-identity) (byte `0x18`). That byte is an 8-bit space whose low range is nearly assigned; `0xFF` (`EXT_INFO`) is reserved for devices that report identity through extended fields instead. Treat the device lists here as the currently-known set, not a closed enumeration.

---

## Wheelbases

All Fanatec wheelbases connect to the host PC via USB and expose HID endpoints for control. The wheelbase handles force feedback and acts as the communication hub for attached steering wheels and button modules.

### Known Wheelbases

| ID (`0x02`) | Code | Display Name | col03 Support | Tuning Menu | Base ITM |
|------------:|------|-------------|---------------|-------------|----------|
| 1 | CSWV2 | ClubSport Wheel Base V2 | Yes | Yes | No |
| 2 | CSWV25 | ClubSport Wheel Base V2.5 | Yes | Yes | No |
| 3 | CSLE_1_0 | CSL Elite Wheel Base (rev 1.0) | Yes | Yes | No |
| 4 | CSLE_1_1 | CSL Elite Wheel Base (rev 1.1) | Yes | Yes | No |
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
| 99 | CSWV1 | ClubSport Wheel Base V1 | Yes | Yes | No |

> The **ID** column is the wheelbase's [system report](protocol.md#0x08--system-report-identity) **BaseType** byte (`0x02`). Names are normalized to include "Wheel Base"; hardware-revision pairs (CSLE_1_0/CSLE_1_1, PDD1/PDD1_PS4) share a marketing name and are disambiguated by Code.

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

### Base Rev LEDs

Some wheelbases have a **resident rev-LED strip on the base unit** (not on the wheel). Confirmed on the **CSL Elite Wheel Base**: 9 fixed-color rev LEDs, individually on/off.

The base strip isn't a wholly separate device — it's driven through the same legacy col01 rev-LED protocol as a connected wheel's rev LEDs. At power-on both respond to the *same* writes; the base only becomes separately controllable once the host addresses it on its dedicated base-LED command, which splits it onto its own channel for the rest of the session. See [Base Rev LEDs](protocol.md#0x13--0x14--base-rev-leds) for the commands and that split behavior.

Once split, a base with resident rev LEDs paired with a wheel that *also* has a rev indicator (e.g. a RevStripe wheel like CSLESWWRC) can be driven simultaneously — an RPM display shown on both at once.

---

## Wheels

Wheels and hubs share one identity code space (the byte-`0x18` wire code). See [Hubs](#hubs) for the hub entries.

### Known Wheels

Keyed by the [system report](protocol.md#0x08--system-report-identity) attachment wire code (byte `0x18`).

| Wire (`0x18`) | Code | Display Name |
|--------------:|------|-------------|
| `0x01` | CSSWBMW | ClubSport Steering Wheel BMW M3 GT2 |
| `0x02` | CSSWFORM | ClubSport Steering Wheel Formula Carbon |
| `0x03` | CSSWPORSCHE | ClubSport Steering Wheel Porsche 918 RSR |
| `0x07` | CSLESWP1X | CSL Elite Steering Wheel P1 for Xbox One |
| `0x08` | CSLESWP1PS4 | CSL Elite Steering Wheel P1 for PlayStation 4 |
| `0x09` | CSLESWMCL | CSL Elite Steering Wheel McLaren GT3 V1.0 |
| `0x0A` | CSSWFORMV2 | ClubSport Steering Wheel Formula V2 |
| `0x0B` | CSLESWMCLV2 | CSL Elite Steering Wheel McLaren GT3 V2 |
| `0x0E` | PSWBENT | Podium Steering Wheel Bentley GT3 |
| `0x0F` | PSWBMW | Podium Steering Wheel BMW M4 GT3 |
| `0x10` | GTSWPRO | GT DD PRO Steering Wheel |
| `0x12` | CSLESWWRC | CSL Elite Steering Wheel WRC |
| `0x13` | CSSWBMWV2 | ClubSport Steering Wheel BMW M3 GT2 V2 |
| `0x14` | CSSWRS | ClubSport Steering Wheel RS |
| `0x16` | CSSWF1ESV2 | ClubSport Steering Wheel F1 Esports V2 |
| `0x17` | PSWBMW | Podium Steering Wheel BMW M4 GT3 (hardware revision of `0x0F`) |
| `0x18` | GTSWX | GT Steering Wheel Extreme |
| `0x1B` | CSSWPVGT | CSL Elite Steering Wheel Porsche Vision GT |
| `0x1C` | CSSWFORMV3 | ClubSport Steering Wheel Formula V3 |
| `0x1D` | CSLSWGT3 | CSL Steering Wheel GT3 |

> The **Code** (e.g. CSSWBMW) is the stable identifier used throughout this reference. `0x0F` and `0x17` both decode to **PSWBMW** (`0x17` is a hardware revision). A V2.5 hardware variant of the Formula V2 (`0x0A`) exists as a sub-variant of the same Code with no separate wire code. `0x1B`/`0x1C`/`0x1D` (CSSWPVGT, CSSWFORMV3, CSLSWGT3) are newer additions. The wire-code list is the currently-known set, not closed — see the [identity-codes note](#device-identification).

Wheels are self-contained rims with fixed hardware. Their capabilities are determined entirely by their built-in components — they cannot be extended with modules.

### Naming Conventions

The same wheel or hub appears under different name conventions across Fanatec's own software and third-party tools, so a name reported elsewhere may not match the **Code** used in this reference:

| Convention | Style | Examples |
|------------|-------|----------|
| "Steering Wheel" (used here) | `CSSW…` / `CSLESW…` | `CSSWBMW`, `CSLESWP1X` |
| "Wheel Rim" (older) | `CSWR…` / `CSLR…` | `CSWRBMW`, `CSLRP1X` |
| Product / marketing name | — | `BENTLEY`, `DDRGT` |

This reference uses the "Steering Wheel"-style **Code** throughout. When matching a wheel identified by other software, watch for these variants — for example the Bentley GT3 is sometimes reported as `BENTLEY` rather than `PSWBENT`, and the GT DD PRO wheel as `DDRGT` rather than `GTSWPRO`.

### Rev LEDs

Rev LEDs are the RPM/shift indicator strip, typically 9 LEDs across the top of the wheel.

#### Individually-Addressable Rev LEDs

| Code | LED Count | Color | Protocol |
|------|-----------|-------|----------|
| CSSWBMW | 9 | Non-RGB | Legacy (col01) |
| CSSWFORM | 9 | Non-RGB | Legacy (col01) |
| CSSWPORSCHE | 9 | Non-RGB | Legacy (col01) |
| GTSWPRO | 9 | Non-RGB | Legacy (col01) |
| CSSWBMWV2 | 9 | Non-RGB | Legacy (col01) |
| CSSWRS | 9 | Non-RGB | Legacy (col01) |
| CSSWFORMV2 | 9 | **RGB** | Modern (col03) |
| CSSWF1ESV2 | 9 | **RGB** | Modern (col03) |
| PSWBENT | 9 | **RGB** | Modern (col03) |
| GTSWX | 9 | **RGB** | Modern (col03) |
| CSSWFORMV3 | 9 | **RGB** | Modern (col03) |

#### RevStripe

These wheels have a single-color LED strip instead of individually-addressable rev LEDs:

| Code | Color | Protocol |
|------|-------|----------|
| CSLESWP1X | RGB333 | Legacy (col01) |
| CSLESWP1PS4 | RGB333 | Legacy (col01) |
| CSLESWWRC | RGB333 | Legacy (col01) |

RevStripe is controlled as a single unit (index 0 only) with RGB333 color encoding. 512 values are representable, but no hardware tested to date renders more than eight — red, green, blue, cyan, magenta, yellow, white and off. See [RevStripe protocol](protocol.md#0x08--rev-led-data-bitmask--color).

#### No Rev LEDs

| Code | Notes |
|------|-------|
| CSLESWMCL | |
| CSLESWMCLV2 | |
| PSWBMW | Has RGB button LEDs but no rev LED strip |
| CSSWPVGT | No rev, flag, or button LEDs |
| CSLSWGT3 | No rev, flag, or button LEDs |

### Flag LEDs

Flag LEDs are status/warning indicators. Only these wheels have native flag LEDs:

| Code |
|------|
| CSSWFORMV2 |
| CSSWF1ESV2 |
| PSWBENT |
| GTSWX |
| CSSWFORMV3 |

### RGB LED Support

Wheels with per-LED RGB color support via the modern col03 protocol:

| Code | Rev RGB | Flag RGB |
|------|---------|----------|
| CSSWFORMV2 | Yes | Yes |
| CSSWF1ESV2 | Yes | Yes |
| PSWBENT | Yes | Yes |
| GTSWX | Yes | Yes |
| CSSWFORMV3 | Yes | Yes |

> **Note:** PSWBMW exposes an RGB **button-LED** path but has no rev-LED strip. CSSWPVGT and CSLSWGT3 have no physical LEDs.

### Button LEDs

Some wheels have built-in button backlighting:

| Code | Protocol | Notes |
|------|----------|-------|
| PSWBMW | Modern (col03) | RGB button LEDs |
| GTSWX | Modern (col03) | RGB button LEDs |

### Display Capabilities

Wheels have several distinct display technologies. The display type determines which protocol features are available.

#### Display Types

| Display Type | Technology | Protocol | ITM Capable |
|-------------|-----------|----------|-------------|
| LED 7-segment | Physical LED segments, 3 digits | col01 7-seg only | No |
| OLED (Basic) | Dot-matrix OLED, typically ~1" | col01 7-seg only | No |
| OLED (ITM) | Larger dot-matrix OLED (e.g., PBME: 2.7" 256x64) | col01 7-seg + col03 ITM | Yes |
| LCD | Graphical LCD (e.g., 3.4" 800x800) | col03 ITM | Yes |

**OLED (Basic)** displays render 7-segment-style content and are addressed with the same col01 commands as physical LED 7-segment displays. A dedicated "SmallOLED" ITM mode (Device ID 2, with its own 11-page layout) was planned but is disabled in current firmware.

**OLED (ITM)** and **LCD** displays support full telemetry dashboards via the col03 ITM protocol. They can also operate in **legacy mode** — the last ITM page (page 6 for most devices, page 5 for Bentley), which renders 7-segment-style content when no telemetry data is being sent.

#### Per-Wheel Display Matrix

| Code | Display Type | ITM Device ID | Notes |
|------|-------------|---------------|-------|
| CSSWBMW | LED 7-seg | — | |
| CSSWFORM | LED 7-seg | — | |
| CSSWPORSCHE | LED 7-seg | — | |
| CSLESWP1X | LED 7-seg | — | |
| CSLESWP1PS4 | LED 7-seg | — | |
| CSLESWWRC | LED 7-seg | — | |
| CSSWBMWV2 | LED 7-seg | — | |
| CSSWRS | LED 7-seg | — | |
| CSSWF1ESV2 | LED 7-seg | — | |
| CSLESWMCL | OLED (Basic) | — | |
| CSSWFORMV2 | OLED (Basic) | — | |
| CSLESWMCLV2 | OLED (Basic) | — | |
| GTSWPRO | OLED (Basic) | — | |
| PSWBMW | OLED (Basic) | — | |
| CSSWPVGT | OLED (Basic) | — | Round display; SmallOLED ITM mode planned but disabled in current firmware |
| CSSWFORMV3 | OLED (ITM) | TBD | ITM Device ID unconfirmed |
| CSLSWGT3 | OLED (Basic) | — | |
| GTSWX | OLED (ITM) | 3 | Dedicated GTSWX ITM pages |
| PSWBENT | LCD | 4 | 3.4" 800x800, dedicated Bentley ITM pages |

### APM (Advanced Paddle Mode)

Only wheels with a rotary encoder support the APM tuning parameter:

| Code |
|------|
| CSLESWMCL |
| CSSWFORMV2 |
| CSLESWMCLV2 |
| CSLSWGT3 |
| CSSWFORMV3 |

### Wheel Protocol Summary

| Protocol | Collection | Wheels |
|----------|-----------|--------|
| Modern (col03, RGB565) | col03 64B | CSSWFORMV2, CSSWF1ESV2, PSWBENT, GTSWX, CSSWFORMV3 |
| Legacy Non-RGB (bitmask) | col01 8B | CSSWBMW, CSSWFORM, CSSWPORSCHE, GTSWPRO, CSSWBMWV2, CSSWRS |
| RevStripe (RGB333) | col01 8B | CSLESWP1X, CSLESWP1PS4, CSLESWWRC |
| No rev LED protocol | — | CSLESWMCL, CSLESWMCLV2, PSWBMW, CSSWPVGT, CSLSWGT3 |

---

## Hubs

Hubs are active devices with their own PCB and microcontroller. They serve as a mounting platform; **module-capable** hubs also provide a USB-C interface for a [button module](#button-modules), though not all hubs accept one. A module-capable hub's effective capabilities are the combination of its own native features plus whatever module is attached.

### Hub Types

Keyed by the [system report](protocol.md#0x08--system-report-identity) wire code (byte `0x18`).

| Wire (`0x18`) | Code | Display Name | PBME | PBMR | Native 7-Seg |
|--------------:|------|-------------|:----:|:----:|--------------|
| `0x04` | CSSWUH | ClubSport Universal Hub | No | No | Yes |
| `0x06` | CSSWUHX | ClubSport Universal Hub for Xbox One | **Yes** | No | Yes |
| `0x0C` | PHUB | Podium Hub | **Yes** | **Yes** | No |
| `0x11` | CSLSWUH | CSL Universal Hub | No | No | No |
| `0x15` | CSUHV2 | ClubSport Universal Hub V2 | **Yes** | **Yes** | No |
| `0x1E` | WHEELHUB | Wheel Hub (a.k.a. Sideswipe) | No | No | No |

> **Module compatibility follows Fanatec's published lists, and differs per module.** The **PBMR** fits the Podium Hub, ClubSport Universal Hub V2, and ClubSport Universal Hub V2 for Xbox. The **PBME** fits those three **plus** the ClubSport Universal Hub for Xbox One (`0x06`). A "No" means the hub is absent from that module's list; the plain ClubSport Universal Hub (`0x04`), CSL Universal Hub (`0x11`), and Wheel Hub / Sideswipe (`0x1E`) take neither.
>
> The **"ClubSport Universal Hub V2 for Xbox"** (compatible with both modules) isn't separately pinned to a wire code here — it may share `0x15` or use an uncatalogued code.

> **Inferred:** The Wheel Hub / Sideswipe (`0x1E`) is treated as tentative — its hub classification and capabilities are partly inferred.

> **Unverified:** How the built-in 7-segment display on CSSWUH/CSSWUHX interacts with a module's display (if a module is connected simultaneously) is not yet confirmed.

> Hubs share the same identity code space as wheels; shortened Codes are used here for clarity.

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

> **Note:** Some hubs (CSSWUH, CSSWUHX) have a native 7-segment display. How this interacts with a module's display when both are present is [unverified](#hub-types).

### Module Types

| ID (`0x1F`) | Code | Display Name |
|------------:|------|-------------|
| `0x01` | PBME | Podium Button Module Endurance |
| `0x02` | PBMR | Podium Button Module Rally |

> `0x00` means no module attached.

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
- **Compatible hubs** (per Fanatec): ClubSport Universal Hub for Xbox One, ClubSport Universal Hub V2, ClubSport Universal Hub V2 for Xbox, and Podium Hub. See [Hub Types](#hub-types).

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
- **Compatible hubs** (per Fanatec): Podium Hub, ClubSport Universal Hub V2, and ClubSport Universal Hub V2 for Xbox. The older universal hubs and the CSL Universal Hub are not listed. See [Hub Types](#hub-types).
