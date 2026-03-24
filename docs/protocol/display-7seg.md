# 7-Segment Display Protocol

The 3-digit 7-segment display is found on many Fanatec steering wheels and button modules. It is controlled via **8-byte col01 HID reports** and is typically used to show gear, speed, or short text strings.

## Display Command

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

## Segment Encoding

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

### Digit Encoding Table

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

### Letter Encoding Table

7-segment displays can approximate most letters:

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

### Symbol Encoding

| Symbol | Hex | Description |
|--------|-----|-------------|
| Blank | `0x00` | All segments off |
| `-` (Dash) | `0x40` | Middle segment only |
| `_` (Underscore) | `0x08` | Bottom segment only |
| `.` (Dot) | `0x80` | Decimal point only |

### Decimal Point

The decimal point (bit 7, `0x80`) can be combined with any character via bitwise OR:

```
Digit 3 with dot: 0x4F | 0x80 = 0xCF
Letter A with dot: 0x77 | 0x80 = 0xF7
```

## Examples

### Display Gear "5"

```
[RID, F8, 09, 01, 02, 00, 6D, 00]
                        │   │   │
                        │   │   └─ Right: blank
                        │   └───── Center: "5" (0x6D)
                        └───────── Left: blank
```

### Display Speed "142"

```
[RID, F8, 09, 01, 02, 06, 66, 5B]
                        │   │   │
                        │   │   └─ Right: "2" (0x5B)
                        │   └───── Center: "4" (0x66)
                        └───────── Left: "1" (0x06)
```

### Display Text "Hi"

```
[RID, F8, 09, 01, 02, 76, 06, 00]
                        │   │   │
                        │   │   └─ Right: blank
                        │   └───── Center: "I" (0x06)
                        └───────── Left: "H" (0x76)
```

### Clear Display

```
[RID, F8, 09, 01, 02, 00, 00, 00]
```

All three segment bytes set to `0x00` (blank).

## Display Ownership

On OLED-equipped devices, the host can explicitly take or release control of the display. This command only works on devices where the 7-segment display uses an OLED panel (not all devices):

```
Host takes control:   [RID, F8, 09, 01, 18, 02, 00, 00]
Release to firmware:  [RID, F8, 09, 01, 18, 01, 00, 00]
```

| Byte[5] | Meaning |
|---------|---------|
| `0x02` | Host control — firmware stops updating the display |
| `0x01` | Firmware control — firmware resumes display ownership |

This is important during operations where the firmware needs to show its own content (e.g., [CBP adjustment](clutch-bite-point.md), tuning menu navigation). The host should release control, wait for the operation to complete, then reclaim control.

For non-OLED devices, this command is a no-op. Display conflict management must be handled by the host software (e.g., by pausing display writes during firmware operations).

## Interaction with Other Protocols

The 7-segment display can be preempted by:

- **Tuning menu navigation** — the firmware shows tuning parameter values when the user scrolls
- **CBP adjustment** — the firmware displays the current CBP value
- **Firmware boot/init** — the display shows initialization state

During these periods, host display writes will conflict with firmware output, causing visible flickering. See the [CBP protocol](clutch-bite-point.md#display-interaction) for mitigation strategies.
