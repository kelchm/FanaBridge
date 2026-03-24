# Clutch Bite Point (CBP) Protocol

The Clutch Bite Point (CBP) controls the engagement threshold for analog clutch paddles. CBP uses a **completely separate command path** from the [tuning menu](tuning-menu.md) — it is not part of the tuning parameter structure.

## Set CBP

Sends a CBP value to the device via an 8-byte col01 report:

```
[ReportID, 0xF8, 0x09, 0x01, 0x17, 0x01, <CBP>, 0x00]
                              │     │     │
                              │     │     └─ CBP value (0–100, clamped)
                              │     └─────── Enable flag (always 0x01)
                              └───────────── CBP subcmd identifier
```

The value is clamped to the range 0–100.

After sending the set command, the [trigger sequence](#trigger-mechanism) must be executed:

```
Complete Set CBP sequence:
1. Send: [RID, F8, 09, 01, 17, 01, <CBP>, 00]   ← Set CBP value
2. Send: [RID, F8, 09, 01, 06, FF, 02,    00]   ← Trigger ON (SubId=2)
3. Send: [RID, F8, 09, 01, 06, 00, 00,    00]   ← Trigger OFF
4. Wait: 100ms                                    ← Firmware processing time
```

## Get CBP

Reading the CBP value requires triggering the firmware to report its current state, then reading the value after a delay:

```
Complete Get CBP sequence:
1. Send: [RID, F8, 09, 01, 06, FF, 02, 00]   ← Trigger ON (SubId=2)
2. Send: [RID, F8, 09, 01, 06, 00, 00, 00]   ← Trigger OFF
3. Wait: 100ms                                  ← Wait for firmware response
4. Read: CBP value from device input report
```

The 100ms delay is necessary because the firmware sends a response via the input handler. The official SDK reads the resulting value from a Windows registry key where the filter driver stores it.

## Trigger Mechanism

The trigger mechanism is a general-purpose notification system used by several commands. It sends a pair of col01 reports — an ON followed by an OFF:

### Report Format

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

### SubId Values

| SubId | Purpose | Used By |
|-------|---------|---------|
| 0 | Cancel / off | Always sent as the second half of a pair |
| 1 | Button module detection refresh | BME refresh operations |
| 2 | **CBP data request / notification** | **SetClutchBitePoint / GetClutchBitePoint** |

SubId=2 tells the firmware either "I just set CBP" (after Set) or "please report the current CBP" (before Get). The firmware responds with a HID input report that the input handler processes.

### Important: Single Pair + Delay

The correct sequence is **one ON/OFF pair** followed by a **100ms sleep**. The sleep gives the firmware time to process the trigger and send its response.

## Integration with Tuning Menu

CBP is always sent **after** tuning data when both are being set:

```
1. Send tuning data via col03  (FF 03 00 ...)    ← WRITE command
2. Send CBP via col01          (RID F8 09 01 17 ...)
3. Send trigger pair           (SubId=2)
4. Wait 100ms
```

This ordering matches the official software's behavior, where `TuningMenuDataSet` is called first, followed by `ClutchBitePointSet`.

## Display Interaction

The SubId=2 trigger can cause the firmware to momentarily display the CBP value on the 7-segment display. This is by design — the firmware uses the trigger as a signal to show the CBP feedback to the user.

During this display period, sending [7-segment display](display-7seg.md) commands will conflict with the firmware's CBP display, causing visible flickering. The 100ms delay helps, but the firmware may hold the CBP display for longer.

For OLED-equipped devices (e.g., PBME), the display ownership can be explicitly yielded to the firmware during CBP operations using the [SevenSegmentModeEnable](display-7seg.md#display-ownership) command. For non-OLED devices, display suspension must be managed by the host software.

## Prerequisites

- The connected steering wheel rim must have clutch paddles. The official SDK checks `HasWheelRimClutchPaddles` before allowing CBP operations.
- CBP operations should not be performed during active display updates to avoid flickering.
