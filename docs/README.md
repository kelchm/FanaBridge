# Fanatec Protocol & Device Documentation

This directory contains documentation for the Fanatec HID protocols and hardware ecosystem, as used by [FanaBridge](../README.md) and applicable to anyone working with Fanatec steering wheels, wheelbases, and button modules.

| Document | Description |
|----------|-------------|
| [Terminology](terminology.md) | Glossary of all key concepts — hardware categories, display types, protocol terms, color encodings, SDK concepts |
| [Devices](devices.md) | Wheelbases, wheels, hubs, button modules — identification, capabilities, and the compositional model |
| [Protocol](protocol.md) | HID transport, LED control, 7-segment display, ITM display, tuning menu, clutch bite point |

## Conventions

- **Byte values** are written in hexadecimal with `0x` prefix (e.g., `0xFF`).
- **Report bytes** are shown as space-separated hex: `FF 05 04 03 01`.
- **Byte offsets** are zero-indexed.
- **Host** refers to the PC sending commands to the device.
- **Device** refers to the Fanatec wheelbase + attached peripherals.
- All multi-byte integer values are **little-endian** unless noted otherwise.
