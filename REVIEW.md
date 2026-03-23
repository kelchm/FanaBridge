# FanaBridge Code Review

## Overview

FanaBridge is a SimHub plugin (C#/.NET Framework 4.8) that provides LED and
7-segment display control for Fanatec racing wheel hardware via USB HID. The
codebase is approximately 3,500 lines of C# across ~30 source files, organized
into clean architectural layers: **Transport** (HID I/O), **Protocol** (wire
encoding), **Profiles** (device capabilities from JSON), **Adapters** (SimHub
integration), and **UI** (WPF settings panels).

**Overall impression**: This is a well-structured, cleanly-layered codebase with
good separation of concerns, thoughtful dirty-tracking optimizations, and solid
interface-based testability. The code quality is notably high for a beta plugin.
The findings below are refinements, not indications of fundamental problems.

---

## Architecture & Design

### Strengths

- **Clean layering**: Transport → Protocol → Adapters → UI. Dependencies flow
  one way. Protocol classes depend on `IDeviceTransport`, not on concrete HID
  types.
- **Interface-based testability**: `IDeviceTransport`, `IDeviceConnection`, and
  `ISdkConnection` allow the `ConnectionMonitor` and `FanatecTuningController`
  to be tested with hand-written stubs — and the tests are good.
- **Profile-driven architecture**: Hardware capabilities come from JSON profiles,
  making it easy to add new wheels without code changes. The
  `WheelProfileStore` snapshot-and-swap pattern is a solid approach to thread
  safety.
- **Zero-allocation hot path**: Both `LedEncoder` and `DisplayEncoder` use
  pooled buffers, and `FanatecLedDriver` uses ping-pong double-buffering. No
  per-frame heap allocations on the render path.
- **Dirty tracking**: `LedEncoder` skips redundant HID writes when LED state
  hasn't changed, avoiding unnecessary USB traffic.

### Concerns

1. **Singleton pattern** (`FanatecPlugin.Instance`): The plugin uses a static
   singleton so `DeviceInstance` wrappers can access shared hardware. This is
   pragmatic for SimHub's architecture, but it creates tight coupling and makes
   the adapters layer harder to test in isolation. **Recommendation**: Document
   the design rationale and accept this as a SimHub-imposed constraint.

2. **`WheelProfileStore` is fully static**: All state is in static fields, which
   makes it impossible to test in isolation or run multiple instances.
   `Reload()` has a subtle TOCTOU race: two threads could both see `_snapshot`
   as null and both call `Reload()`. The `Interlocked.Exchange` at the end
   prevents corruption, but wasted work could occur. **Impact**: Low — this
   only runs at startup.

3. **`BeginBatch()` is not re-entrant despite the doc comment**: The
   `IDeviceTransport` documentation says "Re-entrant: sends made inside a batch
   skip re-acquiring the lock", but `SendCol03`/`SendCol01` on `FanatecDevice`
   always call `lock (_writeLock)`, which would deadlock if called inside a
   `BeginBatch()`. In practice this doesn't happen because the protocol classes
   call the private `SendLedReport`/`SendDisplayReport` methods directly when
   inside a batch — but the doc comment is misleading.
   **EDIT**: Actually, looking more carefully, `SendCol03` and `SendCol01` are
   explicit interface implementations that always `lock`, while `BeginBatch`
   uses `Monitor.Enter`. Since `Monitor` in .NET is re-entrant (same thread can
   acquire the same lock multiple times), this actually works correctly. The doc
   comment is accurate. No issue here.

---

## Potential Bugs & Logic Issues

### Medium Priority

4. **`FanatecLedDriver.SendLeds` race on `_refreshTask`**: The field
   `_refreshTask` is read and written from both the SimHub frame thread and the
   Task.Run callback without synchronization. The `finally { _refreshTask = null; }`
   inside the Task could race with the null check on the next frame. In practice
   this is likely benign (worst case: one extra frame dropped), but it would be
   cleaner to use `Interlocked.CompareExchange` or a volatile field.

   ```csharp
   // Current (racy):
   _refreshTask = Task.Run(() => { ... finally { _refreshTask = null; } });

   // Safer:
   private volatile Task _refreshTask;
   ```

5. **`DisplayEncoder._reportBuf` is shared and not thread-safe**: The pooled
   `_reportBuf` is reused across calls without locking. If `SetDisplay` were
   ever called from two threads simultaneously, the buffer could be corrupted.
   Currently this is safe because all display updates flow through the single
   SimHub frame thread, but the class doesn't document this constraint.

6. **`FanatecDevice.Disconnect()` calls both `Close()` and `Dispose()` on
   streams**: `Close()` and `Dispose()` are equivalent on `HidStream` (and most
   .NET streams). Calling both is harmless but unnecessary — just `Dispose()` is
   sufficient.

### Low Priority

7. **`WheelProfile` LINQ-computed properties**: `RevLedCount`, `FlagLedCount`,
   etc. all call `.Count(predicate)` on each access, which iterates the full LED
   list. These are called repeatedly from `WheelCapabilities` constructor,
   `FanatecLedDriver`, etc. Consider caching the counts.

8. **`SevenSegment` duplicate encodings**: `H` and `X` are both `0x76`, and `S`
   and `Digit5` are both `0x6D`. This is inherent to 7-segment displays (X
   looks like H, S looks like 5), but worth a comment.

---

## Thread Safety

9. **`FanatecPlugin.StateChanged` event**: This is invoked from multiple
   contexts (connection monitor, SDK wheel-change callback) and subscribers
   include UI code. The event itself is not protected against concurrent
   add/remove. Consider using `Interlocked` or making it a proper event with
   lock-protected accessor, or document that subscribers must be added during
   `Init()` only.

10. **`FanatecPlugin.WizardActive`**: This `bool` is set from the UI thread and
    read from the SimHub data thread. It should be `volatile` to ensure
    visibility across threads.

---

## Resource Management

11. **`FanatecDevice.Connect` resource leak on partial failure**: If `_ledStream`
    opens successfully (line 135) but a later exception occurs before the method
    returns `true`, the method returns `false` without calling `Disconnect()`.
    The `Disconnect()` call at the top of `Connect()` only handles pre-existing
    connections, not partial failures in the current attempt. The leaked
    `_ledStream` would be orphaned.

12. **`FanatecSdkManager.TrySdkConnect` leaks `WheelInterface` on failure**:
    When `wi.Connect(productId)` returns `false`, the `WheelInterface` instance
    is abandoned without calling `Release()`. If `WheelInterface` holds
    unmanaged SDK resources, they leak.

13. **`CancellationTokenSource` and `ManualResetEventSlim` in wizard never
    disposed**: `_probeCts`, `_blinkCts`, and `_blinkDone` in
    `WheelProfileWizardDialog` are cancelled/reset but never disposed. These
    implement `IDisposable` and hold `WaitHandle`s that should be released.
    Over multiple wizard sessions this could leak OS handles.

14. **`FanatecPlugin.End()` disposes `_sdk` and `_device` but doesn't dispose
    `_connectionMonitor`, `_tuning`, `_leds`, or `_display`**: These don't
    implement `IDisposable`, so there's nothing to dispose. **Impact**: None —
    the plugin lives for the process lifetime.

---

## Test Coverage

### Current State (Good)

- **`ConnectionMonitorTests`**: 14 tests covering connect/disconnect, reconnect
  cooldowns, heartbeat checks, force-reconnect. Excellent coverage.
- **`FanatecTuningControllerTests`**: 11 tests covering the read-modify-write
  protocol, error paths, ack burst structure.
- **`SevenSegmentTests`**: Covers digit/char lookup and unknown fallback.
- **`ColorHelperTests`**: Covers RGB565 bit-packing for primary colors.

### Coverage Gaps

| Component | Risk | Recommendation |
|-----------|------|----------------|
| `LedEncoder` | High | Pure-logic protocol encoder with dirty tracking. Highly testable, critical to correctness. |
| `DisplayEncoder` | Medium | Simple but testable — verify display modes, text wrapping, dot-folding. |
| `WheelProfileStore` | Medium | JSON loading, profile resolution, override keys. Important for correctness. |
| `WheelProfile` computed properties | Low | LINQ-based counts could be verified. |
| Adapters (`FanatecLedDriver`, etc.) | Low | Hard to test without SimHub mocks; focus on protocol layer instead. |

---

## Build & CI

13. **Missing `LangVersion` in test project**: The main project specifies
    `<LangVersion>8.0</LangVersion>` but the test project doesn't. This could
    cause inconsistent C# feature availability depending on SDK version.

14. **No code coverage reporting**: No Coverlet package or coverage collection in
    CI. Adding `--collect:"XPlat Code Coverage"` to the test step would provide
    visibility.

15. **`build-install-archive.ps1` uses `--no-restore`**: Will fail on a clean
    checkout without a preceding `dotnet restore`.

16. **SimHub download URL fragility**: CI downloads SimHub from a GitHub release
    URL. If the release is removed or the URL structure changes, CI breaks with
    no fallback.

17. **CHANGELOG disconnected from GitHub Releases**: `release.yml` uses
    `--generate-notes` which auto-generates from commits, ignoring the curated
    `CHANGELOG.md`.

---

## Transport Concerns

15. **`ReadCol03` holds the write lock during reads**: In `FanatecDevice`, the
    `ReadCol03` method acquires `_writeLock` before reading. Tuning reads can
    time out at up to 1000ms, during which all LED and display writes are
    blocked. This could cause visible LED flickering or stuttering on wheels
    with tuning features enabled.

16. **`FanatecTuningController` drain loop has no iteration cap**: The
    `while (_transport.ReadCol03(buf, DRAIN_TIMEOUT_MS) >= 0) { }` loop in
    `ReadTuningState` has no maximum iteration count. If the device keeps
    producing reports, the drain could run indefinitely (each iteration bounded
    by 50ms timeout, but no total limit).

---

## Dead Code & Unused Settings

17. **`MaxUpdateRateHz` is declared but never consumed**: The
    `FanatecPluginSettings.MaxUpdateRateHz` property exists with a default of
    60 but no code reads it to throttle updates.

---

## UI Layer

18. **`TuningSettingsPanel` hardcoded magic offset**: Line 75 uses `rawDump[18]`
    instead of referencing `FanatecTuningController.READ_ENCODER_MODE_OFFSET`.
    If the constant changes, the UI silently reads the wrong byte.

19. **`SyncFromDevice` blocks the UI thread**: `tuning.ReadTuningStateRaw()`
    performs HID I/O with up to 1-second timeout on the UI dispatcher thread.
    This could freeze the UI briefly.

20. **Wizard `Process.Start` for file explorer lacks error handling**: No
    try/catch around `Process.Start(userDir)` in `SettingsControl.xaml.cs`. If
    the path is invalid, this throws unhandled.

---

## Code Quality Observations

### Positive

- Consistent naming conventions throughout
- XML doc comments on all public APIs
- Good use of `const` for protocol magic numbers
- Defensive null checks with `ArgumentNullException` in constructors
- Sensible logging at appropriate levels (Info, Warn, Error)
- Null-object pattern in `WheelCapabilities.None`
- JSON schema for wheel profiles with strict validation

### Minor Style Notes

- `ColorHelper` method name `RgbToRgb565` is accurate but the doc comment
  explains it produces BGR565 byte order — consider renaming to `RgbToBgr565` or
  keeping the current name with a prominent comment (current approach is fine).
- `DisplaySettings.DisplayMode` uses string-based dispatch instead of an enum.
  This works but is less type-safe.
- Some switch statements in `SevenSegment` and `DisplayEncoder` could use lookup
  arrays, but the current approach is clear and the methods aren't hot-path.

---

## Security

18. **P/Invoke `CreateFile` in `FanatecDevice`**: Opens HID device paths with
    `GENERIC_READ | GENERIC_WRITE`. The device path comes from HidSharp's device
    enumeration (filtered by Fanatec VID), not user input, so there's no path
    injection risk. This is appropriate for a hardware driver.

19. **JSON deserialization in `WheelProfileStore`**: Uses `JsonConvert.DeserializeObject<WheelProfile>`,
    which is safe because the target type has no dangerous deserialization
    callbacks and the JSON comes from trusted sources (embedded resources or
    local disk files the user explicitly created).

20. **`PopulateObject` in `SetSettings`**: The `JsonConvert.PopulateObject` call
    operates on a `JToken.ToString()` intermediate. The source is SimHub's
    settings system, which is trusted. No concern.

---

## Summary of Recommendations

### Should Fix
1. Add `volatile` to `_refreshTask` in `FanatecLedDriver` (item 4)
2. Add `volatile` to `WizardActive` in `FanatecPlugin` (item 10)
3. Add `<LangVersion>8.0</LangVersion>` to `FanaBridge.Tests.csproj`
4. Fix `FanatecDevice.Connect` partial failure resource leak (item 11)
5. Fix `FanatecSdkManager.TrySdkConnect` `WheelInterface` leak (item 12)

### Should Consider
6. Add tests for `LedEncoder` and `DisplayEncoder` — highest-value untested
   components
7. Add code coverage reporting to CI
8. Fix `build-install-archive.ps1` `--no-restore` issue
9. Dispose `CancellationTokenSource`/`ManualResetEventSlim` in wizard (item 13)
10. Reference `READ_ENCODER_MODE_OFFSET` constant in TuningSettingsPanel (item 18)
11. Remove or implement `MaxUpdateRateHz` dead setting (item 17)

### Nice to Have
12. Cache computed LED counts in `WheelProfile` (item 7)
13. Add `WheelProfileStore` tests for profile resolution logic
14. Use CHANGELOG content in GitHub Release notes
15. Add iteration cap to tuning drain loop (item 16)
16. Run `SyncFromDevice` off the UI thread (item 19)
