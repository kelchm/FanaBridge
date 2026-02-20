# SimHub Fanatec Plugin - Development Plan

## Overview
Build a SimHub plugin to control Fanatec M4 GT3 wheel LEDs using the HID protocol we've reverse-engineered.

## References & Resources

### SimHub Plugin SDK
- **Location**: `C:\Program Files (x86)\SimHub\PluginSdk`
- **Key Examples**:
  - `User.DeviceExtensionDemo` - Device extension (attaches to existing devices)
  - `User.LedEditorEffect` - LED effect system
  - `User.PluginSdkDemo` - Full plugin with properties and actions

### Similar Plugins to Study
1. **LED Editor** (built into SimHub)
   - Path: LED effects like rainbow, blink, etc.
   - Found in: `SimHub.Plugins.DataPlugins.RGBDriver`
   
2. **Fanatec Pedals Support** (ShakeIt)
   - SimHub already has some Fanatec integration for pedal vibration
   - May provide device discovery patterns

3. **Arduino LED Support**
   - Standard LED driver sketch pattern
   - Shows how SimHub expects LED data to flow

### Documentation
- **Plugin SDK Wiki**: https://github.com/SHWotever/SimHub/wiki/Plugin-and-extensions-SDKs
- **LED Editor Guide**: https://github.com/SHWotever/SimHub/wiki/LED-Editor-guide
- **Requirements**: Visual Studio 2022+, WPF and C# knowledge

## Plugin Architecture Options

### Option 1: Device Extension (Recommended First Step)
**Extends existing Fanatec wheel device detection**

**Pros:**
- Minimal scaffolding - attaches to existing device
- Appears as a tab in device settings
- Simpler integration path
- Can leverage SimHub's device discovery

**Cons:**
- Depends on SimHub recognizing the Fanatec wheel
- Less control over device lifecycle

**Implementation:**
- Inherit from `DeviceExtension`
- Override `DataUpdate()` for game loop updates
- Implement settings persistence (JSON)
- Create WPF control for settings UI

### Option 2: Standalone Plugin
**Full plugin with complete control**

**Pros:**
- Complete control over device lifecycle
- Can create custom properties/actions
- Independent of other device detection

**Cons:**
- More complex setup
- Need to handle all device management

### Option 3: LED Effect (Not Recommended for Wheel)
**Custom effect in LED editor**

**Pros:**
- Integrates with existing LED editor UI
- Users familiar with interface

**Cons:**
- Designed for external LED strips, not wheel buttons
- Less direct hardware control
- Awkward fit for per-button control

## Recommended Approach: Hybrid Strategy

### Phase 1: Proof of Concept (Current Stage ✅)
**Status: COMPLETE**
- [x] Reverse-engineer HID protocol
- [x] Build standalone C# HID library
- [x] Create demo animations
- [x] Verify hardware control works

### Phase 2: Device Extension (Next)
**Create a Fanatec Wheel Device Extension**

**Why this approach:**
- Fast iteration - uses existing infrastructure
- Users can configure per-game profiles
- Integrates with SimHub's property system
- Can expose wheel LED state as properties for other plugins

**Implementation Steps:**

1. **Create Extension Project**
   - Copy `User.DeviceExtensionDemo` as template
   - Rename to `User.FanatecWheelLedExtension`
   - Reference our existing HID library

2. **Device Detection & Connection**
   - Hook into device lifecycle (`Init()` / `End()`)
   - Detect Fanatec ClubSport DD+ (VID: 0x0EB7, PID: 0x0020)
   - Open 64-byte HID interface on connection

3. **Settings Management**
   - LED brightness (0-7)
   - Color presets for different states
   - Enable/disable individual buttons
   - Default color scheme

4. **Game Data Integration**
   - `DataUpdate()` receives game state every frame
   - Map game data to LED states:
     - RPM/shift light
     - Flags (blue, yellow, green, checkered)
     - DRS status
     - Pit limiter
     - Warnings (damage, fuel, etc.)
     - Custom per-button bindings

5. **Properties Exposure**
   - Create properties: `FanatecWheel_LED0_Color`, etc.
   - Allow other plugins/effects to control LEDs
   - Expose current state for scripting

6. **UI/Settings Control (WPF)**
   - Color picker for each button
   - Brightness slider
   - Preview/test buttons
   - Import/export profiles

### Phase 3: LED Effect Integration (Optional)
**Allow LED editor effects to control wheel buttons**

- Create custom effect container
- Map 12 buttons to LED strip indices
- Allow existing effects (rainbow, pulse, etc.) on wheel

### Phase 4: Advanced Features
**Polish and production-ready features**

1. **Multi-Wheel Support**
   - Detect multiple Fanatec devices
   - Per-device settings

2. **Animation System**
   - Reuse animations from our demo
   - Knight Rider, rainbow, sparkle, etc.
   - Trigger on events (race start, pit stop, etc.)

3. **Property Bindings**
   - Let users bind any SimHub property to LED color/brightness
   - Formula support (NCalc/JavaScript)

4. **Profile Management**
   - Per-game profiles (auto-switch)
   - Import/export community profiles
   - Default profiles for popular sims

5. **Performance Optimization**
   - Rate limiting (don't spam HID)
   - Batch updates
   - Background thread for HID communication

## Code Structure

```
FanatecWheelLedPlugin/
├── Core/
│   ├── FanatecHidDevice.cs           (existing - HID layer)
│   ├── ColorHelper.cs                (existing - color utils)
│   └── LedAnimations.cs              (existing - effects)
│
├── Plugin/
│   ├── FanatecWheelExtension.cs      (main extension class)
│   ├── FanatecWheelSettings.cs       (settings model)
│   └── FanatecLedManager.cs          (game data → LED mapper)
│
├── UI/
│   ├── FanatecWheelSettingsControl.xaml      (settings UI)
│   └── FanatecWheelSettingsControl.xaml.cs
│
└── Models/
    ├── LedMapping.cs                 (button → game data binding)
    └── ColorScheme.cs                (preset color schemes)
```

## Game Data to LED Mapping Examples

### RPM-Based Shift Light
```csharp
// In DataUpdate()
float rpmPercent = data.NewData.Rpms / data.NewData.MaxRpm;
byte intensity = (byte)(rpmPercent * 7);

// Progressive fill
for (int i = 0; i < 12; i++)
{
    if (rpmPercent > (i / 12.0f))
    {
        SetLed(i, GetShiftColor(rpmPercent), intensity);
    }
}
```

### Flag States
```csharp
if (data.NewData.Flag_Blue)
{
    SetAllLeds(ColorHelper.Colors.Blue, intensity: 7);
}
else if (data.NewData.Flag_Yellow)
{
    BlinkAllLeds(ColorHelper.Colors.Yellow, 500);
}
```

### DRS Indicator
```csharp
if (data.NewData.DRSEnabled)
{
    SetLed(0, ColorHelper.Colors.Green, 7);  // Button 1
}
else
{
    SetLed(0, ColorHelper.Colors.Black, 0);
}
```

## Technical Considerations

### Threading
- SimHub calls `DataUpdate()` on game thread (high frequency)
- HID writes should be fast (already ~0.5ms)
- Consider async/background thread if latency issues

### Performance
- Current approach: ~40-60 FPS update rate is plenty
- Batch color + intensity updates in single frame
- Use `commit: false` for staging, then one `commit: true`

### Error Handling
- Device disconnection (USB unplug)
- HID write failures
- Multiple instances of SimHub/FanaLab conflict

### Compatibility
- Test with FanaLab (Fanatec official software)
- May need exclusive HID access
- Detect and warn if other software is controlling LEDs

## Testing Strategy

1. **Unit Tests**
   - Color conversion (RGB ↔ RGB565)
   - HID packet construction
   - Settings serialization

2. **Integration Tests**
   - Device connection/disconnection
   - Game data updates
   - Profile switching

3. **Real-World Testing**
   - Different racing sims (iRacing, ACC, AC, etc.)
   - Long sessions (memory leaks, stability)
   - Multiple devices
   - Hot-plug scenarios

## Distribution

### Initial Release
- GitHub repository
- Installation: Drop DLL in SimHub plugins folder
- Documentation/README with setup guide

### Future
- Submit to SimHub community plugins
- Auto-update mechanism
- Community profile sharing

## Timeline Estimate

- **Phase 2 (Device Extension)**: 2-3 days
  - Day 1: Basic extension setup, device connection
  - Day 2: Game data integration, basic mappings
  - Day 3: UI/settings, testing

- **Phase 3 (LED Effects)**: 1-2 days
  - Integration with LED editor

- **Phase 4 (Advanced Features)**: 1-2 weeks
  - Animation system, profiles, polish

## Next Steps

1. **Copy SDK example** to our project
2. **Reference our existing HID library**
3. **Implement basic extension** with device connection
4. **Test with SimHub** running a sim
5. **Iterate on game data mappings**

Would you like to proceed with Phase 2 and create the Device Extension?
