# Fanatec Protocol & Device Documentation

This directory contains documentation for the Fanatec HID protocols and hardware ecosystem, as used by [FanaBridge](../README.md) and applicable to anyone working with Fanatec steering wheels, wheelbases, and button modules.

## Protocol Reference

Fanatec devices communicate over USB HID using two report collections with distinct roles. The [protocol overview](protocol/overview.md) covers the shared transport layer.

| Document | Description |
|----------|-------------|
| [Protocol Overview](protocol/overview.md) | HID transport, collections (col01/col03), report framing, routing rules |
| [LED Control](protocol/led-control.md) | Rev LEDs, flag LEDs, RevStripe — legacy (col01) and modern (col03) protocols |
| [7-Segment Display](protocol/display-7seg.md) | 3-digit 7-segment display control and segment encoding |
| [Tuning Menu](protocol/tuning-menu.md) | Wheelbase tuning parameters (SEN, FF, SPR, DPR, etc.) — read, write, save, reset |
| [Clutch Bite Point](protocol/clutch-bite-point.md) | CBP get/set protocol and the report trigger mechanism |
| [ITM Display](protocol/display-itm.md) | In-Tuning-Menu OLED display — pages, parameters, keepalive, ParamDefs |

## Device Reference

Fanatec hardware is organized into three categories: wheelbases, steering wheels (rims), and button modules. Each has different capabilities and protocol support.

| Document | Description |
|----------|-------------|
| [Device Overview](devices/README.md) | Fanatec ecosystem overview and device identification |
| [Wheel Bases](devices/wheel-bases.md) | Supported wheelbases, USB product IDs, and base capabilities |
| [Steering Wheels](devices/steering-wheels.md) | All known steering wheel types with LED, display, and protocol capabilities |
| [Button Modules](devices/button-modules.md) | PBME, PBMR — capabilities and protocol differences |

## Conventions

- **Byte values** are written in hexadecimal with `0x` prefix (e.g., `0xFF`).
- **Report bytes** are shown as space-separated hex: `FF 05 04 03 01`.
- **Byte offsets** are zero-indexed.
- **Host** refers to the PC sending commands to the device.
- **Device** refers to the Fanatec wheelbase + attached peripherals.
- All multi-byte integer values are **little-endian** unless noted otherwise.
