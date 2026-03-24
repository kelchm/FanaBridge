# Wheel Bases

All Fanatec wheelbases connect to the host PC via USB and expose HID endpoints for control. The wheelbase handles force feedback and acts as the communication hub for attached steering wheels and button modules.

## Known Wheel Bases

| ID | Enum Name | Display Name | col03 Support | Tuning Menu | Base ITM |
|----|-----------|-------------|---------------|-------------|----------|
| 1 | CSWV2 | ClubSport Wheel Base V2 | Yes | Yes | No |
| 2 | CSWV25 | ClubSport Wheel Base V2.5 | Yes | Yes | No |
| 3 | CSLE_1_0 | CSL Elite Wheel Base 1.0 | Yes | Yes | No |
| 4 | CSLE_1_1 | CSL Elite Wheel Base 1.1 | Yes | Yes | No |
| 5 | CSLEPS4 | CSL Elite Wheel Base+ (PS4) | Yes | Yes | No |
| 6 | PDD1 | Podium Wheel Base DD1 | Yes | Yes | **Yes** |
| 7 | PDD1_PS4 | Podium Wheel Base DD1 (PS4) | Yes | Yes | **Yes** |
| 8 | PDD2 | Podium Wheel Base DD2 | Yes | Yes | **Yes** |
| 9 | GTDDPRO | GT DD PRO Wheel Base | Yes | Yes | No |
| 10 | CSLDD | CSL DD Wheel Base | Yes | Yes | No |
| 11 | CSDD | ClubSport DD Wheel Base | Yes | Yes | No |
| 12 | CSDDPlus | ClubSport DD+ Wheel Base | Yes | Yes | No |
| 13 | PDD25 | Podium Wheel Base DD | Yes | Yes | No |
| 14 | PDD25PLUS | Podium Wheel Base DD+ | Yes | Yes | No |

### USB Product IDs

| Product ID | Wheelbase |
|------------|-----------|
| `0x0005` | CSL Elite series |
| `0x0006` | ClubSport V2 / V2.5 |
| `0x0020` | ClubSport DD+ |
| Others | TBD — additional product IDs not yet cataloged |

> **Note:** The complete USB PID mapping is incomplete. The table above includes confirmed values.

## Base ITM Display

Only three wheelbases have a built-in ITM display:

- **PDD1** (Podium Wheel Base DD1)
- **PDD1_PS4** (Podium Wheel Base DD1 for PS4)
- **PDD2** (Podium Wheel Base DD2)

These use **Device ID 1** for ITM commands. See the [ITM display protocol](../protocol/display-itm.md) for details.

Other wheelbases (CSDD, CSDDPlus, GTDDPRO, CSLDD, etc.) do not have a base display, but ITM is still available through compatible steering wheels or button modules.

## col03 Capability

All current-generation wheelbases support col03 (64-byte reports). Whether col03 is actually used for a given session depends on the **steering wheel** attached — some older rims only support col01.

The wheelbase opens the col03 endpoint at initialization based on the connected wheel's device ID. See the [protocol overview](../protocol/overview.md#collection-routing) for the routing mechanism.
