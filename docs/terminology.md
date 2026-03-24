# Terminology

A reference glossary for Fanatec ecosystem concepts as used in this documentation and the FanaBridge codebase. Intended as a grounding reference for both human contributors and LLM agents.

---

## Hardware

### Wheelbase
The motor unit that connects to the host PC via USB. All HID communication flows through the wheelbase — it acts as the communication bridge between the host and all attached peripherals (wheels, hubs, modules). Provides force feedback. Identified by the `BASE_TYPE` enum. Examples: ClubSport DD+ (CSDDPlus), Podium DD2 (PDD2), CSL DD (CSLDD).

### Wheel
A self-contained steering wheel rim with a **passive** quick-release connection to the wheelbase. Contains built-in buttons and may include LEDs, displays, and encoders. A wheel's capabilities are **fixed** by its hardware — it cannot be extended. Wheels **cannot** accept button modules. Examples: ClubSport Formula V2 (CSWRFORMV2), Podium BMW M4 GT3 (PSWBMW), ClubSport BMW M3 GT2 V2 (CSSWBMWV2).

### Hub
A mounting platform with an **active** PCB/MCU and a quick-release connection to the wheelbase. Designed for attaching third-party or custom steering wheels. Has a USB-C interface for connecting a **button module**. A hub's effective capabilities are **compositional** — the combination of its own native features plus whatever module is attached. Examples: Podium Hub (PHUB), CSL Universal Hub (CSLUHUB), ClubSport Universal Hub V2 (CSUHV2).

### Button Module
An accessory that attaches to a **hub** (never a standalone wheel) via USB-C. Provides LEDs, displays, buttons, and encoders. The module's capabilities become the hub's effective capabilities. Identified by the `BUTTON_MODULE_TYPE` enum. See [Button Modules](devices.md#button-modules) for specific modules and their capabilities.

### Compositional Capability Model
The principle that a hub's effective capabilities are determined by the combination of its native features plus the attached module's features. This is not hardcoded to specific modules — if a new module were released, any compatible hub would gain its capabilities. See [Button Modules](devices/button-modules.md).

---

## Displays

### LED 7-Segment Display
A physical display using discrete LED segments to render 3 digits. Found on older wheels (e.g., CSSWRS, CSLRWRC, CSWRFORMV2). Controlled via the col01 7-segment protocol. Cannot display arbitrary graphics. Not ITM-capable.

### Small OLED
A ~1" dot-matrix OLED display. Found on CSLRMCL, CSLRMCLV1_1, CSLSWGT3, PSWBMW, and the PBMR module. Despite being physically capable of arbitrary rendering, all research to date indicates these are only addressable via the same col01 7-segment protocol used for LED 7-segment displays. Not ITM-capable.

### Large OLED
A 2.7" 256x64 OLED display. Found on the PBME module. Operates in two modes: **ITM mode** (telemetry dashboards via col03) and **legacy mode** (7-segment-style content via col01). See **Legacy Mode**.

### LCD
A larger graphical display. The Podium Bentley GT3 (PSWBENT) has a 3.4" 800x800 LCD. ITM-capable.

### ITM Display
Any display capable of showing multi-page telemetry dashboards via the col03 ITM protocol. Currently: PBME (2.7" OLED), PSWBENT (3.4" LCD), GTSWX (display type unconfirmed), and certain wheelbase displays (PDD1, PDD2). See [ITM Display Protocol](protocol/display-itm.md).

### Legacy Mode
The last ITM page on an ITM-capable display (page 6 for Base/BME/GTSWX, page 5 for Bentley). Renders 7-segment-style content (gear number, speed) and is addressed via the col01 7-segment protocol. This is not a separate protocol mode — it is an ITM page that the firmware uses when no telemetry data is being sent. Sometimes called "basic mode" in FanaBridge.

### Display Ownership
A mechanism (subcmd `0x18`) allowing the host to explicitly take or release control of an OLED display. When the firmware needs to show its own content (e.g., during CBP adjustment or tuning menu navigation), the host should release ownership. Currently only supported on the PBME. See [7-Segment Display — Display Ownership](protocol/display-7seg.md#display-ownership).

---

## LEDs

### Rev LEDs
RPM / shift indicator LEDs, typically a strip of 9 LEDs across the top of a wheel or module. Can be individually-addressable (per-LED on/off or per-LED RGB color) depending on the device. Not all wheels have them. See [LED Control Protocol](protocol/led-control.md).

### Flag LEDs
Status / warning indicator LEDs. Found on select wheels (CSWRFORMV2, CSSWF1ESV2, PSWBENT, GTSWX, CSSWFORMV3) and on the PBME/PBMR modules.

### Button LEDs
Per-button backlight LEDs. Found on some wheels (PSWBMW, GTSWX) and on both button modules. Use a staged commit protocol on col03 — color and intensity are sent separately, and changes only apply when a commit byte is set.

### RevStripe
A single-color LED strip (not individually-addressable) found on three specific wheels: CSLRP1X, CSLRP1PS4, CSLRWRC. Controlled as a single unit with RGB333 color encoding. Uses inverted enable semantics (`0x00` = on, `0x01` = off).

### Encoder LEDs
LEDs associated with rotary encoders on button modules. Controlled via the button intensity payload on col03. Part of the same staged commit mechanism as button LEDs.

---

## Color Encodings

### RGB565
16-bit color encoding: 5 bits red, 6 bits green, 5 bits blue. Stored big-endian in col03 reports. Used by the modern LED protocol for per-LED color on RGB-capable devices. ~65K colors.

### RGB555
15-bit color encoding: 5 bits per channel. Used by the PBMR for button LEDs. Slightly reduced color range vs RGB565 (green channel has 5 bits instead of 6).

### RGB333
9-bit color encoding: 3 bits per channel, packed across 2 bytes. Used by the legacy col01 LED protocol for RevStripe and global rev LED color. The SDK only exercises 8 discrete values (each channel fully on or fully off), but the hardware supports the full 512-color range. See [RGB333 Color Encoding](protocol/led-control.md#rgb333-color-encoding).

---

## HID Protocol

### col01
The legacy 8-byte HID collection. Used for: 7-segment display commands, legacy LED control, clutch bite point, display ownership, and the report trigger mechanism. All col01 output reports use the `[ReportID, 0xF8, 0x09, ...]` framing. The report ID is device-specific and assigned at initialization.

### col02
The 8-byte input HID collection. Carries button states, encoder positions, and axis values from device to host. Read by the OS HID driver and exposed as a standard game controller. Not directly controlled by the host.

### col03
The modern 64-byte HID collection. Used for: modern LED control (RGB565), ITM display commands, and tuning menu read/write. All col03 output reports start with `0xFF`. Not all devices support col03 — older wheelbases and wheels operate exclusively through col01.

### Collection Routing
The SDK routes reports based on the first byte of the output buffer: `0xFF` → col03 (64-byte write), anything else → col01 (8-byte write, first byte replaced with device report ID). This allows a single send path for both collections.

### Command Class
The second byte of a col03 report, identifying the protocol domain: `0x01` = LED control, `0x02` = ITM enable / analysis page, `0x03` = tuning menu, `0x05` = ITM display (page set, param defs, value updates, keepalive).

### Report Trigger
A general-purpose notification mechanism using col01 reports. Sends an ON/OFF pair: `[RID, F8, 09, 01, 06, FF, <SubId>, 00]` followed by `[RID, F8, 09, 01, 06, 00, 00, 00]`. SubId=1 is used for button module detection refresh, SubId=2 for CBP operations. See [Clutch Bite Point — Trigger Mechanism](protocol/clutch-bite-point.md#trigger-mechanism).

### Staged Commit
A protocol pattern where multiple data reports are sent without effect, then a final report with a commit flag (`0x01`) causes all pending changes to be applied atomically. Used by the col03 button LED protocol (color + intensity reports).

---

## Tuning & Configuration

### Tuning Menu
The wheelbase settings system (SEN, FF, SPR, DPR, etc.) controlled via col03 command class `0x03`. Uses a read-modify-write pattern — the device rejects writes that don't reflect the current state. See [Tuning Menu Protocol](protocol/tuning-menu.md).

### CBP (Clutch Bite Point)
The engagement threshold for analog clutch paddles. Uses a completely separate command path from the tuning menu — sent via col01 through the WheelCommand interface (not the TuningMenu interface). Value range 0–100. See [Clutch Bite Point Protocol](protocol/clutch-bite-point.md).

### APM (Advanced Paddle Mode)
A tuning parameter (struct offset 17) only relevant to wheels with a rotary encoder: CSLRMCL, CSWRFORMV2, CSLRMCLV1_1, CSLSWGT3. Zero on all other devices.

### Setup Slot
The tuning menu supports 5 setup slots (index 0–4). Each slot stores a complete set of tuning parameters. The active slot can be switched via the SELECT SETUP subcmd (`0x01`) or by writing `UserSetupIndex` in the tuning data.

---

## ITM (In-Tuning-Menu)

### ITM
The telemetry display system that shows multi-page dashboards on compatible displays. "In-Tuning-Menu" is the historical name from the SDK; in practice it is used for game telemetry, not just tuning. See [ITM Display Protocol](protocol/display-itm.md).

### ITM Device ID
A numeric identifier routing ITM commands to the correct display: 1 = wheelbase display (PDD1/PDD2), 2 = steering wheel / SmallOLED (disabled in SDK), 3 = PBME or GTSWX (shared, mutually exclusive), 4 = Bentley GT3 wheel.

### ParamDefs
Parameter definition reports (`FF 05 03 ...`) that tell the firmware what parameters will be displayed and in which slot positions. Required when using raw HID (FanaBridge approach). The SDK handles this internally.

### ValueUpdate
Parameter value reports (`FF 05 01 ...`) that send actual telemetry data for display. Each entry contains a handle, parameter ID, size, and value.

### Keepalive
A periodic packet (`FF 05 04 02 0B ...`) that must be sent every ~100ms to keep the ITM display alive.

### Page
An ITM display layout showing a specific set of telemetry parameters. Base/BME/GTSWX have 6 pages (page 6 = legacy), Bentley has 5 (page 5 = legacy). Pages contain SPEED and GEAR as persistent headers plus page-specific parameters.

### Subscription Table
The firmware's internal table of up to 16 parameter slots. In the SDK approach, the host queries this table to learn what parameters the firmware expects. In the raw HID approach (FanaBridge), the host defines the slots directly via ParamDefs.

---

## SDK Concepts

### STEERINGWHEEL_TYPE
The SDK enum identifying all wheels and hubs. Uses a single enum for both categories despite them being physically distinct device types. Values 0–26 (plus pseudo-entries like PBME=901). See [Steering Wheels & Hubs](devices/steering-wheels.md).

### BASE_TYPE
The SDK enum identifying wheelbase models. Values 1–14. See [Wheel Bases](devices/wheel-bases.md).

### BUTTON_MODULE_TYPE
The SDK enum identifying button modules: 0 = none, 1 = PBME, 2 = PBMR.

### QueryInterface
The native SDK's mechanism for obtaining typed interface objects from a device. Different type IDs return different interface classes (e.g., type 6 = wheel rev LEDs, type 7 = flag LEDs, type 0xB = tuning menu, type 0xD = wheel commands / CBP).

### Native SDK
The Fanatec C++ DLLs (`EndorFanatecSdk64_VS2019.dll`, `EndorFanatecSdkLibAdaptor.dll`) that construct HID reports and perform device I/O. This is the authoritative source for protocol behavior.

### Managed SDK
The C# wrapper layer (`EndorFanatecSdkDll.dll`, `FanatecLib.dll`) that provides P/Invoke declarations to the native SDK. May be from an older SDK version than the native DLLs.

### GameControlService
The Fanatec application-layer service (FanaLab backend). Handles game telemetry, LED animations, ITM page management, and tuning UI.
