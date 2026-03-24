# LED Control Protocol

Fanatec steering wheels support several types of LEDs controlled through two distinct protocol generations. The protocol used depends on the wheel hardware — newer rims use the modern col03 protocol, while older rims require the legacy col01 protocol.

## LED Types

| Type | Purpose | Typical Count | Color |
|------|---------|---------------|-------|
| **Rev LEDs** | RPM / shift indicator strip | 9 | Per-LED RGB (modern) or on/off (legacy) |
| **Flag LEDs** | Status / warning indicators | 6 | Per-LED RGB (modern) or single color (legacy) |
| **Button LEDs** | Button backlighting | Up to 12 | Per-LED RGB + intensity |
| **RevStripe** | Single-color LED strip | 1 (entire strip) | RGB333 (8 colors via SDK, 512 via raw) |
| **Mono LEDs** | Monochrome intensity LEDs | Varies | 3-bit intensity (0–7) |

## Modern Protocol (col03)

The modern protocol uses **64-byte col03 HID reports** with per-LED RGB565 color values. This is the protocol used by newer rims such as the PBME, CSSWFORMV3, GTSWX, and others with col03 support.

### Report Format

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

### Sub-commands

| Subcmd | Channel | Max LEDs | Description |
|--------|---------|----------|-------------|
| `0x00` | Rev LEDs | 30 | RPM/shift indicator colors |
| `0x01` | Flag LEDs | 30 | Status/warning indicator colors |
| `0x02` | Button colors | 12 | Button backlight RGB colors (staged) |
| `0x03` | Button intensities | 16 bytes | Per-button intensity + extra slots (staged) |

### Simple LED Reports (Rev + Flag)

Rev and flag LEDs use a straightforward report — one RGB565 value per LED, sent in a single report:

```
Example: Set 9 rev LEDs (subcmd 0x00)
FF 01 00 [R0hi R0lo] [R1hi R1lo] ... [R8hi R8lo] 00...

Example: Set 6 flag LEDs (subcmd 0x01)
FF 01 01 [F0hi F0lo] [F1hi F1lo] ... [F5hi F5lo] 00...
```

### Staged Button LED Reports (Color + Intensity)

Button LEDs use a **staged commit protocol**. Colors (subcmd `0x02`) and intensities (subcmd `0x03`) can be sent independently, and changes only take effect when the **commit byte** is set to `0x01`.

#### Button Color Report (subcmd 0x02)

```
Byte:  [0]   [1]   [2]   [3..4]    ... [25..26]  [27]
       0xFF  0x01  0x02  LED0_RGB  ... LED11_RGB  commit
```

- Bytes 3–26: Up to 12 RGB565 values (big-endian)
- Byte 27: Commit flag (`0x01` = apply, `0x00` = stage only)

#### Button Intensity Report (subcmd 0x03)

```
Byte:  [0]   [1]   [2]   [3]     [4]     ... [18]
       0xFF  0x01  0x03  int_0   int_1   ... commit
```

- Bytes 3–17: 15 intensity bytes (per-button + additional slots for encoder LEDs, etc.)
- Byte 18: Commit flag (`0x01` = apply, `0x00` = stage only)

The meaning of each intensity slot varies by wheel. For example, on wheels with encoder LEDs, some slots control encoder indicator brightness rather than button backlights.

#### Staging Behavior

When both color and intensity need to change:
1. Send the color report with commit = `0x00` (stage)
2. Send the intensity report with commit = `0x01` (commit both)

When only one has changed, send that report alone with commit = `0x01`.

## Legacy Protocol (col01)

The legacy protocol uses **8-byte col01 HID reports** with the `0xF8 0x09` command prefix. This protocol is used by older rims that do not support col03, including non-RGB rims like the CSSWBMWV2 and rims with RevStripe.

### Rev LED Control

Legacy rev LEDs are controlled via a **global on/off**, a **color setting**, and a **per-LED bitmask**.

#### Global On/Off (subcmd 0x02)

Enables or disables the entire rev LED strip:

```
[ReportID, 0xF8, 0x09, 0x02, enable, 0x00, 0x00, 0x00]

  enable: 0x01 = rev LEDs on
          0x00 = rev LEDs off
```

#### LED Bitmask / Color Data (subcmd 0x08)

Sets which individual LEDs are lit and/or the strip color:

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

#### Blink Enable (subcmd 0x07)

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

### Flag LED Control (Legacy)

Legacy flag LEDs use subcmd `0x0C`:

```
[ReportID, 0xF8, 0x09, 0x0C, flag_color, dirty_flag, 0x00, 0x00]
```

Only a subset of wheels have flag LEDs. See the [steering wheels reference](../devices/steering-wheels.md) for the support matrix.

### RevStripe

RevStripe is a **single-color LED strip** found on three specific rims (CSLRP1X, CSLRP1PS4, CSLRWRC). Unlike individual rev LEDs, the entire strip is controlled as one unit.

#### Enable/Disable (subcmd 0x06)

RevStripe uses **inverted semantics** — `0x00` means ON:

```
Enable:  [ReportID, 0xF8, 0x09, 0x06, 0x00, 0x00, 0x00, 0x00]
Disable: [ReportID, 0xF8, 0x09, 0x06, 0x01, 0x00, 0x00, 0x00]
```

#### Color Control

RevStripe color is set via subcmd `0x08` with an RGB333 value (same encoding as rev LED color). See the [RGB333 section](#rgb333-color-encoding) above.

#### Typical Sequence

```
1. Enable RevStripe:  [RID, F8, 09, 06, 00, 00, 00, 00]
2. Global LEDs on:    [RID, F8, 09, 02, 01, 00, 00, 00]
3. Set color (red):   [RID, F8, 09, 08, 38, 00, 00, 00]

To turn off:
4. Set color (off):   [RID, F8, 09, 08, 00, 00, 00, 00]
5. Global LEDs off:   [RID, F8, 09, 02, 00, 00, 00, 00]
```

## LED Internal State Model

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

## Protocol Selection by Wheel Type

Which protocol a wheel uses is determined by its hardware capabilities:

| Capability | Protocol | Collection | Color Depth |
|------------|----------|------------|-------------|
| RGB LED + col03 support | Modern | col03 (64B) | RGB565 (65K colors) |
| RGB LED + no col03 | Legacy RGB | col01 (8B) | RGB333 via subcmds 0x09/0x0A |
| Non-RGB LED | Legacy bitmask | col01 (8B) | On/off only + global RGB333 |
| RevStripe | Legacy color | col01 (8B) | RGB333 (512 colors) |

See the [steering wheels reference](../devices/steering-wheels.md) for the per-wheel capability matrix.
