# Fanatec Protocol & Device Documentation

This directory documents the Fanatec HID protocols and hardware ecosystem — as used by [FanaBridge](../README.md) and applicable to anyone working with Fanatec steering wheels, wheelbases, and button modules. The **reference** docs are vendor-neutral; **FanaBridge-specific** material is grouped separately.

## Reference

Vendor-neutral Fanatec protocol and hardware documentation, independent of FanaBridge:

| Document | Description |
|----------|-------------|
| [Terminology](terminology.md) | Glossary of all key concepts — hardware categories, display types, protocol terms, color encodings, SDK concepts |
| [Devices](reference/devices.md) | Wheelbases, wheels, hubs, button modules — identification, capabilities, and the compositional model |
| [Protocol](reference/protocol.md) | HID transport, LED control, 7-segment display, ITM display, tuning menu, clutch bite point |

## FanaBridge

Documentation specific to the FanaBridge plugin:

| Document | Description |
|----------|-------------|
| [Architecture](architecture.md) | Layers, namespace rule, test-tree convention, and frozen names |
| [Supported Devices](supported-devices.md) | Wheels and hub + module combos with built-in FanaBridge profiles — tested vs. unverified, and their LED/display capabilities |
| [Device Settings Lifecycle](device-settings-lifecycle.md) | How a device decides what to store, why the LED editor is built up front, and the rules that keep a save from erasing settings |

## Conventions

- **Byte values** are written in hexadecimal with `0x` prefix (e.g., `0xFF`).
- **Report bytes** are shown as space-separated hex: `FF 05 04 03 01`.
- **Byte offsets** are zero-indexed.
- **Host** refers to the PC sending commands to the device.
- **Device** refers to the Fanatec wheelbase + attached peripherals.
- All multi-byte integer values are **little-endian** unless noted otherwise.
