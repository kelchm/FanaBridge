# Fanatec HID Protocol

Fanatec wheelbases communicate with the host PC over USB HID (Human Interface Device). All commands and data flow through HID reports organized into separate **collections**, each with its own endpoint, report size, and purpose.

## Table of Contents

- [USB Identifiers](#usb-identifiers)
- [HID Collections](#hid-collections)
- [Report Framing](#report-framing)
- [Collection Routing](#collection-routing)
- [col01 Subcmd Reference](#col01-subcmd-reference)
- [LED Control](#led-control)
  - [Modern Protocol (col03)](#modern-protocol-col03)
  - [Legacy Protocol (col01)](#legacy-protocol-col01)
  - [RGB333 Color Encoding](#rgb333-color-encoding)
  - [RevStripe](#revstripe)
- [7-Segment Display](#7-segment-display)
  - [Segment Encoding](#segment-encoding)
  - [Display Ownership](#display-ownership)
- [ITM Display](#itm-display)
  - [Command Reference](#itm-command-reference)
  - [Parameter System](#parameter-system)
  - [Page Layouts](#page-layouts)
- [Tuning Menu](#tuning-menu)
  - [Tuning Parameter Structure](#tuning-parameter-structure)
  - [READ vs WRITE Layout](#read-vs-write-report-layout)
  - [Read-Modify-Write Pattern](#read-modify-write-pattern)
- [Clutch Bite Point (CBP)](#clutch-bite-point-cbp)
  - [Trigger Mechanism](#trigger-mechanism)

---

## USB Identifiers

| Field | Value |
|-------|-------|
| Vendor ID | `0x0EB7` (Endor AG / Fanatec) |
| Product ID | Varies by wheelbase (e.g., `0x0020` = ClubSport DD+) |

## HID Collections

Fanatec devices expose three HID collections:

| Collection | Report Size | Direction | Purpose |
|------------|-------------|-----------|---------|
| **col01** | 8 bytes | Bidirectional | Legacy LED control, 7-segment display, configuration commands |
| **col02** | 8 bytes | Device → Host | Input reports (buttons, encoders, axes) |
| **col03** | 64 bytes | Bidirectional | Modern LED control, ITM display, tuning menu |

### col01 — Legacy Control (8 bytes)

The original control channel, used by older wheels and for commands that predate the 64-byte protocol. Col01 carries:

- [7-segment display](#7-segment-display) updates
- [Legacy LED](#legacy-protocol-col01) reports (rev LEDs, flag LEDs, RevStripe)
- [Clutch bite point](#clutch-bite-point-cbp) commands
- Display ownership control
- Report trigger / acknowledgment commands

### col02 — Input (8 bytes)

Device-to-host input reports carrying button states, encoder positions, and axis values. Not covered in detail here — this channel is read by the OS HID driver and exposed as a standard game controller.

### col03 — Modern Control (64 bytes)

The extended control channel, used by newer devices for features requiring larger payloads:

- [Modern LED](#modern-protocol-col03) reports (per-LED RGB565 color)
- [ITM display](#itm-display) commands (page control, parameter definitions, value updates)
- [Tuning menu](#tuning-menu) read/write operations

Not all devices support col03. Older wheelbases and rims operate exclusively through col01.

## Report Framing

### col01 Reports

All col01 output reports are **8 bytes**. The first byte is the **report ID**, followed by a command header:

```
Byte:  [0]     [1]   [2]   [3]      [4]      [5]   [6]   [7]
       ReportID 0xF8  0x09  subcmd   data...
```

The report ID byte (`0x00` or `0x01`) is device-specific and assigned during initialization. The constant prefix `0xF8 0x09` identifies this as a Fanatec control command.

**Subcmd groups:**

Some subcmds use a two-level scheme where byte[3] selects a group and byte[4] selects the operation within that group:

```
[ReportID, 0xF8, 0x09, 0x01, subcmd, data...]    ← group 0x01
```

### col03 Reports

All col03 output reports are **64 bytes**, zero-padded. The first byte is always `0xFF`:

```
Byte:  [0]   [1]        [2]      [3..63]
       0xFF  cmd_class  subcmd   payload (zero-padded)
```

The **command class** (byte[1]) determines the protocol domain:

| Command Class | Protocol |
|---------------|----------|
| `0x01` | [LED control](#modern-protocol-col03) (rev, flag, button colors/intensities) |
| `0x02` | [ITM enable / analysis page](#itm-display) |
| `0x03` | [Tuning menu](#tuning-menu) (read/write/save/reset tuning parameters) |
| `0x05` | [ITM display](#itm-display) (page set, param defs, value updates, keepalive) |

## Collection Routing

The Fanatec SDK determines the HID collection based on the **first byte** of the output buffer:

| First Byte | Collection | Report Size | Notes |
|------------|-----------|-------------|-------|
| `0xFF` | col03 | 64 bytes | Full buffer written as-is |
| Any other | col01 | 8 bytes | First byte replaced with device report ID |

This means the same send path can be used for both collections — the first byte acts as the routing key. When the first byte is not `0xFF`, the SDK overwrites it with the device-specific report ID before writing to the col01 endpoint.

### col03 Path Derivation

Devices that support col03 derive the col03 endpoint path from the col01 path by:
1. Replacing `"col01"` with `"col03"` in the device path string
2. Replacing `"0000#"` with `"0002#"` in the path

Devices that do not support col03 have no col03 handle, and any `0xFF`-prefixed write will fail silently.

## col01 Subcmd Reference

All known subcmds in the `[ReportID, 0xF8, 0x09, subcmd, ...]` family:

### Direct Subcmds (byte[3])

| Subcmd | Purpose | Details |
|--------|---------|---------|
| `0x02` | Rev LED global on/off | `0x01` = on, `0x00` = off |
| `0x06` | RevStripe enable/disable | `0x00` = on, `0x01` = off (inverted) |
| `0x07` | Rev LED blink enable | `0x01` = enable blink |
| `0x08` | Rev LED data (bitmask or color) | 9-bit bitmask or RGB333 color, 2 bytes LE |
| `0x09` | RGB rev LED data (legacy) | Color data for RGB rims via col01 |
| `0x0A` | RGB rev LED data (legacy) | Color data for RGB rims via col01 |
| `0x0C` | Flag LED data (legacy) | Flag color + dirty flag |

### Group 0x01 Subcmds (byte[3]=`0x01`, operation in byte[4])

| Subcmd | Purpose | Details |
|--------|---------|---------|
| `0x02` | [7-segment display data](#7-segment-display) | `[seg1, seg2, seg3]` — 3 segment bytes |
| `0x06` | [Report trigger / ACK](#trigger-mechanism) | `[enable, SubId, 0x00]` |
| `0x17` | [Set clutch bite point](#clutch-bite-point-cbp) | `[0x01, CBP_value, 0x00]` |
| `0x18` | [Display ownership](#display-ownership) | `[mode, 0x00, 0x00]` — OLED only |

---

## LED Control

Fanatec steering wheels support several types of LEDs controlled through two distinct protocol generations. The protocol used depends on the wheel hardware — newer rims use the modern col03 protocol, while older rims require the legacy col01 protocol.

### LED Types

| Type | Purpose | Typical Count | Color |
|------|---------|---------------|-------|
| **Rev LEDs** | RPM / shift indicator strip | 9 | Per-LED RGB (modern) or on/off (legacy) |
| **Flag LEDs** | Status / warning indicators | 6 | Per-LED RGB (modern) or single color (legacy) |
| **Button LEDs** | Button backlighting | Up to 12 | Per-LED RGB + intensity |
| **RevStripe** | Single-color LED strip | 1 (entire strip) | RGB333 (8 colors via SDK, 512 via raw) |
| **Mono LEDs** | Monochrome intensity LEDs | Varies | 3-bit intensity (0–7) |

### Modern Protocol (col03)

The modern protocol uses **64-byte col03 HID reports** with per-LED RGB565 color values. This is the protocol used by newer rims such as the PBME, CSSWFORMV3, GTSWX, and others with col03 support.

#### Report Format

```
Byte:  [0]   [1]   [2]      [3..4]    [5..6]    ...
       0xFF  0x01  subcmd   LED0_RGB  LED1_RGB  ...
```

Each LED color is a **16-bit RGB565** value stored in **big-endian** byte order:

```
Bits:  RRRRR GGGGGG BBBBB
       15-11  10-5   4-0
```

A color value of `0x0000` means the LED is off.

#### Sub-commands

| Subcmd | Channel | Max LEDs | Description |
|--------|---------|----------|-------------|
| `0x00` | Rev LEDs | 30 | RPM/shift indicator colors |
| `0x01` | Flag LEDs | 30 | Status/warning indicator colors |
| `0x02` | Button colors | 12 | Button backlight RGB colors (staged) |
| `0x03` | Button intensities | 16 bytes | Per-button intensity + extra slots (staged) |

#### Simple LED Reports (Rev + Flag)

Rev and flag LEDs use a straightforward report — one RGB565 value per LED, sent in a single report:

```
Example: Set 9 rev LEDs (subcmd 0x00)
FF 01 00 [R0hi R0lo] [R1hi R1lo] ... [R8hi R8lo] 00...

Example: Set 6 flag LEDs (subcmd 0x01)
FF 01 01 [F0hi F0lo] [F1hi F1lo] ... [F5hi F5lo] 00...
```

#### Staged Button LED Reports (Color + Intensity)

Button LEDs use a **staged commit protocol**. Colors (subcmd `0x02`) and intensities (subcmd `0x03`) can be sent independently, and changes only take effect when the **commit byte** is set to `0x01`.

**Button Color Report (subcmd 0x02):**

```
Byte:  [0]   [1]   [2]   [3..4]    ... [25..26]  [27]
       0xFF  0x01  0x02  LED0_RGB  ... LED11_RGB  commit
```

- Bytes 3–26: Up to 12 RGB565 values (big-endian)
- Byte 27: Commit flag (`0x01` = apply, `0x00` = stage only)

**Button Intensity Report (subcmd 0x03):**

```
Byte:  [0]   [1]   [2]   [3]     [4]     ... [18]
       0xFF  0x01  0x03  int_0   int_1   ... commit
```

- Bytes 3–17: 15 intensity bytes (per-button + additional slots for encoder LEDs, etc.)
- Byte 18: Commit flag (`0x01` = apply, `0x00` = stage only)

The meaning of each intensity slot varies by wheel. For example, on wheels with encoder LEDs, some slots control encoder indicator brightness rather than button backlights.

**Staging Behavior:**

When both color and intensity need to change:
1. Send the color report with commit = `0x00` (stage)
2. Send the intensity report with commit = `0x01` (commit both)

When only one has changed, send that report alone with commit = `0x01`.

### Legacy Protocol (col01)

The legacy protocol uses **8-byte col01 HID reports** with the `0xF8 0x09` command prefix. This protocol is used by older rims that do not support col03, including non-RGB rims like the CSSWBMWV2 and rims with RevStripe.

#### Rev LED Control

Legacy rev LEDs are controlled via a **global on/off**, a **color setting**, and a **per-LED bitmask**.

**Global On/Off (subcmd 0x02):**

```
[ReportID, 0xF8, 0x09, 0x02, enable, 0x00, 0x00, 0x00]

  enable: 0x01 = rev LEDs on
          0x00 = rev LEDs off
```

**LED Bitmask / Color Data (subcmd 0x08):**

```
[ReportID, 0xF8, 0x09, 0x08, data_lo, data_hi, 0x00, 0x00]
```

For **non-RGB rims** (e.g., CSSWBMWV2), this is a 9-bit bitmask where each bit controls one LED (bit 0 = LED 0, etc.):

```
Example: LEDs 0, 1, 2 on, rest off
  data_lo = 0x07, data_hi = 0x00   (bitmask: 0b000000111)

Example: All 9 LEDs on
  data_lo = 0xFF, data_hi = 0x01   (bitmask: 0b111111111)
```

For **RevStripe rims** (CSLRP1X, CSLRP1PS4, CSLRWRC), this is an RGB333 color value:

```
Example: Red
  data_lo = 0x00, data_hi = 0x38

Example: Green
  data_lo = 0x01, data_hi = 0xC0
```

**Blink Enable (subcmd 0x07):**

```
[ReportID, 0xF8, 0x09, 0x07, 0x01, 0x00, 0x00, 0x00]
```

### RGB333 Color Encoding

The legacy protocol uses a 9-bit **RGB333** color encoding packed into 2 bytes:

```
byte[5] (data_hi):  [ G1 G0 | R2 R1 R0 | B2 B1 B0 ]   (GG_RRR_BBB)
byte[4] (data_lo):  [  0  0   0  0  0   0  0  G2  ]   (.......G)
```

Each channel has 3 bits (0–7), yielding 512 possible colors:

| Color | R | G | B | data_lo (byte[4]) | data_hi (byte[5]) |
|-------|---|---|---|-------------------|-------------------|
| Off | 0 | 0 | 0 | `0x00` | `0x00` |
| Red | 7 | 0 | 0 | `0x00` | `0x38` |
| Green | 0 | 7 | 0 | `0x01` | `0xC0` |
| Blue | 0 | 0 | 7 | `0x00` | `0x07` |
| Yellow | 7 | 7 | 0 | `0x01` | `0xF8` |
| Magenta | 7 | 0 | 7 | `0x00` | `0x3F` |
| Cyan | 0 | 7 | 7 | `0x01` | `0xC7` |
| White | 7 | 7 | 7 | `0x01` | `0xFF` |

> **Note:** The Fanatec SDK only uses 8 discrete colors (each channel fully on or fully off). The hardware encoding supports 3 bits per channel, so intermediate values (e.g., R=4, G=2, B=0) may work but are not officially exercised.

#### Flag LED Control (Legacy)

Legacy flag LEDs use subcmd `0x0C`:

```
[ReportID, 0xF8, 0x09, 0x0C, flag_color, dirty_flag, 0x00, 0x00]
```

Only a subset of wheels have flag LEDs. See the [devices reference](devices.md#flag-leds) for the support matrix.

### RevStripe

RevStripe is a **single-color LED strip** found on three specific rims (CSLRP1X, CSLRP1PS4, CSLRWRC). Unlike individual rev LEDs, the entire strip is controlled as one unit.

**Enable/Disable (subcmd 0x06):**

RevStripe uses **inverted semantics** — `0x00` means ON:

```
Enable:  [ReportID, 0xF8, 0x09, 0x06, 0x00, 0x00, 0x00, 0x00]
Disable: [ReportID, 0xF8, 0x09, 0x06, 0x01, 0x00, 0x00, 0x00]
```

**Color Control:**

RevStripe color is set via subcmd `0x08` with an RGB333 value (same encoding as rev LED color). See the [RGB333 section](#rgb333-color-encoding) above.

**Typical Sequence:**

```
1. Enable RevStripe:  [RID, F8, 09, 06, 00, 00, 00, 00]
2. Global LEDs on:    [RID, F8, 09, 02, 01, 00, 00, 00]
3. Set color (red):   [RID, F8, 09, 08, 00, 38, 00, 00]

To turn off:
4. Set color (off):   [RID, F8, 09, 08, 00, 00, 00, 00]
5. Global LEDs off:   [RID, F8, 09, 02, 00, 00, 00, 00]
```

### LED Internal State Model

The firmware maintains an internal state array for each LED. Each entry tracks:

| Field | Size | Description |
|-------|------|-------------|
| On/off state | 1 byte | `0` = off, `1` = on |
| Dirty flag | 1 byte | Set when state changes, cleared after report sent |
| Color dirty | 1 byte | Set when color changes |
| R channel | 1 byte | Red (0–31 for 5-bit, 0–7 for 3-bit) |
| G channel | 1 byte | Green (0–63 for 6-bit, 0–7 for 3-bit) |
| B channel | 1 byte | Blue (0–31 for 5-bit, 0–7 for 3-bit) |

Individual LED operations (`SetOn`, `SetOff`, `SetColor`) modify this array and mark entries as dirty. The `SubmitToDevice` operation then sends only the reports needed for dirty entries — global on/off, color, and/or bitmask — in a single batch.

### Protocol Selection by Wheel Type

| Capability | Protocol | Collection | Color Depth |
|------------|----------|------------|-------------|
| RGB LED + col03 support | Modern | col03 (64B) | RGB565 (65K colors) |
| RGB LED + no col03 | Legacy RGB | col01 (8B) | RGB333 via subcmds 0x09/0x0A |
| Non-RGB LED | Legacy bitmask | col01 (8B) | On/off only + global RGB333 |
| RevStripe | Legacy color | col01 (8B) | RGB333 (512 colors) |

See the [devices reference](devices.md#wheel-protocol-summary) for the per-wheel capability matrix.

---

## 7-Segment Display

This protocol controls the 3-digit display found on many Fanatec wheels, hubs, and button modules. It uses **8-byte col01 HID reports** and is typically used to show gear, speed, or short text strings.

The same protocol is used regardless of the underlying display hardware — physical LED 7-segment displays and small OLED displays are both addressed with identical commands. On devices with a larger ITM-capable OLED (e.g., PBME), this protocol drives the **legacy mode** (the last ITM page), which renders 7-segment-style content. See [Display Capabilities](devices.md#display-capabilities) for per-device display types.

### Display Command

```
[ReportID, 0xF8, 0x09, 0x01, 0x02, <seg1>, <seg2>, <seg3>]
                              │     │
                              │     └─ 7-segment display data subcmd
                              └─────── Group 0x01
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | Report ID | Device-specific (typically `0x00` or `0x01`) |
| 1 | `0xF8` | Command prefix |
| 2 | `0x09` | Command prefix |
| 3 | `0x01` | Subcmd group |
| 4 | `0x02` | 7-segment data subcmd |
| 5 | seg1 | Left digit segment byte |
| 6 | seg2 | Center digit segment byte |
| 7 | seg3 | Right digit segment byte |

Each segment byte is a bitmask controlling the 7 segments plus a decimal point.

### Segment Encoding

Each digit is encoded as a single byte where each bit controls one segment:

```
   seg 0
  ───────
│       │
5       1
│       │
  ───────  seg 6
│       │
4       2
│       │
  ───────  • seg 7 (dot/decimal point)
   seg 3
```

| Bit | Segment | Position |
|-----|---------|----------|
| 0 | Top | Horizontal top bar |
| 1 | Upper-right | Vertical right upper |
| 2 | Lower-right | Vertical right lower |
| 3 | Bottom | Horizontal bottom bar |
| 4 | Lower-left | Vertical left lower |
| 5 | Upper-left | Vertical left upper |
| 6 | Middle | Horizontal middle bar |
| 7 | Dot | Decimal point |

#### Digit Encoding Table

| Character | Hex | Binary | Segments Active |
|-----------|-----|--------|-----------------|
| 0 | `0x3F` | `0111111` | 0,1,2,3,4,5 |
| 1 | `0x06` | `0000110` | 1,2 |
| 2 | `0x5B` | `1011011` | 0,1,3,4,6 |
| 3 | `0x4F` | `1001111` | 0,1,2,3,6 |
| 4 | `0x66` | `1100110` | 1,2,5,6 |
| 5 | `0x6D` | `1101101` | 0,2,3,5,6 |
| 6 | `0x7D` | `1111101` | 0,2,3,4,5,6 |
| 7 | `0x07` | `0000111` | 0,1,2 |
| 8 | `0x7F` | `1111111` | 0,1,2,3,4,5,6 |
| 9 | `0x6F` | `1101111` | 0,1,2,3,5,6 |

#### Letter Encoding Table

| Char | Hex | Char | Hex | Char | Hex |
|------|-----|------|-----|------|-----|
| A | `0x77` | J | `0x0E` | S | `0x6D` |
| B | `0x7C` | K | `0x75` | T | `0x78` |
| C | `0x58` | L | `0x38` | U | `0x3E` |
| D | `0x5E` | M | `0x37` | V | `0x18` |
| E | `0x79` | N | `0x54` | W | `0x7E` |
| F | `0x71` | O | `0x5C` | X | `0x76` |
| G | `0x3D` | P | `0x73` | Y | `0x6E` |
| H | `0x76` | Q | `0x67` | Z | `0x5B` |
| I | `0x06` | R | `0x50` | | |

#### Symbol Encoding

| Symbol | Hex | Description |
|--------|-----|-------------|
| Blank | `0x00` | All segments off |
| `-` (Dash) | `0x40` | Middle segment only |
| `_` (Underscore) | `0x08` | Bottom segment only |
| `.` (Dot) | `0x80` | Decimal point only |

#### Decimal Point

The decimal point (bit 7, `0x80`) can be combined with any character via bitwise OR:

```
Digit 3 with dot: 0x4F | 0x80 = 0xCF
Letter A with dot: 0x77 | 0x80 = 0xF7
```

### 7-Segment Examples

**Display Gear "5":**
```
[RID, F8, 09, 01, 02, 00, 6D, 00]
                        │   │   │
                        │   │   └─ Right: blank
                        │   └───── Center: "5" (0x6D)
                        └───────── Left: blank
```

**Display Speed "142":**
```
[RID, F8, 09, 01, 02, 06, 66, 5B]
                        │   │   │
                        │   │   └─ Right: "2" (0x5B)
                        │   └───── Center: "4" (0x66)
                        └───────── Left: "1" (0x06)
```

**Display Text "Hi":**
```
[RID, F8, 09, 01, 02, 76, 06, 00]
                        │   │   │
                        │   │   └─ Right: blank
                        │   └───── Center: "I" (0x06)
                        └───────── Left: "H" (0x76)
```

**Clear Display:**
```
[RID, F8, 09, 01, 02, 00, 00, 00]
```

### Display Ownership

On certain OLED-equipped devices (currently only the PBME), the host can explicitly take or release control of the display. The SDK checks an internal `IsSevenSegmentOLED` flag before sending this command:

```
Host takes control:   [RID, F8, 09, 01, 18, 02, 00, 00]
Release to firmware:  [RID, F8, 09, 01, 18, 01, 00, 00]
```

| Byte[5] | Meaning |
|---------|---------|
| `0x02` | Host control — firmware stops updating the display |
| `0x01` | Firmware control — firmware resumes display ownership |

This is important during operations where the firmware needs to show its own content (e.g., [CBP adjustment](#clutch-bite-point-cbp), tuning menu navigation). The host should release control, wait for the operation to complete, then reclaim control.

For devices where `IsSevenSegmentOLED` is false (including LED 7-segment displays and the PBMR's small OLED), this command is a no-op. Display conflict management on these devices must be handled by the host software (e.g., by pausing display writes during firmware operations).

### Display Interaction with Other Protocols

The 7-segment display can be preempted by:

- **Tuning menu navigation** — the firmware shows tuning parameter values when the user scrolls
- **CBP adjustment** — the firmware displays the current CBP value
- **Firmware boot/init** — the display shows initialization state

During these periods, host display writes will conflict with firmware output, causing visible flickering. See the [CBP protocol](#display-interaction) for mitigation strategies.

---

## ITM Display

The ITM (In-Tuning-Menu) protocol controls OLED and LCD displays found on select Fanatec steering wheels and button modules. It provides multi-page telemetry dashboards with parameters like speed, gear, lap times, tyre temperatures, and more.

All ITM commands use **col03 64-byte HID reports**.

### Supported Devices

ITM display support depends on the wheelbase, steering wheel, and button module combination:

| ITM Device | Detection | Device ID | Notes |
|------------|-----------|-----------|-------|
| **Base** | Wheelbase is PDD1, PDD1 (PS4), or PDD2 | 1 | Wheelbase's own display |
| **BME** | Button Module Endurance connected | 3 | PBME's large OLED |
| **Bentley** | Bentley GT3 steering wheel | 4 | Bentley wheel's built-in display |
| **GTSWX** | GT Steering Wheel X | 3 | GTSWX's built-in display |

> **Note:** BME and GTSWX share Device ID 3 on the wire. They are mutually exclusive — a setup will have one or the other, never both.

#### Devices Without ITM Support

- **PBMR** (Podium Button Module Rally) — No ITM support. Only supports button LEDs and 7-segment display.
- **DD10/DD20** — Pass the initial ITM gate but have no base display. Require a hub with PBME attached, or a Bentley/GTSWX wheel for ITM.
- **CSDD / CSDDPlus / GTDDPRO / CSLDD** — Not in the official base ITM detection, but raw HID ITM commands work (bypassing the SDK check).

### ITM Command Reference

#### ITM Enable

Activates ITM mode on the wheel display. Uses command class `0x02` (not `0x05`):

```
FF 02 02 00 [00 x60]
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | `0xFF` | Report ID |
| 1 | `0x02` | Command class |
| 2 | `0x02` | Sub-command |
| 3 | `0x00` | Page (0 = default/reset) |

**Important:**
- Enable **resets the slot table** — ParamDefs must be re-sent after each Enable.
- Do not call Enable in a tight loop — rapid repeated calls can crash the PBME firmware.
- This is also used as `PageAnalysisSet(page)` — byte[3] can be 0–6 to set the analysis page.

#### Keepalive

Continuous keepalive packet. Must be sent every ~100ms to keep the ITM display alive:

```
FF 05 04 02 0B [00 x59]
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | `0xFF` | Report ID |
| 1 | `0x05` | Command class |
| 2 | `0x04` | Sub-command (config) |
| 3 | `0x02` | Config type |
| 4 | `0x0B` | Config value |

#### PageSet

Selects which ITM page is active on a specific display device:

```
FF 05 04 <deviceId> <page> [00 x59]
```

| Byte | Value | Description |
|------|-------|-------------|
| 3 | Device ID | Target display (1=Base, 3=BME/GTSWX, 4=Bentley) |
| 4 | Page number | Page to display (1–6, device-dependent) |

#### ParamDefs (Parameter Definitions)

Defines the display slot layout — tells the firmware what parameters will be displayed and in which positions:

```
FF 05 03 <entries...> [00-padded to 64 bytes]
```

Each entry:

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 | Marker | Always `0x03` |
| 1 | 1 | Slot ID | Display layout identifier (e.g., `0x82`, `0x85`, `0x88`) |
| 2 | 1 | Position Lo | Low byte of position (typically `0x00`) |
| 3 | 1 | Position Hi | High byte of position (typically `0x00`) |
| 4 | 1 | Suffix Length | Number of suffix bytes following (0 = no suffix) |
| 5+ | N | Suffix Bytes | ASCII text appended after values (e.g., `2F 30` = "/0") |

**Example — two slots with suffix "/0":**
```
03 82 00 00 02 2F 30   ← slot 0x82, suffix "/0" (2 bytes: '/', '0')
03 83 00 00 02 2F 30   ← slot 0x83, suffix "/0"
```

The suffix system corresponds to the unit display feature in the SDK — the "/0" suffix likely represents the total denominator (e.g., "Lap 5 / 20").

#### ValueUpdate

Sends actual telemetry values for display. Each entry contains a handle, parameter ID, and value:

```
FF 05 01 <entries...> [00-padded to 64 bytes]
```

Each entry:

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 | Marker | Always `0x01` |
| 1 | 1 | Handle | Parameter handle (assigned during ParamDefs) |
| 2–3 | 2 | Param ID | Parameter ID (little-endian) |
| 4 | 1 | Size | Value size in bytes (1, 2, or 4) |
| 5+ | N | Value | Parameter value (little-endian, size from above) |

### Parameter System

#### Parameter IDs

The firmware recognizes a vocabulary of parameter IDs. Only a subset is confirmed to render correctly on current firmware — the rest may display no label, show unexpected formatting, or be silently ignored.

**Vehicle Telemetry (1–84):**

| ID | Name | Size | Type | Notes |
|----|------|------|------|-------|
| 1 | SPEED | 2 | Int16 LE | Present on all pages as header |
| 2 | RPM | 4 | Int32 | |
| 3 | RPM_MAX | 4 | Int32 | |
| 4 | GEAR | 1 | Uint8 | Present on all pages as header |
| 5 | FUEL | 4 | Float32 LE | Supports total (FUEL_MAX) |
| 6 | FUEL_MAX | 4 | Float32 LE | |
| 7 | FUEL_PER_LAP | 4 | Float32 LE | |
| 9 | ERS_LEVEL | 4 | Int32 LE | Percentage |
| 14 | DRS_ZONE | 1 | Uint8 | 0 or 1 |
| 15 | DRS_ACTIVE | 1 | Uint8 | 0 or 1 |
| 18 | ABS_SETTING | 1 | Uint8 | |
| 20 | TC_SETTING | 1 | Uint8 | |
| 25 | BRAKE_BIAS | 4 | Float32 LE | Must be 4 bytes — values >= 127 can crash PBME firmware |
| 26 | ENGINE_MAPPING | 1 | Uint8 | |
| 33 | OIL_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 42 | TYRE_FL_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 45 | TYRE_FR_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 48 | TYRE_RL_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 51 | TYRE_RR_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |

**Race / Timing (501–536):**

| ID | Name | Size | Type | Notes |
|----|------|------|------|-------|
| 501 | POSITION | 1 | Uint8 | Supports total (POSITION_TOTAL) |
| 505 | LAP | 1 | Uint8 | Supports total (LAP_TOTAL) |
| 509 | LAP_TIME | 4 | Float32 LE | Current lap, seconds |
| 510 | LAST_LAP_TIME | 4 | Float32 LE | |
| 511 | BEST_LAP_TIME | 4 | Float32 LE | |
| 516 | DELTA_OWN_BEST | 4 | Float32 LE | Seconds, +/- |
| 519 | CAR_AHEAD | 4 | Float32 LE | Gap in seconds |
| 520 | CAR_BEHIND | 4 | Float32 LE | Gap in seconds |

**Additional Defined Parameters:**

The full parameter vocabulary includes 120+ IDs covering tyre pressures, brake temps, G-forces, pedal positions, flags, system metrics (CPU/GPU), and more. These are defined in the firmware's shared vocabulary but are **not confirmed to render** on all display types. The complete `FS_ITM_PARAM_ID` enum ranges:

- 0–84: Vehicle telemetry
- 501–536: Race/timing data and flags
- 1001–1008: System metrics (CPU load, GPU temp, FPS, etc.)
- 65535 (`0xFFFF`): UNSUBSCRIBE sentinel

#### Unit System

Parameters can have associated display units:

| Value | Unit | Category |
|-------|------|----------|
| 0 | DEFAULT | No suffix |
| 1 | SPEED_KPH | Speed |
| 2 | SPEED_MPH | Speed |
| 4 | VOLUME_LITER | Volume |
| 5 | VOLUME_GALLON | Volume |
| 7 | TIME_SEC | Time |
| 8 | TEMP_C | Temperature |
| 9 | TEMP_F | Temperature |
| 17 | TOTAL_POSITION | Total companion |
| 18 | TOTAL_LAP | Total companion |
| 19 | TOTAL_CLASS | Total companion |
| 22 | OTHERS_PERCENT | Percentage |

"Total" units enable `value / total` display (e.g., "Lap 5 / 20"). The SDK submits units via `ItmUnitSet()` on every tick; the raw HID equivalent maps to the suffix mechanism in ParamDefs.

### Page Layouts

SPEED and GEAR appear on **every page** as persistent header fields.

#### Base / BME Pages (1–6)

Base and BME use identical page layouts. Page 6 is the legacy/default fallback.

**Page 1 — Lap Info:**

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 505 | LAP | 1 | Uint8 |
| 501 | POSITION | 1 | Uint8 |
| 509 | LAP_TIME | 4 | Float32 LE |
| 510 | LAST_LAP_TIME | 4 | Float32 LE |

Detection signature: LAP (505) present in subscription.

**Page 2 — Fuel / ERS / DRS:**

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 5 | FUEL | 4 | Float32 LE |
| 9 | ERS_LEVEL | 4 | Int32 LE |
| 14 | DRS_ZONE | 1 | Uint8 |
| 15 | DRS_ACTIVE | 1 | Uint8 |
| 516 | DELTA_OWN_BEST | 4 | Float32 LE |

Detection signature: ERS_LEVEL (9).

**Page 3 — Car Settings:**

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 20 | TC_SETTING | 1 | Uint8 |
| 18 | ABS_SETTING | 1 | Uint8 |
| 25 | BRAKE_BIAS | 4 | Float32 LE |
| 26 | ENGINE_MAPPING | 1 | Uint8 |
| 33 | OIL_TEMP | 1 | Uint8 |

Detection signature: OIL_TEMP (33).

> **Warning:** BRAKE_BIAS values >= 127 have been observed to crash PBME firmware. Rapid value cycling on this page can also cause firmware lockups.

**Page 4 — Lap Times:**

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 510 | LAST_LAP_TIME | 4 | Float32 LE |
| 511 | BEST_LAP_TIME | 4 | Float32 LE |
| 519 | CAR_AHEAD | 4 | Float32 LE |
| 520 | CAR_BEHIND | 4 | Float32 LE |

Detection signature: CAR_AHEAD (519).

**Page 5 — Tyre Temps:**

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 42 | TYRE_FL_C_TEMP | 1 | Uint8 |
| 45 | TYRE_FR_C_TEMP | 1 | Uint8 |
| 48 | TYRE_RL_C_TEMP | 1 | Uint8 |
| 51 | TYRE_RR_C_TEMP | 1 | Uint8 |

Detection signature: TYRE_FL_C_TEMP (42).

**Page 6 — Legacy / Default:**

No telemetry parameters. Fallback page when ITM is inactive.

#### Bentley Pages (1–5)

Bentley uses the same parameters but with **no Car Settings page** and only 5 pages total:

| Page | Content | Detection |
|------|---------|-----------|
| 1 | Lap Info | LAP (505) |
| 2 | Fuel / ERS / DRS | ERS_LEVEL (9) |
| 3 | Lap Times | CAR_AHEAD (519) |
| 4 | Tyre Temps | TYRE_FL_C_TEMP (42) |
| 5 | Legacy / Default | — |

#### GTSWX Pages (1–6)

GTSWX uses the same page content as Base/BME but with **multi-parameter detection signatures** — all params in the group must be present before the page is matched:

| Page | Content | Detection (ALL required) |
|------|---------|-------------------------|
| 1 | Lap Info | LAP + POSITION + LAP_TIME + LAST_LAP_TIME |
| 2 | Fuel / ERS / DRS | FUEL + ERS_LEVEL + DRS_ZONE + DRS_ACTIVE + DELTA_OWN_BEST |
| 3 | Car Settings | TC_SETTING + ABS_SETTING + ENGINE_MAPPING + OIL_TEMP + BRAKE_BIAS |
| 4 | Lap Times (compact) | LAST_LAP_TIME + BEST_LAP_TIME + CAR_AHEAD (no CAR_BEHIND) |
| 5 | Tyre Temps | Any one of: TYRE_FL/FR/RL_C_TEMP |
| 6 | Legacy / Default | — |

#### Slot & Handle Mapping (Raw HID)

When using raw HID (bypassing the SDK), displays require explicit slot configuration via ParamDefs:

| Page(s) | Slot IDs | Handle Range | Suffix |
|---------|----------|--------------|--------|
| 1, 5 | 0x82, 0x83, 0x84, 0x85 | 0–5 | "/0" (Page 1 only) |
| 2, 4 | 0x88 | 6–12 | "/0" (Page 2 only) |
| 3 | 0x85 | 0–5 + 13 | none |

Maximum subscribed parameters per device: **16**.

### Timing & Rate Limiting

#### Parameter Update Intervals

Not all parameters need to be sent at full rate. Recommended minimum intervals:

| Category | Delay (ms) | Parameters |
|----------|-----------|------------|
| Real-time | 0 | SPEED, GEAR, POSITION, LAP, LAST_LAP_TIME, BEST_LAP_TIME, BRAKE_BIAS, FUEL, DRS, ABS, TC |
| Near real-time | 30 | LAP_TIME |
| Moderate | 40–50 | RPM_MAX, TYRE temps |
| Low-frequency | 100 | ENGINE_MAPPING, ERS, others |

#### Page Changes

Page change commands should be spaced at least 100ms apart. The firmware needs time to reconfigure its internal display state between pages.

### Control Model: SDK vs Raw HID

There are two fundamentally different approaches to controlling the ITM display:

#### SDK Approach (Firmware-Driven)

The firmware maintains a subscription table of up to 16 parameter slots. The host queries which parameters the firmware expects for the current page, then populates them:

1. Firmware owns the page layout
2. Host calls `ItmSubscribedParamsGet()` to learn what params are needed
3. Host sends values for subscribed params only
4. Page content is fixed by firmware

#### Raw HID Approach (Host-Driven)

The host bypasses the SDK and directly defines the display layout via ParamDefs:

1. Host sends PageSet — tells firmware which page
2. Host sends ParamDefs — tells firmware the slot layout
3. Host sends ValueUpdate — sends actual data
4. Host can potentially choose which params appear on each page

The raw HID approach gives more flexibility but requires knowing the page layouts in advance and carries the risk that untested parameter IDs may not render correctly.

### Automatic Page Changes (Alerts)

The official software supports automatic page switching based on telemetry events:

#### Value-Change Triggers

| Trigger | Target Page |
|---------|-------------|
| Lap number or Position changed | Page 1 (Lap Info) |
| DRS zone or DRS active changed | Page 2 (Fuel/ERS/DRS) |
| TC, ABS, EngineMap, or BrakeBias changed | Page 3 (Car Settings) |
| Best lap time changed | Page 4 (Lap Times) |

#### Threshold Triggers

| Trigger | Target Page |
|---------|-------------|
| Fuel below threshold | Page 2 |
| ERS below threshold | Page 2 |
| Oil temp above threshold | Page 3 |
| Any tyre temp out of range | Page 5 |

#### Favorite Page

Each device has a configurable favorite page with a display duration (default 10 seconds, range 3–60 seconds). After a trigger-caused page change, the display reverts to the favorite page after the duration expires.

---

## Tuning Menu

The tuning menu protocol controls all wheelbase settings (SEN, FF, damper, spring, etc.) via **col03 64-byte HID reports** using command class `0x03`.

### Command Structure

All tuning menu commands share this frame:

```
Byte:  [0]   [1]   [2]      [3]      [4..63]
       0xFF  0x03  subcmd   devId    payload (zero-padded to 64 bytes)
```

#### Sub-commands

| Subcmd | Name | Direction | Description |
|--------|------|-----------|-------------|
| `0x00` | WRITE | Host → Device | Set tuning parameters (fire-and-forget) |
| `0x01` | SELECT SETUP | Host → Device | Switch active setup index (0–4) |
| `0x02` | READ | Host → Device | Request current state (triggers response) |
| `0x03` | SAVE | Host → Device | Persist current state to device flash |
| `0x04` | RESET | Host → Device | Restore factory defaults |
| `0x06` | TOGGLE | Host → Device | Toggle standard/simplified tuning mode |

A READ command triggers a response on the col03 IN endpoint with the current tuning state.

### Tuning Parameter Structure

The `WHEEL_TUNING_MENU_DATA` structure is used in both READ responses and WRITE commands. It contains all tuning parameters at fixed offsets:

| Offset | Field | Type | Range | Description |
|--------|-------|------|-------|-------------|
| 0 | UserSetupIndex | byte | 0–4 | Active setup slot |
| 1 | SEN | byte | 0–255 | Steering Sensitivity |
| 2 | FF | byte | 0–255 | Force Feedback strength |
| 3 | SHO | byte | 0–255 | Shock / vibration intensity |
| 4 | BLI | byte | 0–255 | Brake Linearity |
| 5 | LIN | byte | 0–255 | Linearity (aliased as FFS in some SDK versions) |
| 6 | DEA | byte | 0–255 | Dead Zone |
| 7 | DRI | **sbyte** | -128–127 | Drift Mode (**signed** — the only signed field) |
| 8 | FOR | byte | 0–255 | Force |
| 9 | SPR | byte | 0–255 | Spring |
| 10 | DPR | byte | 0–255 | Damper |
| 11 | NDP | byte | 0–255 | Natural Damper |
| 12 | NFR | byte | 0–255 | Natural Friction |
| 13 | BRF | byte | 0–255 | Brake Force |
| 14 | BRG | byte | 0–255 | Brake Gain |
| 15 | FEI | byte | 0–255 | Force Effect Intensity |
| 16 | MPS | byte | 0–255 | Max Power Supply / Motor Protection |
| 17 | APM | byte | 0–255 | Advanced Paddle Mode (rotary wheels only) |
| 18 | INT | byte | 0–255 | Interactivity |
| 19 | NIN | byte | 0–255 | Natural Inertia |
| 20 | FUL | byte | 0–255 | Full Lock (steering angle) |
| 21 | BIL | byte | 0–255 | Bilateral / Balance |
| 22 | ROT | byte | 0–255 | Rotation |
| 23–63 | Reserved | byte | — | Always zero |

**Notes:**

- **DRI (offset 7)** is the only signed byte in the structure.
- **LIN vs FFS**: Two SDK struct variants exist. `WHEEL_TUNING_MENU_DATA` calls offset 5 `LIN`; `FS_WHEEL_TUNING_MENU_DATA` calls it `FFS`. Same byte position, same semantics.
- **APM (offset 17)**: Only populated on wheels with a rotary encoder — CSL Elite McLaren GT3, CSL Steering Wheel GT3, ClubSport Formula V2, and their revisions. Zero on all other wheels.
- Bytes 23–63 are reserved and always zero.

### READ vs WRITE Report Layout

**Critical:** READ responses and WRITE commands place the tuning data at **different byte offsets** within the 64-byte HID report.

#### READ Command (Host → Device)

```
[FF 03 02 00 00 00 ... 00]     (64 bytes)
 │   │  │
 │   │  └─ subcmd = READ (0x02)
 │   └──── cmd class = 0x03
 └──────── report ID = 0xFF
```

#### READ Response (Device → Host)

```
[FF 03 devId data[0] data[1] ... data[60]]     (64 bytes)
 │   │   │     │
 │   │   │     └── Tuning data starts at byte[3]
 │   │   └──────── device ID (e.g., 0x02)
 │   └──────────── cmd class = 0x03
 └──────────────── report ID = 0xFF
```

In the READ response, `UserSetupIndex` is at **byte[3]**, `SEN` at byte[4], `FF` at byte[5], etc.

#### WRITE Command (Host → Device)

```
[FF 03 00 devId data[0] data[1] ... data[59]]     (64 bytes)
 │   │  │   │     │
 │   │  │   │     └── Tuning data starts at byte[4]
 │   │  │   └──────── device ID
 │   │  └──────────── subcmd = WRITE (0x00)
 │   └─────────────── cmd class = 0x03
 └─────────────────── report ID = 0xFF
```

In the WRITE command, `UserSetupIndex` is at **byte[4]**, `SEN` at byte[5], etc. — shifted by 1 byte relative to the READ response due to the subcmd byte.

#### Conversion: READ → WRITE Buffer

```
writeBuf[0]     = 0xFF
writeBuf[1]     = 0x03
writeBuf[2]     = 0x00              // subcmd = WRITE
writeBuf[3]     = readBuf[2]        // device ID
writeBuf[4..63] = readBuf[3..62]    // tuning data (shifted by 1)
```

### Read-Modify-Write Pattern

The device **rejects WRITE commands** that don't reflect the current state. Always follow the read-modify-write pattern:

```
Step 1: Send READ
  → [FF 03 02 00 ... 00]

Step 2: Receive response
  ← [FF 03 02 XX YY ZZ ...]    (devId=0x02, data follows)

Step 3: Build WRITE buffer
  writeBuf = [FF 03 00 02 XX YY ZZ ...]
  (copy readBuf[2..62] → writeBuf[3..63])

Step 4: Modify desired fields
  e.g., writeBuf[4 + field_offset] = newValue

Step 5: Send WRITE
  → [FF 03 00 02 XX YY' ZZ ...]
```

#### Example: Changing Force Feedback Strength

FF is at struct offset 2. In the WRITE buffer: `writeBuf[4 + 2] = writeBuf[6]`:

```
writeBuf[6] = 80;    // Set FF to 80
```

#### Example: Switching Setup Slot

UserSetupIndex is at struct offset 0. In the WRITE buffer: `writeBuf[4]`:

```
writeBuf[4] = 2;     // Switch to setup slot 2
```

The dedicated SELECT SETUP subcmd (`0x01`) can also be used for this.

### Post-WRITE Behavior

The native SDK's tuning menu WRITE (`FSTuningMenu::PrivateDataSet`) sends a single col03 HID report and returns immediately — **no acknowledgment burst or trigger sequence is sent after a WRITE**.

The [report trigger mechanism](#trigger-mechanism) (subcmd `0x06`) is used only for CBP operations, not for tuning menu writes. The tuning menu has its own `DataReportTrigger` method (subcmd `0x06` within the col03 `0x03` command class), but this is part of the **READ** path — it requests the device to send back its current tuning state.

> **Note:** FanaBridge currently sends a burst of 4 ON/OFF trigger pairs after tuning writes. This was reverse-engineered from observed behavior and does not match the native SDK, which sends zero trigger pairs after a WRITE. The FanaBridge implementation should be considered experimental.

### WRITE Report Byte Map (Quick Reference)

| HID Byte | Content | Struct Offset | Field |
|----------|---------|---------------|-------|
| 0 | `0xFF` (report ID) | — | — |
| 1 | `0x03` (cmd class) | — | — |
| 2 | `0x00` (WRITE subcmd) | — | — |
| 3 | device ID | — | — |
| 4 | UserSetupIndex | 0 | Setup slot |
| 5 | SEN | 1 | Sensitivity |
| 6 | FF | 2 | Force Feedback |
| 7 | SHO | 3 | Shock |
| 8 | BLI | 4 | Brake Linearity |
| 9 | LIN/FFS | 5 | Linearity |
| 10 | DEA | 6 | Dead Zone |
| 11 | DRI (signed) | 7 | Drift |
| 12 | FOR | 8 | Force |
| 13 | SPR | 9 | Spring |
| 14 | DPR | 10 | Damper |
| 15 | NDP | 11 | Natural Damper |
| 16 | NFR | 12 | Natural Friction |
| 17 | BRF | 13 | Brake Force |
| 18 | BRG | 14 | Brake Gain |
| 19 | FEI | 15 | Force Effect Int. |
| 20 | MPS | 16 | Max Power Supply |
| 21 | APM | 17 | Adv Paddle Mode |
| 22 | INT | 18 | Interactivity |
| 23 | NIN | 19 | Natural Inertia |
| 24 | FUL | 20 | Full Lock |
| 25 | BIL | 21 | Bilateral |
| 26 | ROT | 22 | Rotation |
| 27–63 | `0x00` | 23–63 | Reserved |

### Clutch Bite Point in Tuning Context

CBP is **not** part of the tuning data structure. It uses a completely separate command path via the WheelCommand interface. See [Clutch Bite Point](#clutch-bite-point-cbp) below.

When setting multiple tuning parameters at once, CBP should be sent **after** the col03 tuning data write, matching the ordering used by the official software.

### Live Change Notifications

The firmware supports event-driven tuning change notification — no polling required. Four event types are available:

| Event | Description |
|-------|-------------|
| TuningMenuDataChanged | Tuning parameters changed (user adjusted via wheel controls) |
| ITMPageChanged | ITM page subscription changed |
| AnalysisPageChanged | Analysis page updated |
| DeviceSettingsChanged | CBP / TorqueMode / MaxTorque / FFS changed |

### Tuning Wheel-Specific Notes

#### APM (Advanced Paddle Mode) — Rotary Wheels Only

Only these steering wheels report APM via the tuning menu:

| Wheel | Internal ID |
|-------|-------------|
| CSL Elite McLaren GT3 | CSLRMCL |
| CSL Steering Wheel GT3 | CSLSWGT3 |
| CSL Elite McLaren GT3 V1.1 | CSLRMCLV1_1 |
| ClubSport Formula V2 | CSWRFORMV2 |

For all other wheels, APM is ignored (always zero).

---

## Clutch Bite Point (CBP)

The Clutch Bite Point (CBP) controls the engagement threshold for analog clutch paddles. CBP uses a **completely separate command path** from the [tuning menu](#tuning-menu) — it is not part of the tuning parameter structure.

### Set CBP

Sends a CBP value to the device via an 8-byte col01 report:

```
[ReportID, 0xF8, 0x09, 0x01, 0x17, 0x01, <CBP>, 0x00]
                              │     │     │
                              │     │     └─ CBP value (0–100, clamped)
                              │     └─────── Enable flag (always 0x01)
                              └───────────── CBP subcmd identifier
```

The value is clamped to the range 0–100.

After sending the set command, the [trigger sequence](#trigger-mechanism) must be executed:

```
Complete Set CBP sequence:
1. Send: [RID, F8, 09, 01, 17, 01, <CBP>, 00]   ← Set CBP value
2. Send: [RID, F8, 09, 01, 06, FF, 02,    00]   ← Trigger ON (SubId=2)
3. Send: [RID, F8, 09, 01, 06, 00, 00,    00]   ← Trigger OFF
4. Wait: 100ms                                    ← Firmware processing time
```

### Get CBP

Reading the CBP value requires triggering the firmware to report its current state, then reading the value after a delay:

```
Complete Get CBP sequence:
1. Send: [RID, F8, 09, 01, 06, FF, 02, 00]   ← Trigger ON (SubId=2)
2. Send: [RID, F8, 09, 01, 06, 00, 00, 00]   ← Trigger OFF
3. Wait: 100ms                                  ← Wait for firmware response
4. Read: CBP value from device input report
```

The 100ms delay is necessary because the firmware sends a response via the input handler. The official SDK reads the resulting value from a Windows registry key where the filter driver stores it.

### Trigger Mechanism

The trigger mechanism is a general-purpose notification system used by several commands. It sends a pair of col01 reports — an ON followed by an OFF:

#### Report Format

```
Trigger ON:  [ReportID, 0xF8, 0x09, 0x01, 0x06, 0xFF, <SubId>, 0x00]
Trigger OFF: [ReportID, 0xF8, 0x09, 0x01, 0x06, 0x00, 0x00,    0x00]
```

| Byte | ON value | OFF value | Description |
|------|----------|-----------|-------------|
| 4 | `0x06` | `0x06` | Report trigger subcmd |
| 5 | `0xFF` | `0x00` | Enable flag (ON/OFF) |
| 6 | SubId | `0x00` | Identifies the trigger purpose |
| 7 | `0x00` | `0x00` | Padding |

#### SubId Values

| SubId | Purpose | Used By |
|-------|---------|---------|
| 0 | Cancel / off | Always sent as the second half of a pair |
| 1 | Button module detection refresh | BME refresh operations |
| 2 | **CBP data request / notification** | **SetClutchBitePoint / GetClutchBitePoint** |

SubId=2 tells the firmware either "I just set CBP" (after Set) or "please report the current CBP" (before Get). The firmware responds with a HID input report that the input handler processes.

#### Important: Single Pair + Delay

The correct sequence is **one ON/OFF pair** followed by a **100ms sleep**. The sleep gives the firmware time to process the trigger and send its response.

### CBP Integration with Tuning Menu

CBP is always sent **after** tuning data when both are being set:

```
1. Send tuning data via col03  (FF 03 00 ...)    ← WRITE command
2. Send CBP via col01          (RID F8 09 01 17 ...)
3. Send trigger pair           (SubId=2)
4. Wait 100ms
```

This ordering matches the official software's behavior, where `TuningMenuDataSet` is called first, followed by `ClutchBitePointSet`.

### Display Interaction

The SubId=2 trigger can cause the firmware to momentarily display the CBP value on the 7-segment display. This is by design — the firmware uses the trigger as a signal to show the CBP feedback to the user.

During this display period, sending [7-segment display](#7-segment-display) commands will conflict with the firmware's CBP display, causing visible flickering. The 100ms delay helps, but the firmware may hold the CBP display for longer.

For OLED-equipped devices (e.g., PBME), the display ownership can be explicitly yielded to the firmware during CBP operations using the [SevenSegmentModeEnable](#display-ownership) command. For non-OLED devices, display suspension must be managed by the host software.

### CBP Prerequisites

- The connected steering wheel rim must have clutch paddles. The official SDK checks `HasWheelRimClutchPaddles` before allowing CBP operations.
- CBP operations should not be performed during active display updates to avoid flickering.
