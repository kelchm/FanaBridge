# Fanatec Device Reference

The Fanatec ecosystem consists of three main hardware categories:

- **Wheel bases** — The motor unit that connects to the PC via USB and provides force feedback. All HID communication flows through the wheelbase.
- **Steering wheels (rims)** — Attach to the wheelbase via the quick release. Contain buttons, LEDs, displays, and encoders. Identified by a `STEERINGWHEEL_TYPE` enum.
- **Button modules** — Optional modules that attach to certain rims (e.g., Podium Hub). Provide additional buttons, displays, and LEDs. Identified by a `BUTTON_MODULE_TYPE` enum.

## Device Identification

All Fanatec wheelbases share a common USB **Vendor ID**: `0x0EB7` (Endor AG).

The **Product ID** varies by wheelbase model. Within a session, the wheelbase reports the connected steering wheel type and button module type, which determine the available features and protocol capabilities.

### Identification Hierarchy

```
USB Device (VID=0x0EB7, PID=wheelbase-specific)
  └─ Wheelbase (BASE_TYPE enum)
      └─ Steering Wheel (STEERINGWHEEL_TYPE enum)
          └─ Button Module (BUTTON_MODULE_TYPE enum, optional)
```

The wheelbase acts as the communication hub — all HID reports are sent to/from the wheelbase, which routes commands internally to the attached peripherals.

## Feature Capabilities

Different hardware combinations support different feature sets:

| Feature | Depends On | Protocol |
|---------|-----------|----------|
| [Rev LEDs](../protocol/led-control.md) | Steering wheel type | col03 (modern) or col01 (legacy) |
| [Flag LEDs](../protocol/led-control.md) | Steering wheel or module type | col03 or col01 |
| [Button LEDs](../protocol/led-control.md#staged-button-led-reports-color--intensity) | Steering wheel or module type | col03 |
| [RevStripe](../protocol/led-control.md#revstripe) | Specific steering wheels | col01 |
| [7-Segment Display](../protocol/display-7seg.md) | Most steering wheels | col01 |
| [ITM Display](../protocol/display-itm.md) | Specific bases, wheels, or modules | col03 |
| [Tuning Menu](../protocol/tuning-menu.md) | All supported bases | col03 |
| [Clutch Bite Point](../protocol/clutch-bite-point.md) | Wheels with clutch paddles | col01 |

## Detailed References

- [Wheel Bases](wheel-bases.md) — All known wheelbase types, USB product IDs, and base capabilities
- [Steering Wheels](steering-wheels.md) — Complete steering wheel type list with LED, display, and protocol support
- [Button Modules](button-modules.md) — PBME, PBMR, and their capabilities
