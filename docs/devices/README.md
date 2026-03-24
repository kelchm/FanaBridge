# Fanatec Device Reference

The Fanatec ecosystem consists of four hardware categories:

- **Wheel bases** — The motor unit that connects to the PC via USB and provides force feedback. All HID communication flows through the wheelbase.
- **Wheels** — Self-contained steering wheel rims with a passive quick-release connection to the wheelbase. Have built-in buttons and may have LEDs, displays, and encoders. Their capabilities are fixed by the hardware.
- **Hubs** — Active mounting platforms with their own PCB/MCU and a quick-release connection to the wheelbase. Designed for attaching third-party or custom steering wheels. Have a USB-C interface for connecting a button module.
- **Button modules** — Attach to hubs via USB-C. Provide LEDs, displays, and additional buttons. A hub's effective capabilities are **compositional** — determined by the hub's native features plus the attached module's capabilities.

> **Note:** The Fanatec SDK uses a single `STEERINGWHEEL_TYPE` enum for both wheels and hubs, and a separate `BUTTON_MODULE_TYPE` enum for modules. The SDK does not enforce the physical constraint that only hubs can accept modules.

## Device Identification

All Fanatec wheelbases share a common USB **Vendor ID**: `0x0EB7` (Endor AG).

The **Product ID** varies by wheelbase model. Within a session, the wheelbase reports the connected wheel/hub type and button module type, which determine the available features and protocol capabilities.

### Identification Hierarchy

```
USB Device (VID=0x0EB7, PID=wheelbase-specific)
  └─ Wheelbase (BASE_TYPE enum)
      └─ Wheel or Hub (STEERINGWHEEL_TYPE enum)
          └─ Button Module (BUTTON_MODULE_TYPE enum, hubs only)
```

The wheelbase acts as the communication hub — all HID reports are sent to/from the wheelbase, which routes commands internally to the attached peripherals.

## Feature Capabilities

Different hardware combinations support different feature sets:

| Feature | Depends On | Protocol |
|---------|-----------|----------|
| [Rev LEDs](../protocol/led-control.md) | Wheel type or attached module | col03 (modern) or col01 (legacy) |
| [Flag LEDs](../protocol/led-control.md) | Wheel type or attached module | col03 or col01 |
| [Button LEDs](../protocol/led-control.md#staged-button-led-reports-color--intensity) | Wheel type or attached module | col03 |
| [RevStripe](../protocol/led-control.md#revstripe) | Specific wheels only | col01 |
| [7-Segment Display](../protocol/display-7seg.md) | Wheel, hub, or module | col01 |
| [ITM Display](../protocol/display-itm.md) | Specific bases, wheels, or modules | col03 |
| [Tuning Menu](../protocol/tuning-menu.md) | All supported bases | col03 |
| [Clutch Bite Point](../protocol/clutch-bite-point.md) | Wheels/hubs with clutch paddles | col01 |

## Detailed References

- [Wheel Bases](wheel-bases.md) — All known wheelbase types, USB product IDs, and base capabilities
- [Steering Wheels & Hubs](steering-wheels.md) — Wheels (native capabilities) and hubs (native + module-extensible)
- [Button Modules](button-modules.md) — PBME, PBMR — capabilities and the compositional model
