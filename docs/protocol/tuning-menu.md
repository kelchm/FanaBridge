# Tuning Menu Protocol

The tuning menu protocol controls all wheelbase settings (SEN, FF, damper, spring, etc.) via **col03 64-byte HID reports** using command class `0x03`.

## Command Structure

All tuning menu commands share this frame:

```
Byte:  [0]   [1]   [2]      [3]      [4..63]
       0xFF  0x03  subcmd   devId    payload (zero-padded to 64 bytes)
```

### Sub-commands

| Subcmd | Name | Direction | Description |
|--------|------|-----------|-------------|
| `0x00` | WRITE | Host → Device | Set tuning parameters (fire-and-forget) |
| `0x01` | SELECT SETUP | Host → Device | Switch active setup index (0–4) |
| `0x02` | READ | Host → Device | Request current state (triggers response) |
| `0x03` | SAVE | Host → Device | Persist current state to device flash |
| `0x04` | RESET | Host → Device | Restore factory defaults |
| `0x06` | TOGGLE | Host → Device | Toggle standard/simplified tuning mode |

A READ command triggers a response on the col03 IN endpoint with the current tuning state.

## Tuning Parameter Structure (64 bytes)

The `WHEEL_TUNING_MENU_DATA` structure is used in both READ responses and WRITE commands. It contains all tuning parameters at fixed offsets:

| Offset | Field | Type | Range | Description |
|--------|-------|------|-------|-------------|
| 0 | UserSetupIndex | byte | 0–4 | Active setup slot |
| 1 | SEN | byte | 0–255 | Steering Sensitivity |
| 2 | FF | byte | 0–255 | Force Feedback strength |
| 3 | SHO | byte | 0–255 | Shock / vibration intensity |
| 4 | BLI | byte | 0–255 | Brake Linearity |
| 5 | LIN | byte | 0–255 | Linearity (aliased as FFS in some SDK versions) |
| 6 | DEA | byte | 0–255 | Dead Zone |
| 7 | DRI | **sbyte** | -128–127 | Drift Mode (**signed** — the only signed field) |
| 8 | FOR | byte | 0–255 | Force |
| 9 | SPR | byte | 0–255 | Spring |
| 10 | DPR | byte | 0–255 | Damper |
| 11 | NDP | byte | 0–255 | Natural Damper |
| 12 | NFR | byte | 0–255 | Natural Friction |
| 13 | BRF | byte | 0–255 | Brake Force |
| 14 | BRG | byte | 0–255 | Brake Gain |
| 15 | FEI | byte | 0–255 | Force Effect Intensity |
| 16 | MPS | byte | 0–255 | Max Power Supply / Motor Protection |
| 17 | APM | byte | 0–255 | Advanced Paddle Mode (rotary wheels only) |
| 18 | INT | byte | 0–255 | Interactivity |
| 19 | NIN | byte | 0–255 | Natural Inertia |
| 20 | FUL | byte | 0–255 | Full Lock (steering angle) |
| 21 | BIL | byte | 0–255 | Bilateral / Balance |
| 22 | ROT | byte | 0–255 | Rotation |
| 23–63 | Reserved | byte | — | Always zero |

### Notes

- **DRI (offset 7)** is the only signed byte in the structure.
- **LIN vs FFS**: Two SDK struct variants exist. `WHEEL_TUNING_MENU_DATA` calls offset 5 `LIN`; `FS_WHEEL_TUNING_MENU_DATA` calls it `FFS`. Same byte position, same semantics.
- **APM (offset 17)**: Only populated on wheels with a rotary encoder — CSL Elite McLaren GT3, CSL Steering Wheel GT3, ClubSport Formula V2, and their revisions. Zero on all other wheels.
- Bytes 23–63 are reserved and always zero.

## READ vs WRITE Report Layout

**Critical:** READ responses and WRITE commands place the tuning data at **different byte offsets** within the 64-byte HID report.

### READ Command (Host → Device)

```
[FF 03 02 00 00 00 ... 00]     (64 bytes)
 │   │  │
 │   │  └─ subcmd = READ (0x02)
 │   └──── cmd class = 0x03
 └──────── report ID = 0xFF
```

### READ Response (Device → Host)

```
[FF 03 devId data[0] data[1] ... data[60]]     (64 bytes)
 │   │   │     │
 │   │   │     └── Tuning data starts at byte[3]
 │   │   └──────── device ID (e.g., 0x02)
 │   └──────────── cmd class = 0x03
 └──────────────── report ID = 0xFF
```

In the READ response, `UserSetupIndex` is at **byte[3]**, `SEN` at byte[4], `FF` at byte[5], etc.

### WRITE Command (Host → Device)

```
[FF 03 00 devId data[0] data[1] ... data[59]]     (64 bytes)
 │   │  │   │     │
 │   │  │   │     └── Tuning data starts at byte[4]
 │   │  │   └──────── device ID
 │   │  └──────────── subcmd = WRITE (0x00)
 │   └─────────────── cmd class = 0x03
 └─────────────────── report ID = 0xFF
```

In the WRITE command, `UserSetupIndex` is at **byte[4]**, `SEN` at byte[5], etc. — shifted by 1 byte relative to the READ response due to the subcmd byte.

### Conversion: READ → WRITE Buffer

```
writeBuf[0]     = 0xFF
writeBuf[1]     = 0x03
writeBuf[2]     = 0x00              // subcmd = WRITE
writeBuf[3]     = readBuf[2]        // device ID
writeBuf[4..63] = readBuf[3..62]    // tuning data (shifted by 1)
```

## Read-Modify-Write Pattern

The device **rejects WRITE commands** that don't reflect the current state. Always follow the read-modify-write pattern:

```
Step 1: Send READ
  → [FF 03 02 00 ... 00]

Step 2: Receive response
  ← [FF 03 02 XX YY ZZ ...]    (devId=0x02, data follows)

Step 3: Build WRITE buffer
  writeBuf = [FF 03 00 02 XX YY ZZ ...]
  (copy readBuf[2..62] → writeBuf[3..63])

Step 4: Modify desired fields
  e.g., writeBuf[4 + field_offset] = newValue

Step 5: Send WRITE
  → [FF 03 00 02 XX YY' ZZ ...]
```

### Example: Changing Force Feedback Strength

FF is at struct offset 2. In the WRITE buffer: `writeBuf[4 + 2] = writeBuf[6]`:

```
writeBuf[6] = 80;    // Set FF to 80
```

### Example: Switching Setup Slot

UserSetupIndex is at struct offset 0. In the WRITE buffer: `writeBuf[4]`:

```
writeBuf[4] = 2;     // Switch to setup slot 2
```

The dedicated SELECT SETUP subcmd (`0x01`) can also be used for this.

## Post-WRITE Behavior

The native SDK's tuning menu WRITE (`FSTuningMenu::PrivateDataSet`) sends a single col03 HID report and returns immediately — **no acknowledgment burst or trigger sequence is sent after a WRITE**.

The [report trigger mechanism](clutch-bite-point.md#trigger-mechanism) (subcmd `0x06`) is used only for CBP operations, not for tuning menu writes. The tuning menu has its own `DataReportTrigger` method (subcmd `0x06` within the col03 `0x03` command class), but this is part of the **READ** path — it requests the device to send back its current tuning state.

> **Note:** FanaBridge currently sends a burst of 4 ON/OFF trigger pairs after tuning writes. This was reverse-engineered from observed behavior and does not match the native SDK, which sends zero trigger pairs after a WRITE. The FanaBridge implementation should be considered experimental.

## WRITE Report Byte Map (Quick Reference)

| HID Byte | Content | Struct Offset | Field |
|----------|---------|---------------|-------|
| 0 | `0xFF` (report ID) | — | — |
| 1 | `0x03` (cmd class) | — | — |
| 2 | `0x00` (WRITE subcmd) | — | — |
| 3 | device ID | — | — |
| 4 | UserSetupIndex | 0 | Setup slot |
| 5 | SEN | 1 | Sensitivity |
| 6 | FF | 2 | Force Feedback |
| 7 | SHO | 3 | Shock |
| 8 | BLI | 4 | Brake Linearity |
| 9 | LIN/FFS | 5 | Linearity |
| 10 | DEA | 6 | Dead Zone |
| 11 | DRI (signed) | 7 | Drift |
| 12 | FOR | 8 | Force |
| 13 | SPR | 9 | Spring |
| 14 | DPR | 10 | Damper |
| 15 | NDP | 11 | Natural Damper |
| 16 | NFR | 12 | Natural Friction |
| 17 | BRF | 13 | Brake Force |
| 18 | BRG | 14 | Brake Gain |
| 19 | FEI | 15 | Force Effect Int. |
| 20 | MPS | 16 | Max Power Supply |
| 21 | APM | 17 | Adv Paddle Mode |
| 22 | INT | 18 | Interactivity |
| 23 | NIN | 19 | Natural Inertia |
| 24 | FUL | 20 | Full Lock |
| 25 | BIL | 21 | Bilateral |
| 26 | ROT | 22 | Rotation |
| 27–63 | `0x00` | 23–63 | Reserved |

## Clutch Bite Point

CBP is **not** part of the tuning data structure. It uses a completely separate command path via the WheelCommand interface. See [Clutch Bite Point Protocol](clutch-bite-point.md) for details.

When setting multiple tuning parameters at once, CBP should be sent **after** the col03 tuning data write, matching the ordering used by the official software.

## Live Change Notifications

The firmware supports event-driven tuning change notification — no polling required. Four event types are available:

| Event | Description |
|-------|-------------|
| TuningMenuDataChanged | Tuning parameters changed (user adjusted via wheel controls) |
| ITMPageChanged | ITM page subscription changed |
| AnalysisPageChanged | Analysis page updated |
| DeviceSettingsChanged | CBP / TorqueMode / MaxTorque / FFS changed |

## Wheel-Specific Notes

### APM (Advanced Paddle Mode) — Rotary Wheels Only

Only these steering wheels report APM via the tuning menu:

| Wheel | Internal ID |
|-------|-------------|
| CSL Elite McLaren GT3 | CSLRMCL |
| CSL Steering Wheel GT3 | CSLSWGT3 |
| CSL Elite McLaren GT3 V1.1 | CSLRMCLV1_1 |
| ClubSport Formula V2 | CSWRFORMV2 |

For all other wheels, APM is ignored (always zero).
