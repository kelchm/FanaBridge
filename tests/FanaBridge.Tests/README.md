# FanaBridge.Tests

## Layout

The test tree mirrors the three product projects (+ this test project):

| Folder | Source project | Product namespace root | Test namespace root |
|--------|----------------|------------------------|--------------------|
| `Core/` | `src/FanaBridge.Core` | `FanaBridge.Core` | `FanaBridge.Tests.Core` |
| `Plugin/` | `src/FanaBridge` | `FanaBridge` | `FanaBridge.Tests.Plugin` |
| `Updater/` | `src/FanaBridge.Updater` | `FanaBridge.Updater` | `FanaBridge.Tests.Updater` |
| `Contracts/` | (repo / external contracts) | — | `FanaBridge.Tests.Contracts` |

Under each product root, domain folders match the corresponding source project's layout (e.g. `Core/Devices/Identity/`, `Plugin/Display/Drivers/`). Product C# namespaces are the project directory root plus the relative folder path, **compiler-enforced** via IDE0130 (`dotnet_style_namespace_match_folder` + `EnforceCodeStyleInBuild`). Deliberate exceptions disable IDE0130 in-file with a one-line reason (`Log.cs`, `ModuleInitializerAttribute.cs`, ControlMapper test shims). Tests keep `FanaBridge.Tests.<Root>.<Domains>` declarations.

## Contracts/

Repo-wide or external-contract subjects, not owned by a single product domain:

- `XamlClassLayoutTests.cs` — every `x:Class` under `src/` must match project namespace root + relative folder (XAML has no IDE0130).
- `SimHubEnumSnapshotTests.cs` — reflects `SimHub.FanatecManaged.dll` and compares its public enums to the committed snapshot under `Snapshots/` (fixture folder stays at the test project root).

## TestDoubles/

Shared home for cross-domain test doubles. A double used by tests of **exactly one** domain lives next to that domain (namespace follows path). Genuinely multi-domain doubles stay here under `FanaBridge.Tests.TestDoubles`.

Current residents:

- `FakeReportStream` — transport stream fake used across Core and Plugin domains.
- `FakeLedModuleHost` — LED module host seam used by Plugin Devices and Settings tests.

Domain-local (not here):

- `Plugin/ControlMapper/ControlMapperFakes.cs` — Control Mapper reflection shims; only the ControlMapper bridge tests use them. Its helper types live in the mapped `FanaBridge.Tests.Plugin.ControlMapper` namespace; only the SimHub type-name shim (`SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin`) is a frozen reflection target, with IDE0130 disabled narrowly around that block.

## Fixtures

Repository-owned fixture files read by tests are tracked under the test tree, organized by the domain that owns the subject (or at the root for repo-wide contracts — e.g. `Snapshots/` for the SimHub enum guard). Prefer `CopyToOutputDirectory` in the csproj over ad-hoc path probing when a fixture must be available next to the test assembly.
