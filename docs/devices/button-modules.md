# Button Modules

Button modules are optional accessories that attach to compatible steering wheels (primarily the Podium Hub). They add buttons, encoders, LEDs, and in some cases displays to the wheel.

## Module Types

| ID | Enum Name | Display Name |
|----|-----------|-------------|
| 0 | (none) | No module attached |
| 1 | PBME | Podium Button Module Endurance |
| 2 | PBMR | Podium Button Module Rally |

## Podium Button Module Endurance (PBME)

The PBME is the more capable of the two modules, featuring a large OLED display and full LED support.

### Capabilities

| Feature | Support | Protocol | Notes |
|---------|---------|----------|-------|
| Rev LEDs | 9 LEDs | Modern (col03, RGB565) | Per-LED RGB color |
| Flag LEDs | 6 LEDs | Modern (col03, RGB565) | Per-LED RGB color |
| Button LEDs | Yes | Modern (col03, staged) | RGB color + intensity |
| 7-Segment Display | Yes | col01 | 3-digit display |
| ITM Display | **Yes** | col03 | Full OLED — Device ID 3 |
| Encoder LEDs | Yes | col03 (intensity) | Part of button intensity payload |

### ITM Display

The PBME provides ITM (In-Tuning-Menu) display support using **Device ID 3**. It supports pages 1–6 with the same layout as the Base ITM. See [ITM Display Protocol](../protocol/display-itm.md) for page layouts and parameter details.

### Display Ownership (OLED)

Because the PBME uses an OLED-based 7-segment display, it supports the `SevenSegmentModeEnable` command for explicit display ownership control:

```
Host takes control:   [RID, F8, 09, 01, 18, 02, 00, 00]
Release to firmware:  [RID, F8, 09, 01, 18, 01, 00, 00]
```

This is important during CBP adjustments and tuning menu navigation, where the firmware needs to show its own content. See [7-Segment Display — Display Ownership](../protocol/display-7seg.md#display-ownership).

### col03 Input Reports

The PBME sends col03 input reports (device → host) for events like analysis page changes. This enables the firmware-driven notification system for page changes:

- `AnalysisPageChanged` — fires when the user navigates tuning/analysis pages
- Used by the SDK to detect when to yield or reclaim display control

## Podium Button Module Rally (PBMR)

The PBMR is a simpler module focused on rally-style controls with button LEDs but no OLED display and no ITM support.

### Capabilities

| Feature | Support | Protocol | Notes |
|---------|---------|----------|-------|
| Rev LEDs | No | — | Must use wheel's native rev LEDs if available |
| Flag LEDs | No | — | |
| Button LEDs | 9 LEDs | Modern (col03, RGB555) | 5-5-5 bit color (limited green channel) |
| Encoder LEDs | 3 LEDs | Modern (col03, intensity) | Part of button intensity payload |
| 7-Segment Display | Yes | col01 | 3-digit display |
| ITM Display | **No** | — | Not supported |

### Color Format Difference

The PBMR uses **RGB555** (5-5-5 bit) color encoding instead of the standard RGB565. This means the green channel has 5 bits of precision (0–31) instead of 6 bits (0–63), resulting in a slightly reduced color range.

### No col03 Input Reports

Unlike the PBME, the PBMR does **not** send col03 input reports. This means:

- No `AnalysisPageChanged` notifications
- No firmware-driven page change detection
- CBP mode detection must rely on alternative methods (e.g., registry monitoring on Windows)

### Display Ownership

The `SevenSegmentModeEnable` command is a **no-op** on the PBMR since its 7-segment display is not OLED-based. Display conflict management (e.g., during CBP adjustment) must be handled by pausing host display writes.

## Compatibility

Button modules attach to the **Podium Hub** (PHUB, steering wheel type ID 12). The Podium Hub itself has no LEDs or displays — all visual feedback comes from the attached module.

| Configuration | Rev LEDs | Flag LEDs | Button LEDs | ITM | 7-Seg |
|--------------|----------|-----------|-------------|-----|-------|
| PHUB alone | None | None | None | No | No |
| PHUB + PBME | 9 (RGB) | 6 (RGB) | Yes (RGB565) | Yes | Yes |
| PHUB + PBMR | None | None | 9+3 (RGB555) | No | Yes |

Other wheels may also support button modules, but the Podium Hub is the primary attachment point.
