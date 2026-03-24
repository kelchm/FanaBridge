# Fanatec HID Protocol Overview

Fanatec wheelbases communicate with the host PC over USB HID (Human Interface Device). All commands and data flow through HID reports organized into separate **collections**, each with its own endpoint, report size, and purpose.

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

- [7-segment display](display-7seg.md) updates
- [Legacy LED](led-control.md#legacy-protocol-col01) reports (rev LEDs, flag LEDs, RevStripe)
- [Clutch bite point](clutch-bite-point.md) commands
- Display ownership control
- Report trigger / acknowledgment commands

### col02 — Input (8 bytes)

Device-to-host input reports carrying button states, encoder positions, and axis values. Not covered in detail here — this channel is read by the OS HID driver and exposed as a standard game controller.

### col03 — Modern Control (64 bytes)

The extended control channel, used by newer devices for features requiring larger payloads:

- [Modern LED](led-control.md#modern-protocol-col03) reports (per-LED RGB565 color)
- [ITM display](display-itm.md) commands (page control, parameter definitions, value updates)
- [Tuning menu](tuning-menu.md) read/write operations

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
| `0x01` | [LED control](led-control.md#modern-protocol-col03) (rev, flag, button colors/intensities) |
| `0x02` | [ITM enable / analysis page](display-itm.md) |
| `0x03` | [Tuning menu](tuning-menu.md) (read/write/save/reset tuning parameters) |
| `0x05` | [ITM display](display-itm.md) (page set, param defs, value updates, keepalive) |

## Collection Routing

The native Fanatec SDK determines the HID collection based on the **first byte** of the output buffer:

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
| `0x02` | [7-segment display data](display-7seg.md) | `[seg1, seg2, seg3]` — 3 segment bytes |
| `0x06` | [Report trigger / ACK](clutch-bite-point.md#trigger-mechanism) | `[enable, SubId, 0x00]` |
| `0x17` | [Set clutch bite point](clutch-bite-point.md) | `[0x01, CBP_value, 0x00]` |
| `0x18` | [Display ownership](display-7seg.md#display-ownership) | `[mode, 0x00, 0x00]` — OLED only |
