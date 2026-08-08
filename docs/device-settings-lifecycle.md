# Device Settings Lifecycle

How a FanaBridge device decides what to store, and why the parts are arranged the way they are.

This exists because getting it wrong destroys user data. In August 2026 a user's device settings files were replaced by ~450-byte stubs, losing hand-built LED profiles. The cause was structural rather than a slip, and the rules below are what prevent it.

## What SimHub does

Three behaviours of SimHub shape everything here. They were established against 9.11.21 and have held unchanged under the currently pinned 9.11.22, where this design was manually validated.

**A device's settings file is rewritten from a single call, wholesale.** SimHub asks a device to serialize itself and writes the result over the file. There is no merge against what is already on disk, so anything the device does not include is erased. A device cannot repair this later — by the time it notices, the previous contents are gone.

**Devices are created and saved whether or not the plugin is running.** SimHub finds device types by scanning assemblies, not by consulting the list of enabled plugins. Disabling FanaBridge stops the plugin, not the devices: SimHub still constructs them, still loads their settings, and still saves them. Anything a device needs in order to describe itself must therefore not depend on the plugin.

**A save that throws is safe.** SimHub records the device in its index *before* asking it to serialize, and catches failures per device. A device that refuses to save leaves the existing file untouched, keeps its index entry, and does not get its settings directory cleaned up as an orphan. Other devices still save. Refusing is therefore a legitimate outcome, and a better one than writing something incomplete. (This describes the save-all path. A couple of SimHub's one-off UI actions — saving a device as its type's defaults, for one — call the same serialization without a catch of their own, so a refusal there surfaces as an error rather than a quiet skip. The file-safety half of the promise still holds; it is only quietness that does not.)

The same holds for a device that fails to load: SimHub keeps it in the index as an orphan, so its stored settings survive until the problem is fixed.

## Two planes

The design separates what a device *is* from what is *currently plugged in*.

**Configuration** — the device's profile and capabilities, resolved when SimHub registers device types. Fixed for the lifetime of a device instance. This is what the LED editor is sized from, and everything about storing settings depends only on this.

**Runtime** — the live plugin, its hardware encoders, and the connected wheel. Appears and disappears freely: the plugin can be disabled, restarted between games, or replaced. This drives hardware output and nothing else.

The wipe happened because the LED editor was built on the runtime plane. With no plugin there was no editor, and with no editor the device could not describe its LED settings — but SimHub asked it to save anyway.

## Building it up front

The LED editor and the settings owner are created with the device instance, from its registered capabilities. Nothing in that touches hardware or the plugin: SimHub's LED module only allocates editor state at construction, and its per-channel drivers appear when settings are applied.

The consequence is the point: a device can always describe its settings, so a save while FanaBridge is disabled reproduces what was loaded instead of dropping what it cannot see.

Because the editor's slot count is fixed once built, the profile that sizes it has to be the one the user actually chose. Device registration therefore resolves the user's profile override itself, reading the stored settings file directly rather than through the plugin — which may not be running. An override is only honoured when it matches the same wheel and module, since the device's identity, and therefore the name of its settings file, is derived from those.

Changing an override to one with a different LED layout still needs a SimHub restart for the editor to resize. That is unchanged, and the settings page says so.

## What a saved document contains

One type owns the answer. It composes each document in this order, so that later sources win:

1. **Residual** — everything from the loaded document that nothing else accounts for, kept verbatim.
2. **LED module** — whatever the module currently reports.
3. **Typed settings** — the options FanaBridge itself understands.
4. **Identity** — derived from the device's profile, never echoed back from input.

Two rules in there are load-bearing:

**Unrecognised settings survive.** Anything this build does not understand is kept and written back untouched. Without this, opening SimHub with an older build silently deleted settings a newer one had written — and a feature branch's settings were dropped on every reload.

The promise covers whole roots. Settings nested *inside* the LED module's own data are re-serialized by the module and are not preserved.

**A channel with no driver keeps its stored data.** SimHub reports every LED channel, using null for ones it has no driver for right now. Treating that as "delete" wiped stored data for hardware the current profile has no driver for. A null means "nothing to say", so the stored value stays.

## When something goes wrong

The rule is that a device would rather save nothing than save something incomplete, because SimHub's file is the only copy.

**Settings the module rejects are not committed.** The previous settings stay in place, and the device stops saving. A module that took only part of a document holds a mixture of old and new state that nobody chose, so persisting it would replace a good file with a fictional one. LED output pauses for the same reason. A later successful load, or an explicit reset, clears it.

**A failed serialization is not sticky.** It can happen transiently — the LED editor is being used on another thread while a save runs — so it fails that one save and the next one tries again. Latching it would turn one bad moment into a device that never saves again.

**Failing to construct or load leaves the device unpublished** rather than half-built. SimHub keeps its settings directory, so nothing is lost.

**Anything the runtime does is not a failure of this kind.** No plugin means no LED output, and the device presents itself as not enabled: SimHub greys a device's settings while it is switched off, and one nothing can drive gets the same treatment. Editing is therefore unavailable — deliberately, and consistently with how SimHub already treats a device the user switched off.

What that does *not* mean is data loss. The device still describes its settings in full, so a save taken in that state reproduces what was loaded, and the user's own on/off choice is left exactly as they set it — a click on the toggle while nothing can drive the device is declined rather than stored, since the interface would refuse to show it either way.

## Concurrency

Applying, saving, resetting and editing all take one private lock in the settings owner, so a save sees either the whole old state or the whole new one, never a mixture. Parsing happens outside it, and listeners are notified after it is released.

SimHub's LED editor synchronizes itself internally and is outside that boundary — the plugin cannot lock it, and pretending otherwise would be a claim the code cannot keep.

## Output

LED output binds to the runtime at the moment it needs a driver, never to whichever generation happened to exist earlier. It builds only from the current plugin's encoders: reusing earlier ones is what issue #37 was about, where a device kept writing to a transport belonging to a plugin that had been disposed.

When there is nothing to drive, it returns no driver rather than throwing — SimHub asks for one outside its own exception handling, so throwing escapes into the frame loop. It also keeps asking indefinitely, so a device idle while the plugin was down recovers when it comes back. Both the unavailable and the recovered states are logged once, not per frame.

## Known gaps

A settings panel left open while SimHub applies a different document to the same device shows the previous values until it is reopened. The stored settings and the hardware are correct; only the open panel is stale.

Two narrow races remain, both requiring a settings document to be applied at the same moment as something else, and neither able to corrupt the stored file:

The Screen panel edits the live display settings object directly, outside the owner's lock. An edit landing at the same instant as a document being applied can submit a mixture of the two, and the display driver can briefly read one. The next save writes whichever values won; nothing is lost.

Output checks whether the device is faulted just before driving a frame, rather than holding the lock across it. An apply that faults in that gap can let one frame render from partly applied settings. Holding the lock across per-frame hardware output would be the alternative, which is a worse trade.
