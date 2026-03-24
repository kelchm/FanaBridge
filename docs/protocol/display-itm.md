# ITM Display Protocol

The ITM (In-Tuning-Menu) protocol controls OLED and LCD displays found on select Fanatec steering wheels and button modules. It provides multi-page telemetry dashboards with parameters like speed, gear, lap times, tyre temperatures, and more.

All ITM commands use **col03 64-byte HID reports**.

## Supported Devices

ITM display support depends on the wheelbase, steering wheel, and button module combination:

| ITM Device | Detection | Device ID | Notes |
|------------|-----------|-----------|-------|
| **Base** | Wheelbase is PDD1, PDD1 (PS4), or PDD2 | 1 | Wheelbase's own display |
| **BME** | Button Module Endurance connected | 3 | PBME's large OLED |
| **Bentley** | Bentley GT3 steering wheel | 4 | Bentley wheel's built-in display |
| **GTSWX** | GT Steering Wheel X | 3 | GTSWX's built-in display |

> **Note:** BME and GTSWX share Device ID 3 on the wire. They are mutually exclusive — a setup will have one or the other, never both.

### Devices Without ITM Support

- **PBMR** (Podium Button Module Rally) — No ITM support. Only supports button LEDs and 7-segment display.
- **DD10/DD20** — Pass the initial ITM gate but have no base display. Require a hub with PBME attached, or a Bentley/GTSWX wheel for ITM.
- **CSDD / CSDDPlus / GTDDPRO / CSLDD** — Not in the official base ITM detection, but raw HID ITM commands work (bypassing the SDK check).

## Command Reference

### ITM Enable

Activates ITM mode on the wheel display. Uses command class `0x02` (not `0x05`):

```
FF 02 02 00 [00 x60]
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | `0xFF` | Report ID |
| 1 | `0x02` | Command class |
| 2 | `0x02` | Sub-command |
| 3 | `0x00` | Page (0 = default/reset) |

**Important:**
- Enable **resets the slot table** — ParamDefs must be re-sent after each Enable.
- Do not call Enable in a tight loop — rapid repeated calls can crash the PBME firmware.
- This is also used as `PageAnalysisSet(page)` — byte[3] can be 0–6 to set the analysis page.

### Keepalive

Continuous keepalive packet. Must be sent every ~100ms to keep the ITM display alive:

```
FF 05 04 02 0B [00 x59]
```

| Byte | Value | Description |
|------|-------|-------------|
| 0 | `0xFF` | Report ID |
| 1 | `0x05` | Command class |
| 2 | `0x04` | Sub-command (config) |
| 3 | `0x02` | Config type |
| 4 | `0x0B` | Config value |

### PageSet

Selects which ITM page is active on a specific display device:

```
FF 05 04 <deviceId> <page> [00 x59]
```

| Byte | Value | Description |
|------|-------|-------------|
| 3 | Device ID | Target display (1=Base, 3=BME/GTSWX, 4=Bentley) |
| 4 | Page number | Page to display (1–6, device-dependent) |

### ParamDefs (Parameter Definitions)

Defines the display slot layout — tells the firmware what parameters will be displayed and in which positions:

```
FF 05 03 <entries...> [00-padded to 64 bytes]
```

Each entry:

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 | Marker | Always `0x03` |
| 1 | 1 | Slot ID | Display layout identifier (e.g., `0x82`, `0x85`, `0x88`) |
| 2 | 1 | Position Lo | Low byte of position (typically `0x00`) |
| 3 | 1 | Position Hi | High byte of position (typically `0x00`) |
| 4 | 1 | Suffix Length | Number of suffix bytes following (0 = no suffix) |
| 5+ | N | Suffix Bytes | ASCII text appended after values (e.g., `2F 30` = "/0") |

**Example — two slots with suffix "/0":**
```
03 82 00 00 02 2F 30   ← slot 0x82, suffix "/0" (2 bytes: '/', '0')
03 83 00 00 02 2F 30   ← slot 0x83, suffix "/0"
```

The suffix system corresponds to the unit display feature in the SDK — the "/0" suffix likely represents the total denominator (e.g., "Lap 5 / 20").

### ValueUpdate

Sends actual telemetry values for display. Each entry contains a handle, parameter ID, and value:

```
FF 05 01 <entries...> [00-padded to 64 bytes]
```

Each entry:

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 | Marker | Always `0x01` |
| 1 | 1 | Handle | Parameter handle (assigned during ParamDefs) |
| 2–3 | 2 | Param ID | Parameter ID (little-endian) |
| 4 | 1 | Size | Value size in bytes (1, 2, or 4) |
| 5+ | N | Value | Parameter value (little-endian, size from above) |

## Parameter System

### Parameter IDs

The firmware recognizes a vocabulary of parameter IDs. Only a subset is confirmed to render correctly on current firmware — the rest may display no label, show unexpected formatting, or be silently ignored.

#### Vehicle Telemetry (1–84)

| ID | Name | Size | Type | Notes |
|----|------|------|------|-------|
| 1 | SPEED | 2 | Int16 LE | Present on all pages as header |
| 2 | RPM | 4 | Int32 | |
| 3 | RPM_MAX | 4 | Int32 | |
| 4 | GEAR | 1 | Uint8 | Present on all pages as header |
| 5 | FUEL | 4 | Float32 LE | Supports total (FUEL_MAX) |
| 6 | FUEL_MAX | 4 | Float32 LE | |
| 7 | FUEL_PER_LAP | 4 | Float32 LE | |
| 9 | ERS_LEVEL | 4 | Int32 LE | Percentage |
| 14 | DRS_ZONE | 1 | Uint8 | 0 or 1 |
| 15 | DRS_ACTIVE | 1 | Uint8 | 0 or 1 |
| 18 | ABS_SETTING | 1 | Uint8 | |
| 20 | TC_SETTING | 1 | Uint8 | |
| 25 | BRAKE_BIAS | 4 | Float32 LE | Must be 4 bytes — values >= 127 can crash PBME firmware |
| 26 | ENGINE_MAPPING | 1 | Uint8 | |
| 33 | OIL_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 42 | TYRE_FL_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 45 | TYRE_FR_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 48 | TYRE_RL_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |
| 51 | TYRE_RR_C_TEMP | 1 | Uint8 | Unit-converted (C/F) |

#### Race / Timing (501–536)

| ID | Name | Size | Type | Notes |
|----|------|------|------|-------|
| 501 | POSITION | 1 | Uint8 | Supports total (POSITION_TOTAL) |
| 505 | LAP | 1 | Uint8 | Supports total (LAP_TOTAL) |
| 509 | LAP_TIME | 4 | Float32 LE | Current lap, seconds |
| 510 | LAST_LAP_TIME | 4 | Float32 LE | |
| 511 | BEST_LAP_TIME | 4 | Float32 LE | |
| 516 | DELTA_OWN_BEST | 4 | Float32 LE | Seconds, +/- |
| 519 | CAR_AHEAD | 4 | Float32 LE | Gap in seconds |
| 520 | CAR_BEHIND | 4 | Float32 LE | Gap in seconds |

#### Additional Defined Parameters

The full parameter vocabulary includes 120+ IDs covering tyre pressures, brake temps, G-forces, pedal positions, flags, system metrics (CPU/GPU), and more. These are defined in the firmware's shared vocabulary but are **not confirmed to render** on all display types. The complete `FS_ITM_PARAM_ID` enum ranges:

- 0–84: Vehicle telemetry
- 501–536: Race/timing data and flags
- 1001–1008: System metrics (CPU load, GPU temp, FPS, etc.)
- 65535 (`0xFFFF`): UNSUBSCRIBE sentinel

### Unit System

Parameters can have associated display units:

| Value | Unit | Category |
|-------|------|----------|
| 0 | DEFAULT | No suffix |
| 1 | SPEED_KPH | Speed |
| 2 | SPEED_MPH | Speed |
| 4 | VOLUME_LITER | Volume |
| 5 | VOLUME_GALLON | Volume |
| 7 | TIME_SEC | Time |
| 8 | TEMP_C | Temperature |
| 9 | TEMP_F | Temperature |
| 17 | TOTAL_POSITION | Total companion |
| 18 | TOTAL_LAP | Total companion |
| 19 | TOTAL_CLASS | Total companion |
| 22 | OTHERS_PERCENT | Percentage |

"Total" units enable `value / total` display (e.g., "Lap 5 / 20"). The SDK submits units via `ItmUnitSet()` on every tick; the raw HID equivalent maps to the suffix mechanism in ParamDefs.

## Page Layouts

SPEED and GEAR appear on **every page** as persistent header fields.

### Base / BME Pages (1–6)

Base and BME use identical page layouts. Page 6 is the legacy/default fallback.

#### Page 1 — Lap Info

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 505 | LAP | 1 | Uint8 |
| 501 | POSITION | 1 | Uint8 |
| 509 | LAP_TIME | 4 | Float32 LE |
| 510 | LAST_LAP_TIME | 4 | Float32 LE |

Detection signature: LAP (505) present in subscription.

#### Page 2 — Fuel / ERS / DRS

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 5 | FUEL | 4 | Float32 LE |
| 9 | ERS_LEVEL | 4 | Int32 LE |
| 14 | DRS_ZONE | 1 | Uint8 |
| 15 | DRS_ACTIVE | 1 | Uint8 |
| 516 | DELTA_OWN_BEST | 4 | Float32 LE |

Detection signature: ERS_LEVEL (9).

#### Page 3 — Car Settings

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 20 | TC_SETTING | 1 | Uint8 |
| 18 | ABS_SETTING | 1 | Uint8 |
| 25 | BRAKE_BIAS | 4 | Float32 LE |
| 26 | ENGINE_MAPPING | 1 | Uint8 |
| 33 | OIL_TEMP | 1 | Uint8 |

Detection signature: OIL_TEMP (33).

> **Warning:** BRAKE_BIAS values >= 127 have been observed to crash PBME firmware. Rapid value cycling on this page can also cause firmware lockups.

#### Page 4 — Lap Times

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 510 | LAST_LAP_TIME | 4 | Float32 LE |
| 511 | BEST_LAP_TIME | 4 | Float32 LE |
| 519 | CAR_AHEAD | 4 | Float32 LE |
| 520 | CAR_BEHIND | 4 | Float32 LE |

Detection signature: CAR_AHEAD (519).

#### Page 5 — Tyre Temps

| Param ID | Name | Size | Type |
|----------|------|------|------|
| 1 | SPEED | 2 | Int16 LE |
| 4 | GEAR | 1 | Uint8 |
| 42 | TYRE_FL_C_TEMP | 1 | Uint8 |
| 45 | TYRE_FR_C_TEMP | 1 | Uint8 |
| 48 | TYRE_RL_C_TEMP | 1 | Uint8 |
| 51 | TYRE_RR_C_TEMP | 1 | Uint8 |

Detection signature: TYRE_FL_C_TEMP (42).

#### Page 6 — Legacy / Default

No telemetry parameters. Fallback page when ITM is inactive.

### Bentley Pages (1–5)

Bentley uses the same parameters but with **no Car Settings page** and only 5 pages total:

| Page | Content | Detection |
|------|---------|-----------|
| 1 | Lap Info | LAP (505) |
| 2 | Fuel / ERS / DRS | ERS_LEVEL (9) |
| 3 | Lap Times | CAR_AHEAD (519) |
| 4 | Tyre Temps | TYRE_FL_C_TEMP (42) |
| 5 | Legacy / Default | — |

### GTSWX Pages (1–6)

GTSWX uses the same page content as Base/BME but with **multi-parameter detection signatures** — all params in the group must be present before the page is matched:

| Page | Content | Detection (ALL required) |
|------|---------|-------------------------|
| 1 | Lap Info | LAP + POSITION + LAP_TIME + LAST_LAP_TIME |
| 2 | Fuel / ERS / DRS | FUEL + ERS_LEVEL + DRS_ZONE + DRS_ACTIVE + DELTA_OWN_BEST |
| 3 | Car Settings | TC_SETTING + ABS_SETTING + ENGINE_MAPPING + OIL_TEMP + BRAKE_BIAS |
| 4 | Lap Times (compact) | LAST_LAP_TIME + BEST_LAP_TIME + CAR_AHEAD (no CAR_BEHIND) |
| 5 | Tyre Temps | Any one of: TYRE_FL/FR/RL_C_TEMP |
| 6 | Legacy / Default | — |

### Slot & Handle Mapping (Raw HID)

When using raw HID (bypassing the SDK), displays require explicit slot configuration via ParamDefs:

| Page(s) | Slot IDs | Handle Range | Suffix |
|---------|----------|--------------|--------|
| 1, 5 | 0x82, 0x83, 0x84, 0x85 | 0–5 | "/0" (Page 1 only) |
| 2, 4 | 0x88 | 6–12 | "/0" (Page 2 only) |
| 3 | 0x85 | 0–5 + 13 | none |

Maximum subscribed parameters per device: **16**.

## Timing & Rate Limiting

### Parameter Update Intervals

Not all parameters need to be sent at full rate. Recommended minimum intervals:

| Category | Delay (ms) | Parameters |
|----------|-----------|------------|
| Real-time | 0 | SPEED, GEAR, POSITION, LAP, LAST_LAP_TIME, BEST_LAP_TIME, BRAKE_BIAS, FUEL, DRS, ABS, TC |
| Near real-time | 30 | LAP_TIME |
| Moderate | 40–50 | RPM_MAX, TYRE temps |
| Low-frequency | 100 | ENGINE_MAPPING, ERS, others |

### Page Changes

Page change commands should be spaced at least 100ms apart. The firmware needs time to reconfigure its internal display state between pages.

## Control Model: SDK vs Raw HID

There are two fundamentally different approaches to controlling the ITM display:

### SDK Approach (Firmware-Driven)

The firmware maintains a subscription table of up to 16 parameter slots. The host queries which parameters the firmware expects for the current page, then populates them:

1. Firmware owns the page layout
2. Host calls `ItmSubscribedParamsGet()` to learn what params are needed
3. Host sends values for subscribed params only
4. Page content is fixed by firmware

### Raw HID Approach (Host-Driven)

The host bypasses the SDK and directly defines the display layout via ParamDefs:

1. Host sends PageSet — tells firmware which page
2. Host sends ParamDefs — tells firmware the slot layout
3. Host sends ValueUpdate — sends actual data
4. Host can potentially choose which params appear on each page

The raw HID approach gives more flexibility but requires knowing the page layouts in advance and carries the risk that untested parameter IDs may not render correctly.

## Automatic Page Changes (Alerts)

The official software supports automatic page switching based on telemetry events:

### Value-Change Triggers

| Trigger | Target Page |
|---------|-------------|
| Lap number or Position changed | Page 1 (Lap Info) |
| DRS zone or DRS active changed | Page 2 (Fuel/ERS/DRS) |
| TC, ABS, EngineMap, or BrakeBias changed | Page 3 (Car Settings) |
| Best lap time changed | Page 4 (Lap Times) |

### Threshold Triggers

| Trigger | Target Page |
|---------|-------------|
| Fuel below threshold | Page 2 |
| ERS below threshold | Page 2 |
| Oil temp above threshold | Page 3 |
| Any tyre temp out of range | Page 5 |

### Favorite Page

Each device has a configurable favorite page with a display duration (default 10 seconds, range 3–60 seconds). After a trigger-caused page change, the display reverts to the favorite page after the duration expires.
