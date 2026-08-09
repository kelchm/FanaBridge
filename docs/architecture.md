# Architecture

Rules and frozen names for the FanaBridge solution. Folder inventories and history live elsewhere; this page is the contract.

## Layers

Three source projects, layered by dependency:

| Project | Role |
|---------|------|
| **FanaBridge.Core** | SimHub-free device stack: HID transport, Fanatec protocol, profiles, LEDs, display encoding, tuning, diagnostics. References only non-SimHub libraries (HidSharp, Newtonsoft.Json). |
| **FanaBridge** (plugin) | SimHub / WPF shell: plugin entrypoint, device registry, Control Mapper bridge, settings, UI, display drivers. References Core and Updater. |
| **FanaBridge.Updater** | Isolated self-updater (release feed, download, file swap). References neither Core nor the plugin — audit boundary for code that rewrites files next to SimHub. |

**Reference rules:** Core and Updater must not reference SimHub assemblies or each other. The plugin may reference both. Packaging uses **ILRepack** to merge Core + Updater into the shipped **`FanaBridge.dll`** (development builds keep separate assemblies for layering).

## Namespace rule

```
namespace = project namespace root + relative folder path
```

| Project | Namespace root |
|---------|----------------|
| `src/FanaBridge.Core` | `FanaBridge.Core` |
| `src/FanaBridge` | `FanaBridge` |
| `src/FanaBridge.Updater` | `FanaBridge.Updater` |
| `tests/FanaBridge.Tests` | `FanaBridge.Tests` |

Enforced at compile time via **IDE0130** (`dotnet_style_namespace_match_folder = true`, severity error, `EnforceCodeStyleInBuild`). Deliberate exceptions use an in-file `#pragma warning disable IDE0130` with a one-line reason.

**Product exceptions:**

1. **`Log` in namespace `FanaBridge`** (`FanaBridge.Core/Logging/Log.cs`) — unqualified `Log.*` call sites in both Core and plugin resolve only when the type lives on the shared root ancestor.
2. **`ModuleInitializerAttribute`** (`FanaBridge/Properties/ModuleInitializerAttribute.cs`) — net48 polyfill; must live in `System.Runtime.CompilerServices` by definition.

XAML `x:Class` values follow the same path rule and are checked by a contract test (the C# analyzer does not cover markup).

## Test tree

`tests/FanaBridge.Tests` mirrors the product projects:

- `Core/` → `FanaBridge.Tests.Core…`
- `Plugin/` → `FanaBridge.Tests.Plugin…`
- `Updater/` → `FanaBridge.Tests.Updater…`
- `Contracts/` → repo-wide / external-contract tests (XAML layout, SimHub enum snapshot)

Domain-local fakes live next to their domain; multi-domain doubles live under `TestDoubles/`. A third layout exception lives in the test tree: the Control Mapper reflection shim (see `tests/FanaBridge.Tests/README.md` for conventions and details).

## Frozen names

These strings are invisible to the C# namespace analyzer but **breaking to rename** (SimHub persistence, embedded resources, dashboards, or the updater whitelist):

| Name | Why frozen |
|------|------------|
| **`FanaBridge.dll`** | Shipped assembly file name; updater package whitelist and install path. |
| **`FanaBridge.FanatecPlugin`** | Fully-qualified plugin type name persisted by SimHub. |
| **`Profiles` path segment** (under Core's embedded resources) | The built-in wheel-profile loader matches manifest names on a `.Profiles.` substring and `.json` suffix; keep profile JSON under a `Profiles/` folder. |
| **`FanatecPlugin.FanaBridgeSettings.json`** | On-disk settings file under SimHub `PluginsData/Common/`. |
| **`AttachDelegate` / `AddEvent` keys** | Property and event names registered with SimHub (`FanaBridge.*` properties, `DeviceConnected`, `DeviceDisconnected`, `WheelChanged`, …). Dashboards and automations bind to these strings. |
| **`Fanatec_<wheel>[_<module>]`** | `DeviceTypeID` format for SimHub device descriptors (and `Fanatec_Module_<module>` parents for hub logos). |
| **`FS_WHEEL_SWTYPE_<code>`** | Control Mapper variant ids (`FanaBridgeVariantProvider`); persisted in users' Control Mapper settings. |

Not frozen, for the record: SimHub's `ResolveCache.json` stores plugin/registry FQNs, but the cache is hash-invalidated whenever `FanaBridge.dll` changes on disk, so those entries rebuild on every update — cache state, not durable state.
