# Fanatec HID Protocol

Fanatec wheelbases communicate with the host PC over USB HID (Human Interface Device). All commands and data flow through HID reports organized into separate **collections**, each with its own endpoint, report size, and purpose.

## Table of Contents

- [USB Identifiers](#usb-identifiers)
- [HID Collections](#hid-collections)
- [Report Framing](#report-framing)
- [Collection Routing](#collection-routing)
- [col01 Reference](#col01-reference)
  - [Direct Subcmds](#direct-subcmds)
    - [0x02 — Rev LED Global On/Off](#0x02--rev-led-global-onoff)
    - [0x06 — RevStripe Enable/Disable](#0x06--revstripe-enabledisable)
    - [0x07 — Rev LED Blink Enable](#0x07--rev-led-blink-enable)
    - [0x08 — Rev LED Data (Bitmask / Color)](#0x08--rev-led-data-bitmask--color)
    - [0x09 / 0x0A — RGB Rev LED Data (Legacy)](#0x09--0x0a--rgb-rev-led-data-legacy)
    - [0x0C — Flag LED Data (Legacy)](#0x0c--flag-led-data-legacy)
  - [Group 0x01 Subcmds](#group-0x01-subcmds)
    - [0x02 — 7-Segment Display Data](#0x02--7-segment-display-data)
    - [0x06 — Report Trigger / ACK](#0x06--report-trigger--ack)
    - [0x17 — Set Clutch Bite Point](#0x17--set-clutch-bite-point)
    - [0x18 — Display Ownership](#0x18--display-ownership)
- [col03 Reference](#col03-reference)
  - [0x01 — LED Control](#0x01--led-control)
    - [0x00 — Rev LEDs](#0x00--rev-leds)
    - [0x01 — Flag LEDs](#0x01--flag-leds)
    - [0x02 — Button Colors (Staged)](#0x02--button-colors-staged)
    - [0x03 — Button Intensities (Staged)](#0x03--button-intensities-staged)
  - [0x02 — ITM Enable](#0x02--itm-enable)
  - [0x03 — Tuning Menu](#0x03--tuning-menu)
    - [0x00 — WRITE](#0x00--write)
    - [0x01 — SELECT SETUP](#0x01--select-setup)
    - [0x02 — READ](#0x02--read)
    - [0x03 — SAVE](#0x03--save)
    - [0x04 — RESET](#0x04--reset)
    - [0x06 — TOGGLE](#0x06--toggle)
    - [Tuning Payload Structure](#tuning-payload-structure)
    - [READ vs WRITE Report Layout](#read-vs-write-report-layout)
    - [Read-Modify-Write Pattern](#read-modify-write-pattern)
    - [WRITE Report Byte Map](#write-report-byte-map)
    - [Live Change Notifications](#live-change-notifications)
  - [0x05 — ITM Display](#0x05--itm-display)
    - [0x01 — ValueUpdate](#0x01--valueupdate)
    - [0x03 — ParamDefs](#0x03--paramdefs)
    - [0x04 — PageSet / Keepalive / Config](#0x04--pageset--keepalive--config)
- [Cross-Reference Topics](#cross-reference-topics)
  - [LEDs](#leds)
  - [Displays](#displays)
  - [Tuning & Configuration](#tuning--configuration)
- [Appendix](#appendix)
  - [RGB333 Color Encoding](#rgb333-color-encoding)
  - [RGB565 Color Encoding](#rgb565-color-encoding)
  - [7-Segment Encoding Tables](#7-segment-encoding-tables)
  - [ITM Parameter IDs](#itm-parameter-ids)
  - [ITM Unit System](#itm-unit-system)
  - [ITM Page Layouts](#itm-page-layouts)
  - [ITM Supported Devices](#itm-supported-devices)
  - [APM-Capable Wheels](#apm-capable-wheels)

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

The original control channel, used by older wheels and for commands that predate the 64-byte protocol. See [col01 Reference](#col01-reference) for all subcmds.

### col02 — Input (8 bytes)

Device-to-host input reports carrying button states, encoder positions, and axis values. Not covered in detail here — this channel is read by the OS HID driver and exposed as a standard game controller.

### col03 — Modern Control (64 bytes)

The extended control channel, used by newer devices for features requiring larger payloads. Not all devices support col03. Older wheelbases and rims operate exclusively through col01. See [col03 Reference](#col03-reference) for all command classes.

## Report Framing

### col01 Reports

All col01 output reports are **8 bytes**. The first byte is the **report ID**, followed by a command header:

```
Byte:  [0]     [1]   [2]   [3]      [4]      [5]   [6]   [7]
       ReportID 0xF8  0x09  subcmd   data...
```

The report ID byte (`0x00` or `0x01`) is device-specific and assigned during initialization: use `0x01` on col03-capable devices and `0x00` on col01-only devices. The constant prefix `0xF8 0x09` identifies this as a Fanatec control command.

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
| `0x01` | [LED control](#0x01--led-control) |
| `0x02` | [ITM enable](#0x02--itm-enable) |
| `0x03` | [Tuning menu](#0x03--tuning-menu) |
| `0x05` | [ITM display](#0x05--itm-display) |

## Collection Routing

col01 and col03 are separate HID interfaces — the host opens and writes to each one independently. The two are distinguished by their framing:

| Collection | First Byte | Report Size |
|------------|-----------|-------------|
| col03 | `0xFF` | 64 bytes |
| col01 | Device report ID | 8 bytes |

> **Note:** The Fanatec SDK provides a single high-level send path that uses the first byte of the application buffer as a routing hint (`0xFF` → col03, anything else → col01 with the first byte replaced by the device report ID). This is an SDK convenience, not a wire-level mechanism.

### col03 Endpoint Discovery

The col03 collection is a separate HID interface from col01. On Windows, col03 device paths typically contain `"col03"` in the path string and can also be identified by a 64-byte maximum output report length.

Whether col03 availability is determined by the **wheelbase** (always present on modern bases) or the **wheel** (only present when a col03-capable rim is connected) has not been verified. In practice, col03 should be discovered dynamically at connection time rather than assumed.

<!-- TODO: Verify col03 availability behavior — does a modern wheelbase always expose the col03 interface, or only when a col03-capable wheel is attached? -->

---

## col01 Reference

All col01 commands use the `[ReportID, 0xF8, 0x09, ...]` framing described in [Report Framing](#col01-reports).

### Direct Subcmds

Direct subcmds are identified by byte[3] of the report.

#### 0x02 — Rev LED Global On/Off

Enables or disables the rev LED strip globally.

```
[ReportID, 0xF8, 0x09, 0x02, enable, 0x00, 0x00, 0x00]
```

| Byte | Field | Values |
|------|-------|--------|
| 4 | enable | `0x01` = on, `0x00` = off |

#### 0x06 — RevStripe Enable/Disable

Enables or disables the RevStripe LED strip. Found on three rims: CSLRP1X, CSLRP1PS4, CSLRWRC.

**Inverted semantics** — `0x00` means ON:

```
Enable:  [ReportID, 0xF8, 0x09, 0x06, 0x00, 0x00, 0x00, 0x00]
Disable: [ReportID, 0xF8, 0x09, 0x06, 0x01, 0x00, 0x00, 0x00]
```

| Byte | Field | Values |
|------|-------|--------|
| 4 | enable | `0x00` = on, `0x01` = off (inverted) |

**Typical RevStripe sequence:**

```
1. Enable RevStripe:  [RID, F8, 09, 06, 00, 00, 00, 00]
2. Global LEDs on:    [RID, F8, 09, 02, 01, 00, 00, 00]
3. Set color (red):   [RID, F8, 09, 08, 00, 38, 00, 00]

To turn off:
4. Set color (off):   [RID, F8, 09, 08, 00, 00, 00, 00]
5. Global LEDs off:   [RID, F8, 09, 02, 00, 00, 00, 00]
```

#### 0x07 — Rev LED Blink Enable

Enables blinking mode for rev LEDs.

```
[ReportID, 0xF8, 0x09, 0x07, 0x01, 0x00, 0x00, 0x00]
```

#### 0x08 — Rev LED Data (Bitmask / Color)

Sends LED data — interpreted as either a bitmask or a color depending on the connected rim.

```
[ReportID, 0xF8, 0x09, 0x08, data_lo, data_hi, 0x00, 0x00]
```

**Non-RGB rims** (e.g., CSSWBMWV2): 9-bit bitmask where each bit controls one LED (bit 0 = LED 0):

```
Example: LEDs 0, 1, 2 on, rest off
  data_lo = 0x07, data_hi = 0x00   (bitmask: 0b000000111)

Example: All 9 LEDs on
  data_lo = 0xFF, data_hi = 0x01   (bitmask: 0b111111111)
```

**RevStripe rims** (CSLRP1X, CSLRP1PS4, CSLRWRC): [RGB333](#rgb333-color-encoding) color value controlling the entire strip as one unit:

```
Example: Red     → data_lo = 0x00, data_hi = 0x38
Example: Green   → data_lo = 0x01, data_hi = 0xC0
```

#### 0x09 / 0x0A — RGB Rev LED Data (Legacy)

Color data for RGB-capable rims sent via col01. Used by older RGB rims that lack col03 support.

```
[ReportID, 0xF8, 0x09, 0x09, data...]
[ReportID, 0xF8, 0x09, 0x0A, data...]
```

#### 0x0C — Flag LED Data (Legacy)

Sets flag LED color via legacy protocol. Only a subset of wheels have flag LEDs — see the [devices reference](devices.md#flag-leds) for the support matrix.

```
[ReportID, 0xF8, 0x09, 0x0C, flag_color, dirty_flag, 0x00, 0x00]
```

### Group 0x01 Subcmds

Group 0x01 subcmds use byte[3] = `0x01` with the operation in byte[4]:

```
[ReportID, 0xF8, 0x09, 0x01, subcmd, data...]
```

#### 0x02 — 7-Segment Display Data

Controls the 3-digit display found on many Fanatec wheels, hubs, and button modules. Typically used to show gear, speed, or short text strings.

The same command is used regardless of the underlying display hardware — physical LED 7-segment displays and small OLED displays are both addressed identically. On devices with a larger ITM-capable OLED (e.g., PBME), this drives the **legacy mode** (the last ITM page), which renders 7-segment-style content. See [Display Capabilities](devices.md#display-capabilities) for per-device display types.

```
[ReportID, 0xF8, 0x09, 0x01, 0x02, <seg1>, <seg2>, <seg3>]
```

| Byte | Field | Description |
|------|-------|-------------|
| 5 | seg1 | Left digit — segment bitmask |
| 6 | seg2 | Center digit — segment bitmask |
| 7 | seg3 | Right digit — segment bitmask |

Each segment byte is a bitmask controlling 7 segments plus a decimal point. See [7-Segment Encoding Tables](#7-segment-encoding-tables) for the full digit, letter, and symbol lookup tables.

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

The decimal point (bit 7, `0x80`) can be combined with any character via bitwise OR:
```
Digit 3 with dot: 0x4F | 0x80 = 0xCF
Letter A with dot: 0x77 | 0x80 = 0xF7
```

**Examples:**

```
Display Gear "5":    [RID, F8, 09, 01, 02, 00, 6D, 00]   (center = 0x6D)
Display Speed "142": [RID, F8, 09, 01, 02, 06, 66, 5B]   (1, 4, 2)
Display Text "Hi":   [RID, F8, 09, 01, 02, 76, 06, 00]   (H, I, blank)
Clear Display:       [RID, F8, 09, 01, 02, 00, 00, 00]
```

#### 0x06 — Report Trigger / ACK

General-purpose notification mechanism used to trigger firmware actions. Sent as an ON/OFF pair:

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

**SubId values:**

| SubId | Purpose | Used By |
|-------|---------|---------|
| 0 | Cancel / off | Always sent as the second half of a pair |
| 1 | Button module detection refresh | BME refresh operations |
| 2 | **CBP data request / notification** | **[Set CBP](#0x17--set-clutch-bite-point) / Get CBP** |

SubId=2 tells the firmware either "I just set CBP" (after Set) or "please report the current CBP" (before Get). The firmware responds with a HID input report that the input handler processes.

The correct sequence is **one ON/OFF pair** followed by a **100ms sleep**. The sleep gives the firmware time to process the trigger and send its response.

#### 0x17 — Set Clutch Bite Point

Sets the clutch bite point (CBP) engagement threshold for analog clutch paddles.

```
[ReportID, 0xF8, 0x09, 0x01, 0x17, 0x01, <CBP>, 0x00]
```

| Byte | Field | Description |
|------|-------|-------------|
| 5 | `0x01` | Enable flag (always `0x01`) |
| 6 | CBP | CBP value (0–100, clamped) |

CBP uses a **completely separate command path** from the [tuning menu](#0x03--tuning-menu) — it is not part of the tuning payload structure.

**Prerequisites:**
- The connected steering wheel must have clutch paddles.
- CBP operations should not be performed during active display updates to avoid flickering.

**Complete Set CBP sequence:**

```
1. Send: [RID, F8, 09, 01, 17, 01, <CBP>, 00]   ← Set CBP value
2. Send: [RID, F8, 09, 01, 06, FF, 02,    00]   ← Trigger ON (SubId=2)
3. Send: [RID, F8, 09, 01, 06, 00, 00,    00]   ← Trigger OFF
4. Wait: 100ms                                    ← Firmware processing time
```

**Complete Get CBP sequence:**

```
1. Send: [RID, F8, 09, 01, 06, FF, 02, 00]   ← Trigger ON (SubId=2)
2. Send: [RID, F8, 09, 01, 06, 00, 00, 00]   ← Trigger OFF
3. Wait: 100ms                                  ← Wait for firmware response
4. Read: CBP value from device input report
```

The 100ms delay is necessary because the firmware sends a response via the input handler. The official software retrieves the resulting value from a Windows registry key where the filter driver stores it.

**Display interaction:** The SubId=2 trigger can cause the firmware to momentarily display the CBP value on the 7-segment display. This is by design — the firmware uses the trigger as a signal to show CBP feedback to the user. The 100ms delay helps, but the firmware may hold the CBP display for longer. During this period, sending [7-segment display](#0x02--7-segment-display-data) commands will conflict with the firmware's CBP display, causing visible flickering. For OLED-equipped devices (e.g., PBME), use [display ownership](#0x18--display-ownership) to yield to the firmware during CBP operations. For non-OLED devices, display suspension must be managed by the host software.

<!-- TODO: Document exact firmware display hold duration and recommended host-side delay/backoff strategy -->

#### 0x18 — Display Ownership

On certain OLED-equipped devices (currently only the PBME), the host can explicitly take or release control of the display. The official software checks whether the device has an OLED before sending this command.

```
Host takes control:   [RID, F8, 09, 01, 18, 02, 00, 00]
Release to firmware:  [RID, F8, 09, 01, 18, 01, 00, 00]
```

| Byte[5] | Meaning |
|---------|---------|
| `0x02` | Host control — firmware stops updating the display |
| `0x01` | Firmware control — firmware resumes display ownership |

This is important during operations where the firmware needs to show its own content (e.g., [CBP adjustment](#0x17--set-clutch-bite-point), tuning menu navigation). The host should release control, wait for the operation to complete, then reclaim control.

For non-OLED devices (including LED 7-segment displays and the PBMR's small OLED), this command is a no-op. Display conflict management on these devices must be handled by the host software.

---

## col03 Reference

All col03 commands use the `[0xFF, cmd_class, subcmd, ...]` framing described in [Report Framing](#col03-reports).

### 0x01 — LED Control

Modern LED protocol using per-LED RGB565 color values. Used by newer rims with col03 support (PBME, CSSWFORMV3, GTSWX, etc.).

```
Byte:  [0]   [1]   [2]      [3..4]    [5..6]    ...
       0xFF  0x01  subcmd   LED0_RGB  LED1_RGB  ...
```

Each LED color is a **16-bit [RGB565](#rgb565-color-encoding)** value stored in **big-endian** byte order. A color value of `0x0000` means the LED is off.

#### 0x00 — Rev LEDs

RPM/shift indicator colors. Up to 30 LEDs, one RGB565 value per LED.

```
FF 01 00 [R0hi R0lo] [R1hi R1lo] ... [R8hi R8lo] 00...
```

#### 0x01 — Flag LEDs

Status/warning indicator colors. Up to 30 LEDs, one RGB565 value per LED.

```
FF 01 01 [F0hi F0lo] [F1hi F1lo] ... [F5hi F5lo] 00...
```

#### 0x02 — Button Colors (Staged)

Button backlight RGB colors. Up to 12 RGB565 values. Only applies to devices with RGB-capable button LEDs (currently PSWBMW, GTSWX, and the PBMR module).

> **Note — PBMR color inconsistency:** The PBMR is the only known device that interprets these RGB565 values as RGB555 (5 bits per channel, green MSB ignored). This means the 6th green bit (G5) is silently discarded. For example, `0x0400` (RGB565: R=0, G=32, B=0 — a dim green) renders as **black** on the PBMR because only G5 is set and it's the ignored bit. In practice, colors should be constructed with 5-bit green values (shift left by 6, not 5) to display correctly on PBMR. See [PBMR](devices.md#pbmr-podium-button-module-rally) for details.

```
Byte:  [0]   [1]   [2]   [3..4]    ... [25..26]  [27]
       0xFF  0x01  0x02  LED0_RGB  ... LED11_RGB  commit
```

| Field | Bytes | Description |
|-------|-------|-------------|
| LED colors | 3–26 | Up to 12 RGB565 values (big-endian) |
| Commit | 27 | `0x01` = apply, `0x00` = stage only |

#### 0x03 — Button Intensities (Staged)

Per-button intensity values. 3-bit values (range 0–7).

```
Byte:  [0]   [1]   [2]   [3]     [4]     ... [17]    [18]
       0xFF  0x01  0x03  int_0   int_1   ... int_14  commit
```

| Field | Bytes | Description |
|-------|-------|-------------|
| Intensities | 3–17 | 15 intensity bytes (3-bit, range 0–7) |
| Commit | 18 | `0x01` = apply, `0x00` = stage only |

The intensity payload has 15 slots while the color payload has 12 — the extra slots exist because intensity values are 1 byte each vs 2 bytes for RGB565 colors. On some devices, the higher-indexed intensity slots control additional lighting elements (e.g., encoder backlighting) that are intensity-only and have no corresponding color slot.

**Staging behavior:** When both color and intensity need to change:
1. Send the color report with commit = `0x00` (stage)
2. Send the intensity report with commit = `0x01` (commit both)

When only one has changed, send that report alone with commit = `0x01`.

### 0x02 — ITM Enable

Activates ITM mode on the wheel display. Note this uses command class `0x02`, not `0x05`.

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
- Byte[3] can also be set to 0–6 to select the analysis page.

### 0x03 — Tuning Menu

Controls all wheelbase settings (SEN, FF, damper, spring, etc.) via command class `0x03`.

```
Byte:  [0]   [1]   [2]      [3]      [4..63]
       0xFF  0x03  subcmd   devId    payload (zero-padded to 64 bytes)
```

#### 0x00 — WRITE

Sets tuning parameters.

```
[FF 03 00 devId data[0] data[1] ... data[59]]     (64 bytes)
```

The tuning data starts at **byte[4]** in WRITE commands. See [Tuning Payload Structure](#tuning-payload-structure) for field offsets and [WRITE Report Byte Map](#write-report-byte-map) for the complete byte-level layout.

The device **rejects WRITE commands** that don't reflect the current state. Always use the [Read-Modify-Write Pattern](#read-modify-write-pattern). The WRITE itself produces no response — once sent, no trigger or acknowledgment is needed.

> **Subcmd `0x06` disambiguation:** Two unrelated commands share the number `0x06`. Neither is part of the WRITE flow:
> - [TOGGLE](#0x06--toggle) (`FF 03 06`) — col03 tuning subcmd that switches between standard and simplified tuning mode.
> - [Report Trigger](#0x06--report-trigger--ack) (`RID F8 09 01 06`) — col01 group 0x01 subcmd used for CBP operations.

#### 0x01 — SELECT SETUP

Switches the active setup index (0–4).

```
[FF 03 01 devId setupIndex 00 ... 00]
```

#### 0x02 — READ

Requests the current tuning state. Triggers a response on the col03 IN endpoint.

**Request:**
```
[FF 03 02 00 00 00 ... 00]     (64 bytes)
```

**Response:**
```
[FF 03 devId data[0] data[1] ... data[60]]     (64 bytes)
```

In the READ response, tuning data starts at **byte[3]** — shifted by 1 byte relative to the WRITE command. See [READ vs WRITE Report Layout](#read-vs-write-report-layout) for details.

#### 0x03 — SAVE

Persists current tuning state to device flash.

```
[FF 03 03 devId 00 ... 00]
```

#### 0x04 — RESET

Restores factory defaults for tuning parameters.

```
[FF 03 04 devId 00 ... 00]
```

#### 0x06 — TOGGLE

Toggles between standard and simplified tuning mode.

```
[FF 03 06 devId 00 ... 00]
```

#### Tuning Payload Structure

The tuning payload is embedded in both READ responses and WRITE commands at different byte offsets (see [READ vs WRITE Report Layout](#read-vs-write-report-layout)). The offsets below are relative to the start of the payload, not the HID report:

| Offset | Field | Type | Range | Description |
|--------|-------|------|-------|-------------|
| 0 | UserSetupIndex | byte | 0–4 | Active setup slot |
| 1 | SEN | byte | 0–255 | Steering Sensitivity |
| 2 | FF | byte | 0–255 | Force Feedback strength |
| 3 | SHO | byte | 0–255 | Shock / vibration intensity |
| 4 | BLI | byte | 0–255 | Brake Linearity |
| 5 | LIN | byte | 0–255 | Linearity (aliased as FFS in some variants) |
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
| 17 | APM | byte | 0–255 | Advanced Paddle Mode ([rotary wheels only](#apm-capable-wheels)) |
| 18 | INT | byte | 0–255 | Interactivity |
| 19 | NIN | byte | 0–255 | Natural Inertia |
| 20 | FUL | byte | 0–255 | Full Lock (steering angle) |
| 21 | BIL | byte | 0–255 | Bilateral / Balance |
| 22 | ROT | byte | 0–255 | Rotation |

**Notes:**

- **DRI (offset 7)** is the only signed byte in the structure.
- **LIN vs FFS**: Two variants of this data structure exist in the official software — one names offset 5 `LIN`, the other calls it `FFS`. Same byte position, same semantics.
- **APM (offset 17)**: Only populated on [APM-capable wheels](#apm-capable-wheels). Zero on all other wheels.
- Payload offsets 23–59 are reserved and always zero in WRITE commands (60 payload bytes at HID bytes 4–63). READ responses carry one additional trailing byte (offset 60 at HID byte 63). The SDK's internal managed struct is 65 bytes (offsets 0–64), but only offsets 0–59 appear on the wire in a WRITE.

#### READ vs WRITE Report Layout

**Critical:** READ responses and WRITE commands place the tuning data at **different byte offsets** within the 64-byte HID report.

| Direction | Header | Data Start | Reason |
|-----------|--------|------------|--------|
| READ response | `[FF 03 devId ...]` | **byte[3]** | No subcmd byte in response |
| WRITE command | `[FF 03 00 devId ...]` | **byte[4]** | Subcmd `0x00` occupies byte[2] |

**Conversion from READ → WRITE buffer:**

```
writeBuf[0]     = 0xFF
writeBuf[1]     = 0x03
writeBuf[2]     = 0x00              // subcmd = WRITE
writeBuf[3]     = readBuf[2]        // device ID
writeBuf[4..63] = readBuf[3..62]    // tuning data (shifted by 1)
```

#### Read-Modify-Write Pattern

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

**Example — Changing Force Feedback Strength:**

FF is at struct offset 2. In the WRITE buffer: `writeBuf[4 + 2] = writeBuf[6]`:

```
writeBuf[6] = 80;    // Set FF to 80
```

**Example — Switching Setup Slot:**

UserSetupIndex is at struct offset 0. In the WRITE buffer: `writeBuf[4]`:

```
writeBuf[4] = 2;     // Switch to setup slot 2
```

The dedicated [SELECT SETUP](#0x01--select-setup) subcmd can also be used for this.

#### WRITE Report Byte Map

Quick reference mapping HID byte positions to tuning fields:

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
| 27–63 | `0x00` | 23–59 | Reserved |

#### Live Change Notifications

The firmware supports event-driven tuning change notification — no polling required:

| Event | Description |
|-------|-------------|
| TuningMenuDataChanged | Tuning parameters changed (user adjusted via wheel controls) |
| ITMPageChanged | ITM page subscription changed |
| AnalysisPageChanged | Analysis page updated |
| DeviceSettingsChanged | CBP / TorqueMode / MaxTorque / FFS changed |

#### CBP in Tuning Context

CBP is **not** part of the tuning data structure. It uses a completely separate command path via col01. See [Set Clutch Bite Point](#0x17--set-clutch-bite-point).

When setting multiple tuning parameters at once, CBP should be sent **after** the col03 tuning data write. This matches the ordering observed in the official software — tuning data first, then CBP:

```
1. Send tuning data via col03  (FF 03 00 ...)    ← WRITE command
2. Send CBP via col01          (RID F8 09 01 17 ...)
3. Send trigger pair           (SubId=2)
4. Wait 100ms
```

### 0x05 — ITM Display

Controls OLED and LCD telemetry displays. Provides multi-page telemetry dashboards with parameters like speed, gear, lap times, tyre temperatures, and more. See [ITM Supported Devices](#itm-supported-devices) for the device compatibility matrix.

All ITM display commands share the frame:

```
Byte:  [0]   [1]   [2]      [3..63]
       0xFF  0x05  subcmd   payload
```

#### 0x01 — ValueUpdate

Sends telemetry values for display. Each entry contains a handle, parameter ID, and value:

```
FF 05 01 <entries...> [00-padded to 64 bytes]
```

Each entry:

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 | Marker | Always `0x01` |
| 1 | 1 | Handle | Parameter handle (assigned during ParamDefs) |
| 2–3 | 2 | Param ID | Parameter ID (little-endian). See [ITM Parameter IDs](#itm-parameter-ids). |
| 4 | 1 | Size | Value size in bytes (1, 2, or 4) |
| 5+ | N | Value | Parameter value (little-endian, size from above) |

#### 0x03 — ParamDefs

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

The suffix system corresponds to the unit display feature in the official software — the "/0" suffix likely represents the total denominator (e.g., "Lap 5 / 20").

Maximum subscribed parameters per device: **16**.

#### 0x04 — PageSet / Keepalive / Config

Subcmd `0x04` is overloaded for several functions based on byte[3]:

**PageSet** — selects which ITM page is active on a specific display device:

```
FF 05 04 <deviceId> <page> [00 x59]
```

| Byte | Field | Description |
|------|-------|-------------|
| 3 | Device ID | Target display (1=Base, 3=BME/GTSWX, 4=Bentley) |
| 4 | Page number | Page to display (1–6, device-dependent). See [ITM Page Layouts](#itm-page-layouts). |

Page change commands should be spaced at least 100ms apart. The firmware needs time to reconfigure its internal display state between pages.

**Keepalive** — must be sent every ~100ms to keep the ITM display alive:

```
FF 05 04 02 0B [00 x59]
```

| Byte | Value | Description |
|------|-------|-------------|
| 3 | `0x02` | Config type |
| 4 | `0x0B` | Config value |

#### Slot & Handle Mapping (Raw HID)

When using raw HID (bypassing the official software), displays require explicit slot configuration via ParamDefs:

| Page(s) | Slot IDs | Handle Range | Suffix |
|---------|----------|--------------|--------|
| 1, 5 | 0x82, 0x83, 0x84, 0x85 | 0–5 | "/0" (Page 1 only) |
| 2, 4 | 0x88 | 6–12 | "/0" (Page 2 only) |
| 3 | 0x85 | 0–5 + 13 | none |

#### Control Model: Official Software vs Raw HID

**Official Software Approach (Firmware-Driven):**

The firmware maintains a subscription table of up to 16 parameter slots. The host queries which parameters the firmware expects for the current page, then populates them:

1. Firmware owns the page layout
2. Host queries the firmware to learn what params are needed for the current page
3. Host sends values for subscribed params only
4. Page content is fixed by firmware

**Raw HID Approach (Host-Driven):**

1. Host sends PageSet — tells firmware which page
2. Host sends ParamDefs — tells firmware the slot layout
3. Host sends ValueUpdate — sends actual data
4. Host can potentially choose which params appear on each page

The raw HID approach gives more flexibility but requires knowing the [page layouts](#itm-page-layouts) in advance and carries the risk that untested parameter IDs may not render correctly.

#### Timing & Rate Limiting

Not all parameters need to be sent at full rate. Recommended minimum intervals:

| Category | Delay (ms) | Parameters |
|----------|-----------|------------|
| Real-time | 0 | SPEED, GEAR, POSITION, LAP, LAST_LAP_TIME, BEST_LAP_TIME, BRAKE_BIAS, FUEL, DRS, ABS, TC |
| Near real-time | 30 | LAP_TIME |
| Moderate | 40–50 | RPM_MAX, TYRE temps |
| Low-frequency | 100 | ENGINE_MAPPING, ERS, others |

#### Automatic Page Changes (Alerts)

The official software supports automatic page switching based on telemetry events:

**Value-Change Triggers:**

| Trigger | Target Page |
|---------|-------------|
| Lap number or Position changed | Page 1 (Lap Info) |
| DRS zone or DRS active changed | Page 2 (Fuel/ERS/DRS) |
| TC, ABS, EngineMap, or BrakeBias changed | Page 3 (Car Settings) |
| Best lap time changed | Page 4 (Lap Times) |

**Threshold Triggers:**

| Trigger | Target Page |
|---------|-------------|
| Fuel below threshold | Page 2 |
| ERS below threshold | Page 2 |
| Oil temp above threshold | Page 3 |
| Any tyre temp out of range | Page 5 |

**Favorite Page:**

Each device has a configurable favorite page with a display duration (default 10 seconds, range 3–60 seconds). After a trigger-caused page change, the display reverts to the favorite page after the duration expires.

---

## Cross-Reference Topics

These sections group related commands across collections for quick navigation.

### LEDs

Fanatec steering wheels support several types of LEDs controlled through two distinct protocol generations:

| Type | Purpose | Typical Count | Color |
|------|---------|---------------|-------|
| **Rev LEDs** | RPM / shift indicator strip | 9 | Per-LED RGB (modern) or on/off (legacy) |
| **Flag LEDs** | Status / warning indicators | 6 | Per-LED RGB (modern) or single color (legacy) |
| **Button LEDs** | Button backlighting (RGB devices only) | Up to 12 | Per-LED RGB + intensity |
| **RevStripe** | Single-color LED strip | 1 (entire strip) | RGB333 (8 colors via official software, 512 via raw HID) |

**Protocol selection by wheel type:**

| Capability | Protocol | Collection | Color Depth |
|------------|----------|------------|-------------|
| RGB LED + col03 support | Modern | [col03 0x01](#0x01--led-control) | RGB565 (65K colors) |
| RGB LED + no col03 | Legacy RGB | [col01 0x09/0x0A](#0x09--0x0a--rgb-rev-led-data-legacy) | RGB333 via col01 |
| Non-RGB LED | Legacy bitmask | [col01 0x08](#0x08--rev-led-data-bitmask--color) | On/off only + global RGB333 |
| RevStripe | Legacy color | [col01 0x06](#0x06--revstripe-enabledisable) + [0x08](#0x08--rev-led-data-bitmask--color) | RGB333 (512 colors) |

See the [devices reference](devices.md#wheel-protocol-summary) for the per-wheel capability matrix.

**Batching behavior:** The official software tracks per-LED dirty state internally and batches changes — only sending reports for LEDs whose on/off state or color has changed since the last update.

### Displays

Fanatec devices have two display protocols:

| Display Type | Protocol | Collection | Devices |
|-------------|----------|------------|---------|
| 3-digit (7-seg / small OLED) | [col01 group 0x01, subcmd 0x02](#0x02--7-segment-display-data) | col01 | Most wheels, hubs, button modules |
| Multi-page telemetry (ITM) | [col03 0x05](#0x05--itm-display) + [0x02 enable](#0x02--itm-enable) | col03 | PDD1/PDD2 base, PBME, Bentley, GTSWX |

**Display preemption:** The 7-segment display can be preempted by tuning menu navigation, [CBP adjustment](#0x17--set-clutch-bite-point), and firmware boot/init. During these periods, host display writes will conflict with firmware output. See [Display Ownership](#0x18--display-ownership) for OLED mitigation.

### Tuning & Configuration

Tuning spans two collections:

| Function | Collection | Command |
|----------|-----------|---------|
| Read/write tuning parameters | col03 | [0x03 subcmds](#0x03--tuning-menu) |
| Clutch bite point | col01 | [0x17 Set CBP](#0x17--set-clutch-bite-point) + [0x06 Trigger](#0x06--report-trigger--ack) |
| Display ownership during tuning | col01 | [0x18 Display Ownership](#0x18--display-ownership) |

When setting both tuning parameters and CBP, always send col03 tuning data first, then col01 CBP. See [CBP in Tuning Context](#cbp-in-tuning-context).

---

## Appendix

### RGB333 Color Encoding

The legacy col01 protocol uses a 9-bit **RGB333** color encoding packed into 2 bytes:

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

> **Note:** The official software only uses 8 discrete colors (each channel fully on or fully off). The hardware encoding supports 3 bits per channel, so intermediate values (e.g., R=4, G=2, B=0) may work but are not officially exercised.

### RGB565 Color Encoding

The modern col03 protocol uses **16-bit RGB565** values in **big-endian** byte order:

```
Bits:  RRRRR GGGGGG BBBBB
       15-11  10-5   4-0
```

- Red: 5 bits (0–31)
- Green: 6 bits (0–63)
- Blue: 5 bits (0–31)
- 65,536 possible colors
- `0x0000` = LED off

### 7-Segment Encoding Tables

#### Digit Encoding

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

#### Letter Encoding

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

### ITM Parameter IDs

The firmware recognizes a vocabulary of parameter IDs. Only a subset is confirmed to render correctly on current firmware — the rest may display no label, show unexpected formatting, or be silently ignored.

#### Vehicle Telemetry (1–84)

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

#### Race / Timing (501–536)

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

#### Additional Defined Parameters

The full parameter vocabulary includes 120+ IDs covering tyre pressures, brake temps, G-forces, pedal positions, flags, system metrics (CPU/GPU), and more. These are defined in the firmware's shared vocabulary but are **not confirmed to render** on all display types. The complete parameter ID ranges:

- 0–84: Vehicle telemetry
- 501–536: Race/timing data and flags
- 1001–1008: System metrics (CPU load, GPU temp, FPS, etc.)
- 65535 (`0xFFFF`): UNSUBSCRIBE sentinel

### ITM Unit System

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

"Total" units enable `value / total` display (e.g., "Lap 5 / 20"). The official software submits units on every tick; the raw HID equivalent maps to the suffix mechanism in ParamDefs.

### ITM Page Layouts

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

### ITM Supported Devices

ITM display support depends on the wheelbase, steering wheel, and button module combination:

| ITM Device | Detection | Device ID | Notes |
|------------|-----------|-----------|-------|
| **Base** | Wheelbase is PDD1, PDD1 (PS4), or PDD2 | 1 | Wheelbase's own display |
| **BME** | Button Module Endurance connected | 3 | PBME's large OLED |
| **Bentley** | Bentley GT3 steering wheel | 4 | Bentley wheel's built-in display |
| **GTSWX** | GT Steering Wheel Extreme | 3 | GTSWX's built-in display |

> **Note:** BME and GTSWX share Device ID 3 on the wire. They are mutually exclusive — a setup will have one or the other, never both.

### APM-Capable Wheels

Only these steering wheels report APM (Advanced Paddle Mode) via the tuning menu:

| ID | Wheel |
|----|-------|
| 9 | CSLRMCL |
| 10 | CSWRFORMV2 |
| 11 | CSLRMCLV1_1 |
| 25 | CSLSWGT3 |

For all other wheels, APM is ignored (always zero).
