# Threading Model

Which thread owns what in FanaBridge, how state crosses threads, and who may write to the hardware.

This page describes the **target** model. Where the code on `main` does not follow it yet, the text says so in a line starting **Not yet**, names the step that closes the gap, and the gap is listed again under [Gaps today](#gaps-today). The pull request that closes a gap removes its marker and its row in the same change, so the page stays true.

The model has two halves:

- **State** is one immutable snapshot. Readers take it without a lock. Every change to it is computed and published under one short lock.
- **Output** goes through one gate in Core. Nothing else can reach the hardware encoders. Every operation is checked against the snapshot before it starts, and every report it sends is bound to the connection it was admitted on.

## What SimHub does

These host behaviours shape the model. They were observed with SimHub 9.13.7, the version pinned in `.simhub-version`, and the model assumes 9.13.7 or later. Where an earlier version is known to differ, the bullet says so.

- **Plugins share one update thread.** SimHub calls every plugin's `DataUpdate` from the same thread, and keeps calling it with no game running. It gives no deadline and can skip samples while that thread is busy. Time FanaBridge spends there is time every other plugin waits. It stops while SimHub restarts its plugin manager between games.
- **A stopping plugin manager does not wait long for that thread.** SimHub waits about two seconds for the update thread and then carries on. A `DataUpdate` call still in progress can therefore overlap `End` and `FinalizePlugin`, and after a restart it can overlap the next `Init` and the new update thread's calls.
- **Each device has its own update thread.** SimHub runs every added device's `DataUpdate` on a thread of that device's own, one frame at a time, and coalesces frames, so a device that falls behind gets the newest frame rather than a queue of old ones. SimHub waits for that thread before it calls the device's `End`, and `End` runs on the thread doing the teardown, not on the device's own. SimHub 9.11.21 ran each device frame as a thread-pool task instead. The model does not depend on the difference: the [device frame lock](#locks) orders a device's frames and its `End` whatever the host does.
- **Devices outlive the plugin.** Device instances survive a plugin manager restart, and survive the plugin being disabled and enabled again, which creates a new plugin object. [Device Settings Lifecycle](device-settings-lifecycle.md) covers what that means for settings.
- **At application exit, devices end in parallel with the plugin's teardown.** A device's `End` and `FinalizePlugin` can run at the same time, in either order.
- **An LED driver that declines a frame stays connected.** When the driver's send returns false, SimHub treats the frame as not shown and asks again next frame. When it throws, or reports itself disconnected, SimHub disposes the driver and only asks for a new one after a delay. SimHub's periodic forced refresh fires only once several seconds have passed without an accepted frame.
- **Control Mapper asks for the wheel variant on a thread of its own.**
- **Settings pages, the wizard and device panels run on the WPF UI thread.**

## Threads

| Thread | Owns | Must not |
|--------|------|----------|
| **Plugin update thread** (SimHub; calls `FanatecPlugin.DataUpdate`) | The [connection turn](#the-connection-turn): connect, disconnect, reconnect, the heartbeat. The transport's identity, ITM and SRM report streams. The identity settler. Both epochs. The Control Mapper reconcile. | Wait for the output gate. Write LEDs, the display or ITM, except to run a [pending cleanup](#cleanup). Wait on the UI thread while holding a FanaBridge lock. Do unbounded work. |
| **`Init` and `FinalizePlugin` callers** (SimHub) | `Init` builds the core once and takes a connection turn for the first connection. `FinalizePlugin` closes the core's output and retires the core; it takes no turn. `End` runs on every plugin manager stop and only saves settings. | Assume the plugin update thread has stopped. |
| **col03 reader thread** (FanaBridge, one per connection) | The blocking HID read. It routes each input report to a per-family queue and runs that queue's diagnostics taps. | Write to the device. Take any lock on this page; the queue's own lock is a leaf inside the transport. Call a consumer. A tap only copies the report and takes at most its own private lock. |
| **Device update threads** (SimHub, one per added device) | That device instance's drivers: the LED manager and driver, the 7-segment display driver, the ITM driver and its lifecycle. Its leases. | Hold an encoder or the transport. Touch another instance's drivers. Decide for itself whether a blank is safe. |
| **Device `End` callers** (SimHub teardown) | Nothing. `End` takes the device's frame lock and releases the device's channels through the gate. | Touch the drivers without the frame lock. |
| **WPF UI thread** | All WPF state. Profile edits: the profile store reload or delete, the override swap, and the synchronous capability refresh that follows. Tuning reads and writes. The diagnostics capture. Taking and releasing holds. | Write output without a hold. Change the override map in place. Touch a device's drivers: a settings edit changes the settings object, and the device's next frame applies it. |
| **UI workers** (the display-test scroll timer, the wizard's blink workers) | Nothing. They write only through the hold their owner took. | Assume the hold is still theirs: every write is checked again. |
| **Control Mapper thread** | Nothing. It reads one snapshot. | Read connection, product id and wheel identity separately. |
| **Updater tasks and timer** | The self-updater's own state. | Touch the core. |

The **core** is the hardware core of one plugin object: the wheelbase, its transport, the encoders and the output gate. It is built once per plugin object and survives plugin manager restarts.

**Not yet (step 2):** the settings-change handler pushes new settings into a live display driver from the UI thread, and a device's `End` uses its drivers without the frame lock.

### The connection turn

All connection work runs inside the core's **connection turn**: connecting, disconnecting, reconnecting, the heartbeat, draining and settling the identity, and every snapshot change except a capability refresh. A turn is entered by trying, never by waiting. A caller that finds a turn in progress does nothing that tick.

That is what makes the overlap SimHub allows safe. Two update threads either side of a manager restart cannot both service the connection, and neither blocks the other.

`FinalizePlugin` does not take a turn, because a turn may be stuck on a late update thread. It **retires** the core instead. Retiring publishes a final disconnected snapshot and marks the core retired under the state lock, then closes the transport for good under the transport write lock. From then on no publish changes the snapshot and no connect leaves a stream open. A turn still in progress finishes without effect: its connect is refused, its publishes are dropped, and its reads see closed queues. A retired core never comes back; a re-enabled plugin is a new plugin object with a new core.

**Not yet (step 1):** nothing fences connection work. An update call that outlives its two-second wait can reconnect a wheelbase that `FinalizePlugin` has disposed, and after a manager restart two update threads can drain the same identity stream.

## State

### The snapshot

Each core publishes one immutable `WheelbaseSnapshot` through a single volatile field. It holds everything a reader decides on:

- the connection: connected or not, product id, product name;
- the **connection epoch**, a counter that changes at every connect and at every disconnect. A connected snapshot's connection epoch is also the number of the transport session its streams were opened as;
- the base identity and the attachment identity: wheel or hub, module, SRM kit;
- whether the identity is stable, meaning no reading is settling;
- the resolved capabilities;
- the **attachment epoch**, a counter that changes whenever output ownership has to be established again: at every connect and disconnect, when an SRM kit is identified, and at the end of every settle;
- the public wheel-change count.

**Not yet (step 1):** `FanatecWheelbase` holds identity, stability and capabilities as separate properties, written one at a time. **Not yet (step 4):** there are no epochs.

### Published connection and live transport health

The snapshot's connected flag is the **published** connection: what the plugin update thread last decided. Every cross-thread reader uses it.

The transport's own health is a different thing: whether the streams are open and the reader thread is alive. It can turn false while the snapshot still says connected, and that difference is how a dead stream is found. Only the connection turn reads it, through `IWheelbaseConnection.IsConnected`, which stays backed by the transport and does not forward to the snapshot. When the heartbeat finds the transport unhealthy it disconnects, and that is what changes the snapshot.

### How readers take state

A reader captures the snapshot once per decision and uses only that copy. A device frame captures it once at the top and takes the attachment check, the capabilities and its lease from the same copy. Whatever the frame builds, including an LED driver SimHub asks for part-way through, is built from that copy and not from a second read. Readers never take a lock.

A device remembers which core and attachment epoch its drivers and leases were built for. When a frame's snapshot shows a different core or a different epoch, the device rebinds, whether or not it ever saw an unstable frame: it takes a lease on the new epoch, discards its drivers' caches and starts ITM cold, and it releases any channel it will no longer drive. A device thread can be blocked for longer than a settle lasts, so it cannot count on seeing the unstable snapshot in between.

The single-value properties on `FanatecWheelbase` forward to the snapshot, apart from the transport health above. They are for bindings and one-value reads. Reading two of them to make one decision is a bug.

SimHub reads each `FanaBridge.*` property through its own delegate, so two properties can come from adjacent snapshots. That is accepted; see the [review floor](#review-floor).

**Not yet (step 1):** the device attachment check, `ResolveCapsFor`, LED driver construction, the restart notice's comparison of live and registered capabilities, the Control Mapper variant provider, the settings page and the diagnostics report each read several properties in turn.

### How state changes

Every change is one function from the previous snapshot and the current inputs to the next snapshot, run start to finish under the wheelbase's **state lock**. That covers connecting, disconnecting, the start of a settle, the end of a settle, an SRM kit being identified, a capability refresh, and retiring the core. Because the previous snapshot and the inputs are read under the same lock that publishes the result, two changes cannot interleave and neither can overwrite the other with a stale result. There is no compare-and-retry and no posted request. Once the core is retired, the function returns the retired snapshot whatever it is asked.

The connection turn makes every change except two: the capability refresh, which the UI thread makes synchronously after a profile edit and whose result it reads straight afterwards, and retirement, which `FinalizePlugin` makes.

Only the connection turn changes either epoch. A capability refresh never does.

**Not yet (step 1):** there is no state lock. The identity commit on the plugin update thread and `RefreshCapabilities` on the UI thread can interleave, and either can publish over the other.

### Inputs

Capability resolution runs under the state lock, so it must be pure: memory only, no callbacks. Its inputs are immutable.

- **Profile overrides** are an immutable map. An edit builds a new map and swaps the reference; nobody changes a map another thread may be reading.
- **The profile catalog** is the profile store's own immutable snapshot. It is loaded before the first state change, and a reload swaps it whole. File I/O happens outside the lock.
- **Profiles** are frozen by ownership: once the store has loaded a profile, nothing changes it or its LED list, and a reload builds new objects instead. The types still have setters, so this is a rule, not something the compiler enforces. A snapshot published before a reload is unchanged by it.

The catalog and the overrides are swapped separately. Between a reload and the override swap, a commit on the plugin update thread can resolve from the new catalog and the old overrides. Each such result is valid for the inputs it read, and the refresh the UI thread runs last converges. The guarantee holds when that refresh returns, not before.

Hub and module composition ([#16](https://github.com/kelchm/FanaBridge/issues/16)) replaces the resolution function. It inherits the same rule.

**Not yet (step 1):** the override map is a plain dictionary changed in place on the UI thread and read through a callback on the plugin update thread. Resolution logs, and on first use loads profiles from disk.

### Events

`WheelChanged` is raised after an identity commit that changed the attachment, after an SRM kit is identified, and after a capability refresh. A settle that ends where it began raises nothing. The connection events are raised on connect and disconnect, and `StateChanged` after any of them.

Events are raised after the state lock is released, with no FanaBridge lock held, on whichever thread made the change. A handler may block; the Control Mapper re-enumeration waits on the UI thread. The connection turn is still in progress while its events are raised, which is safe because nothing waits for a turn.

Events carry no state. A handler reads the snapshot again. Events raised by different threads can arrive in either order, so a handler must not work out what happened from the event alone. WPF handlers dispatch to the UI thread.

**Not yet (step 1):** `WheelChanged` passes the wheelbase, and handlers read its properties one at a time. **Not yet (step 4):** the plugin's `WheelChanged` handler also marks the LED encoders dirty; the gate takes that over at a new epoch.

## Locks

| Lock | Guards | Allowed while holding | Never while holding |
|------|--------|-----------------------|---------------------|
| **State lock** (wheelbase) | Computing and publishing the next snapshot. The retired mark. | Reading inputs, resolving capabilities in memory, allocating. | HID or disk I/O, logging, events, delegates, waits, any other lock. |
| **ITM staging lock** (wheelbase) | The staged ITM reports and the epoch they belong to. | List operations. | Anything else, logging included. |
| **Device frame lock** (one per device instance) | That instance's drivers and frame state. One frame, or `End`, at a time. | Everything a frame does, including the output gate. | Another instance's frame lock. Waiting on the UI thread. |
| **Output gate** (core) | Admission and one whole output operation. Channel owners, holds, lit records, pending cleanup, output revisions. | Encoder calls and the HID writes of that one operation. The ITM staging lock. Logging. | Waiting on a task, a thread, an event or a timer. Elicited reads (tuning, diagnostics). Events and host callbacks. UI dispatch. |
| **Encoder locks** (one per encoder) | That encoder's pooled buffer and dirty cache. | Building and sending one report sequence. | Calling another encoder or the gate. |
| **Transport write lock** | HID writes, and which session the open streams belong to. A batch holds it across a report sequence, or across an elicited request and its reply. | Sending. Reading the caller's own report stream. Closing the streams, with a bounded join of the reader thread. | Taking any lock above it. |
| **Device list lock** (plugin) | The list of registered device instances. | Copying the list, reading one snapshot, reading volatile fields. | Device work. |
| **Device settings lock** | See [Device Settings Lifecycle](device-settings-lifecycle.md). | | Nesting with any lock here. |
| **Control Mapper locks** (the bridge's and the integration's) | Provider registration with SimHub's Control Mapper. | SimHub's own Control Mapper lock. The device list lock. | A UI dispatch, an event, any other lock on this page. |

The **connection turn** is not in the table because nothing ever waits for it.

**Order:** device frame lock, then the output gate, then either an encoder lock and the transport write lock, or the ITM staging lock. Those are two separate paths below the gate and they never meet. The state lock and the ITM staging lock are leaves: nothing is taken while holding them, and they are never taken together. The gate reads the snapshot through its volatile field and never takes the state lock.

Three rules follow from the table and are worth stating on their own:

- **The plugin update thread never waits for the output gate.** The gate is held across HID writes, and a write can wait behind a tuning read. Revoking output is done by publishing a snapshot, which never waits for a writer. The one thing the update thread does at the gate is try it, without waiting, to run a pending cleanup.
- **Disconnecting closes the report queues before it takes the transport write lock.** An elicited read holds that lock while it waits; closing the queues first is what wakes it.
- **Nothing waits for a connection turn,** so nothing can deadlock on one.

### How long things can wait

The waits below come from timeouts in the code. They are estimates of the usual worst case, not deadlines.

- The transport write lock can be held for about a second by a tuning read, for about 0.7 to 1.2 seconds by the diagnostics capture, and for up to about half a second by the first identity read after connecting.
- Anything that writes can wait that long: a device frame, a hold's worker, a pending cleanup, the close in `FinalizePlugin`, and the plugin update thread's own keep-alive and SRM query.
- Disconnecting joins the reader thread for up to a second while holding the transport write lock.
- A `WheelChanged` handler that re-enumerates Control Mapper waits on the UI thread for as long as the UI thread is busy.

**Not yet:** the ITM encoder's buffer is unlocked (step 4). There is no output gate in Core (step 4). Each device instance's lock covers only the drawing part of a frame and orders it against plugin teardown only (step 2 widens it to the whole frame and `End`). The wheelbase logs a warning while holding the ITM staging lock (step 1).

## Hardware output

### Channels

Output is divided into three channels: **LEDs** (every LED report, on either HID collection), the **7-segment display**, and the **ITM display**. A write is one whole operation on one channel: an LED frame with all its reports, a blank, a display update, an ITM command or value batch.

Tuning and the diagnostics capture are not output. They light nothing, they use the transport directly, and only the transport write lock orders them.

### Who may write

Only the core's output gate calls the encoders. No encoder is reachable from outside Core.

**Not yet (step 4):** `FanatecPlugin` hands out the four encoders and the transport. Device drivers, the wizard and the settings page's display test call them directly.

A caller writes by presenting one of two kinds of authority:

- A **lease** belongs to a device instance, to the core that issued it, and to one attachment epoch. The device takes it from the snapshot its frame captured. It is valid while the core is open, the snapshot is connected and stable, and the snapshot's attachment epoch is the lease's.
- A **hold** belongs to the wizard (all channels) or the display test (the 7-segment display). It is exclusive: while it stands, leases are refused on those channels. It belongs to the core and to one connection epoch, not to the attachment, because the wizard probes wheels that have no profile and may not be identified. It is valid while the core is open, the snapshot is connected, and the snapshot's connection epoch is the hold's. Stability is not required to write. Blanking what a hold lit is stricter, and has to pass the [blank test](#the-blank-test).

A hold is a record inside the gate, not a held lock. The wizard can keep one for minutes without blocking anything.

### Admission

For every operation the gate takes its lock, [brings itself up to date](#keeping-the-gate-up-to-date) with the published snapshot, and reads that snapshot once. That read is the **admission point**. The gate checks the caller's authority against it, checks that no one else holds the channel, and checks that the channel is free or already the caller's. An operation is admitted whole or refused whole. A snapshot published after the admission point does not split an admitted operation, because the gate does not look again.

The first lease to use a channel in an epoch owns it until that device releases it or the epoch ends. A second device's operations on an owned channel are refused.

A refused operation writes nothing and says why: stale, unstable, held, not the owner, or closed. A refusal is not an error. The LED driver reports the frame as not shown, stays connected and does not remember the frame, so it is sent again once writes are accepted. No driver throws or reports itself disconnected because it was refused, and a refusal does not start retry pacing.

**Not yet (step 4):** nothing checks a write against the attachment it was computed for. A device decides at the top of its frame and writes for the rest of it, and multi-report operations are not ordered against transitions at all.

### Delivery

Admission says an operation may start. It does not say every report arrived.

Each report of an admitted operation carries the connection epoch it was admitted under. The transport checks it under the write lock and refuses a send for a session that is no longer the open one. An operation that pauses before its first report, or between two reports, therefore cannot continue onto the streams of a later connection.

The connection turn numbers a session when it opens the streams, with the connection epoch it then publishes the connected snapshot in. The epoch a disconnect publishes never numbers a session: no streams are opened under it, and nothing is admitted while the snapshot is disconnected. So every admitted operation carries the number of the session that was open at its admission point, and the transport's comparison is exact.

A send can also fail on the wire. Either way the operation stops there as **failed**, and some of its reports may already have been delivered. The gate then treats the channel as lit, the encoder forgets what it believed the hardware showed, and the caller is told the operation failed, so its next attempt sends everything.

So an operation ends in one of three ways: **delivered**, **refused** with its reason, or **failed**.

A report sequence the firmware needs unbroken is sent as one transport batch by its encoder. An operation as a whole is not a batch: a tuning read or a diagnostics capture can fall between two of its sequences. That is harmless and is not prevented.

**Not yet (step 4):** the transport takes its write lock for each send and opens new streams on reconnect, with nothing tying a send to the connection it was meant for.

### No waits under the gate

The gate is held only for the HID writes of one operation. The LED driver writes on the device's own update thread, as the display and ITM drivers do, so there is no task to wait for and a blank never waits on a frame in flight.

The LED driver remembers on its own thread whether its last attempt was delivered. After a failed attempt the hardware may show part of that frame, so the next frame is sent whatever it is, even one equal to the last delivered frame. A frame identical to the failed one is retried at most about once a second. A frame that differs is never held back.

**Not yet (step 2):** the LED driver sends each frame from a thread-pool task, remembers the frame before the write is accepted, and waits up to two seconds for that task when it clears or is disposed.

### Releasing a channel

A device releases a channel when it stops driving it: the device is switched off, suspended with the monitor, set to display mode "None", ended, no longer the attached device, or its capabilities no longer include the channel. It asks the gate to release, and the gate decides what happens:

- If a hold has the channel, the gate writes nothing now. If the lit record is still the device's, it stays, marked as released, and is owed as a [cleanup](#cleanup) that waits for the hold. If the hold has written since, the record is the hold's and the hold's release settles it.
- If another device owns the channel, or the core is closed, the gate does nothing. Another device owns an LED or display channel only by writing to it, and that write replaced the record.
- Otherwise, if the lit record passes the [blank test](#the-blank-test), meaning the snapshot is connected and stable and the hardware in the record is still there, the gate blanks the channel as one operation, clears the lit record and frees the channel. The caller's lease may be from an earlier epoch; what matters is that it wrote the record and nobody has written the channel since.
- If that blank fails, or the snapshot is unstable, the gate frees the channel, keeps the lit record, and owes a cleanup.
- If the hardware the record describes is gone, the gate drops the record and writes nothing.

The device then drops its drivers. It does not blank on its own judgement and it keeps no retry state: after a swap a device cannot tell which hardware a blank would reach, and the gate can.

A blank the owner sends while it keeps the channel is an ordinary write and the owner retries its own. The 7-segment blank and the ITM placeholder reset when a game exits are both of this kind.

**Not yet (step 4):** a device that loses its attachment blanks unconditionally on its own thread, whenever it next runs, which can be after different hardware is attached. Each device carries its own retry latches for blanks.

### The lit record

For each channel the gate keeps a **lit record**:

- whether FanaBridge's last admitted write may have left the channel lit;
- which device or hold wrote it;
- the connection epoch it was written on, and its **target**, the attachment it was written to;
- what is needed to blank it: the capabilities of a leased write, or the holder's own blank for a hold, which probes with no capabilities at all.

A delivered lit write sets it, and so does a failed write of any kind. Each such write replaces the target with its own. A delivered blank clears it.

The target is the attachment identity in the snapshot the write was admitted against, kept as the raw codes the base reports: the attachment code, the module code and whether it is an SRM kit. It is not the decoded wheel and not its profile. A wheel FanaBridge cannot name, or one with no profile, still reports raw codes, and so does a base with nothing attached. The wizard's probe of an unidentified wheel therefore has a target like any other write.

A lease's write always has a target, because a lease is admitted only against a stable snapshot. A hold's write admitted while a reading is settling has **no target**: the committed identity is known to be out of date, and nothing says which hardware the report reached.

The record describes hardware, so it is dropped when that hardware is gone: when the connection epoch changes, or when the committed attachment is no longer the one written to. A settle that ends where it began keeps it. Today the hardware a channel lives on is the whole attachment. Under [#16](https://github.com/kelchm/FanaBridge/issues/16) it becomes the part that carries the channel, so a hub's own display stays recorded when only the module changes.

#### The blank test

The gate blanks a channel from its lit record only when all of these are true of the snapshot at that gate entry:

- it is connected and stable;
- its connection epoch is the record's;
- the record has a target, and the snapshot's attachment identity has the same raw codes.

The test is the same for every blank the gate sends on its own account: a device's release, a hold's release, a pending cleanup and the close. It is the same whoever wrote the record. A hold gets no exception, because a hold's authority to write says nothing about what its earlier write reached.

When the test fails, what happens depends on why:

- **The snapshot is unstable, and the record has a target on this connection.** The blank waits as a [pending cleanup](#cleanup) until the settle ends. The close cannot wait, so it leaves the channel as it is.
- **The connection epoch differs, or the committed attachment is not the target.** The hardware is gone. The record is dropped and nothing is written.
- **The record has no target.** It cannot be verified now or later. It is dropped and nothing is written, at the hold's release, at the close, and when the settle ends. See [Known limits](#known-limits).

**Not yet (step 4):** the display encoder keeps a `HasWritten` latch, each device keeps its own was-connected flag, and teardown asks each device to blank itself.

### Cleanup

A **pending cleanup** is a blank the gate owes: a lit record with a target whose writer, a device or a hold, has released the channel, or been revoked, without a delivered blank. A record with no target never becomes a pending cleanup; it is dropped where the cleanup would have been owed.

The gate runs a pending cleanup at its next entry by any caller, once the record passes the [blank test](#the-blank-test): the snapshot is connected and stable, on the record's connection epoch, and its attachment identity is the record's target. A hold's cleanup is held to the same test as a device's. A cleanup does not run while a hold has the channel or another device owns it; it waits. It ends without a write in two ways: the hardware is gone, which drops the record, or someone else writes the channel, delivered or failed, which replaces the record with the new writer's. Taking a channel does not end a cleanup. Only a write changes what the hardware shows, so only a write moves the responsibility for it. After a failed attempt the gate waits about a second before the next.

A pending cleanup must not wait for a caller who may never come, such as a device that was removed while the identity was settling. So while one is pending and not waiting for a hold, the plugin update thread tries the gate each tick and runs the cleanup if it gets in. It never waits for the gate. While the plugin manager is stopped, the cleanup waits for the update thread to resume or for the core to close.

### ITM has no blank

Nothing turns the ITM display off as cleanup. Turning ITM mode off changes a setting the wheel keeps across power cycles, so that command is sent only to apply the user's own ITM on/off choice. Releasing the ITM channel, a pending cleanup and the close in `FinalizePlugin` send nothing to the ITM display, and it keeps its last page. Clearing its fields to placeholders is a write the owner makes while it has the channel, as it does when a game exits.

### Holds

- **Taking a hold** is one step under the gate, for all the channels asked for or none of them. It takes those channels from any lease that owns them; the owner's next operation is refused as held. Taking a channel writes nothing, so it leaves the lit record and any pending cleanup as they are. It is refused if another hold has any of the channels, so the settings page ends a display test before it opens the wizard.
- **Each hold has its own token.** Workers started by the hold's owner write with that token.
- **Releasing a hold** is one step under the gate. It revokes the token first, so a worker that fires from then on is refused. Then it settles each channel the hold lit by the [blank test](#the-blank-test), removes the hold and changes the output revision. A record that passes is blanked with the holder's own blank, and a blank that fails becomes a pending cleanup. A record with a target, released while a reading is settling, becomes a pending cleanup and is blanked only if the settle ends on that target. A record whose target is gone, or that has no target, is dropped without a write. Then, like any gate entry, it runs any cleanup waiting on a channel it has just freed: a record a device released while the hold had the channel and the hold never wrote.
- **A hold may write while a reading is settling, but the gate will not blank on that write's account.** Such a write leaves a record with no target. If the hold is still standing when the settle ends, its next write is admitted against a stable snapshot and records a target again, so the wizard's close blanks as usual. Only a hold released, or a plugin closed, between such a write and the hold's next write on a stable identity leaves the channel unblanked.
- **A hold revoked from outside,** by a disconnect, a reconnect or the core closing, is found out at the holder's next write. A holder that is still open takes a fresh hold to carry on or to clean up after itself.

**Not yet (step 5):** the wizard and the display test are two volatile flags that device frames poll. Both are cleared before their workers have stopped, so a late tick can write after the release. From step 4 the flags are backed by holds in the gate that have no token yet, so every write goes through the gate but a late worker is still admitted.

### Output revisions

Each channel has an **output revision**. It changes whenever the hardware may no longer show what the channel's lease owner last had delivered: a delivered or failed write by anyone else, such as a hold or a cleanup; a hold being taken or released; a change of owner; and every new epoch.

A cleanup's blank is one of those writes, and it is sent only when its record passes the [blank test](#the-blank-test). A hold's release changes the revision whatever the test decided: blanked, left as a pending cleanup, or dropped without a write. In none of the three can the lease owner assume its last frame is showing, so a record that had to be dropped costs the next owner a full repaint and nothing more.

Every delivered operation returns the revision it was admitted under, and the driver stores that value, not one it reads afterwards. A release that races the write therefore cannot make a write from before the release look current.

Before a driver skips a frame as unchanged, it compares its stored revision with the channel's. If they differ, the hardware no longer shows what the driver last sent: the driver discards its caches and repaints in full, straight away and without retry pacing.

**Not yet (step 5):** nothing tells a driver to repaint. Closing the wizard marks the LED encoder dirty but leaves the driver's own frame cache believing the old frame is still shown.

### Keeping the gate up to date

The thread that publishes a snapshot does not touch the gate. The gate brings itself up to date at its next entry, which is any admission, release, hold call, cleanup attempt or close, before it does anything else. It compares epochs, not flags, because it may never see the snapshots in between: a disconnect and a reconnect can both happen between two entries. Both epochs change at a disconnect as well as at a connect, so a disconnect with no reconnect after it is a connection change like any other, and comparing epochs is enough to see it.

- **The connection epoch changed.** Drop every hold, every channel owner, every lit record and every pending cleanup. Invalidate the encoders' dirty caches. Change every output revision. A disconnect takes this branch whether or not a connect follows it.
- **The attachment epoch changed on the same connection.** Drop every channel owner. Invalidate the encoders' dirty caches. Change every output revision. Drop each lit record, and its pending cleanup, whose target is not the committed attachment, and each record that has no target. Holds stay.
- **The snapshot is unstable.** Leases are refused. Nothing is dropped.

## ITM input

The col03 reader thread routes ITM reports to their queue. The plugin update thread is the only reader of that queue. Each tick it moves a bounded number of reports into the staging buffer under the ITM staging lock, stamped with the attachment epoch.

Before a new epoch's snapshot is published, the plugin update thread flushes the raw queue as well as the staging buffer, and restamps the buffer with the new epoch. A report that was queued under the old epoch can then never be staged under the new one.

The device that owns the ITM channel collects staged reports by presenting its lease. Collecting is an ordinary ITM operation at the gate: the core is open, the snapshot is connected and stable, the epochs match, no hold has the channel, and the channel is free or already this device's. The first valid lease to collect or write in an epoch owns the channel, so ownership is settled before anything is consumed.

Admission is not the last check. The plugin update thread does not hold the gate when it flushes, restamps and publishes a new epoch, so it can do all three after the admission point and before the gate reaches the staging buffer, and by then the buffer may hold the next epoch's reports. So under the staging lock, before it takes or clears anything, the gate compares the buffer's epoch stamp with the attachment epoch the collection was admitted under. If they are equal it takes the batch. If they differ it takes nothing, clears nothing and leaves the stamp alone: those reports belong to whoever owns the channel in the buffer's epoch. Either way the gate releases both locks before the device's lifecycle processes anything.

A refused collection, and one that found a different stamp, return nothing and consume nothing.

Only the owning device's update thread changes the ITM lifecycle. The lifecycle starts cold at every new epoch. During a bring-up it adopts handles only from a push that arrived after its own first command of that bring-up was delivered, and that matches the procedure it is running. A capability refresh that leaves the ITM display unchanged does not restart it.

This fences what the host has queued. A push already on the wire when the wheel changes carries nothing that says which wheel sent it; see [Known limits](#known-limits).

**Not yet (step 4):** collecting staged reports takes no lease, so an outgoing device can consume the incoming wheel's reports. The staging buffer is cleared after the new identity is already visible, and the raw queue is not flushed at all, so a report queued by the old wheel can be staged for the new one. The lifecycle adopts handles from any push in any state. Devices detect a wheel change by polling a counter.

## Transitions

### Connect

The connection turn takes the next connection epoch, opens the transport's streams as that session, reads the first identity, and publishes a connected snapshot in that connection epoch and a new attachment epoch. If that first reading is still settling, the snapshot is unstable and shows no wheel until the settle ends.

**Not yet (step 1):** between connecting and the first update tick, the wheelbase reports a stable identity while the first reading is still settling.

### Wheel swap or module hot-plug

1. The plugin update thread drains a reading that differs from the committed identity and publishes the snapshot as unstable, in the same epoch. From here every lease is refused. Nothing is blanked.
2. Devices see no stable attachment. They report Scanning and write nothing.
3. Every further reading offered while the settle is running restarts the settle window, including one that matches the committed identity.
4. When readings have been quiet for the settle window, the plugin update thread flushes the raw ITM queue, clears and restamps the staging buffer, then resolves capabilities and publishes a stable snapshot in a new attachment epoch. This happens whether the identity changed or settled back to what it was. `WheelChanged` is raised after the lock is released if the identity changed.
5. Every device sees a new epoch and rebinds. The one whose configuration matches takes a lease, paints in full and brings ITM up cold. The others release, and the gate drops or cleans up what they had lit.

Nothing blanks the outgoing wheel. It is unpowered or reset, and the first changed reading can arrive after the new hardware is already on the connection. If the settle ends where it began, the lit records survive: a device that returns repaints, and one that does not leaves a pending cleanup.

### Profile change

1. The UI thread writes or deletes the profile file and reloads the profile store. No lock is held.
2. It swaps in a new override map and refreshes capabilities: under the state lock, the capabilities are resolved again for the same identity and published in the same epoch.
3. It saves settings and raises events after the lock is released, then reads the result for the restart notice, taking the live and the registered capabilities from one snapshot.
4. On its next frame the device keeps its lease, sees different capabilities and rebuilds the drivers they affect. It releases any channel the new capabilities no longer include. ITM is not restarted unless its display changed.

When the attachment's last profile is deleted the capabilities are empty. The device releases every channel, which blanks what it lit, and stays attached and idle. It does not fall back to the capabilities it was registered with. The LED colour notice in the device's LEDs tab follows the same change.

**Not yet (step 3):** with no profile, `ResolveCapsFor` returns the registered capabilities and the device keeps driving. The LED manager treats "no profile" the same as "nothing seen yet" and keeps its driver. The LED colour notice reads capabilities once, when it is built.

**Not yet (step 4):** a channel the new capabilities drop is not blanked. Step 3 stops the writes and adds none, because a blank sent from a frame's own snapshot can land after a different wheel has been committed, and only the gate can refuse it. Between step 3 and step 4 the channel keeps its last frame until the device is switched off, loses its attachment or ends, or a profile is chosen again.

### Disconnect

The connection turn publishes a disconnected snapshot in a new connection epoch and a new attachment epoch, which revokes every lease and hold. At its next entry the gate sees a new connection epoch and drops every hold, owner, lit record and pending cleanup, whether or not a connect has followed. The turn then closes the transport: report queues first, then the streams under the write lock, with a bounded join of the reader thread. The ITM staging buffer is cleared. Events follow. There is nothing to blank.

A reconnect requested from the UI is posted and carried out by the connection turn, because it opens and closes HID streams. It disconnects and connects within one tick, so the gate may see only the result: a later connection epoch. A disconnect that is not followed by a connect shows the gate a later connection epoch too.

**Not yet (step 1):** the transport is closed first and the state is reset afterwards, so a frame in flight can still attempt a write at a closing stream.

### Plugin manager restart

SimHub calls `End` and later `Init` on the same plugin object. `End` saves settings. The core stays open, device threads keep running and leases stay valid. While no update thread is running the identity is not serviced; a settle in progress finishes when one resumes, and output stays suspended until then.

An update call from before the restart may still be running when the new update thread starts. Only one of them holds the connection turn at a time, and the other skips its ticks until the turn is free.

### Plugin generation swap

Disabling and re-enabling the plugin creates a new plugin object with a new core. `FinalizePlugin` closed and retired the old one.

Each surviving device notices on its next frame that the published plugin is not the one its drivers were built against. It drops every driver and lease, registers with the new plugin, and takes leases from the new core.

A lease or hold belongs to the core that issued it, and a retired core never comes back. An operation from the retired core is refused for good, even when the epoch numbers happen to match.

### Finalisation

`FinalizePlugin` runs these in order:

1. Stop updater work.
2. Unpublish the plugin, so devices see none and start no new frames against it.
3. Retire the Control Mapper provider.
4. Close the core's output. Under the gate, in one step: bring the gate up to date with the current snapshot; mark the core closed, which refuses every lease and hold from then on; then blank each channel whose lit record passes the [blank test](#the-blank-test) against that snapshot: connected and stable, on the record's connection epoch, with the attachment identity the record's target. A channel a hold had gets no exception. A record that does not pass is dropped without a write, because the close cannot wait for a settle to end. An operation admitted before the close finishes first, so the blank lands after the last lit write. The ITM display is sent nothing.
5. Retire the core, which publishes the final disconnected snapshot and closes the transport for good, then dispose the wheelbase. Retirement changes no epoch and needs none: step 4 has already closed the gate, and a closed transport refuses every send whatever session it names.

A device's `End` may run before, during or after this. Its release is an ordinary gate call: honoured before the close, refused after it. Either order leaves the wheel dark.

The guarantee is this: after `FinalizePlugin`, no write is admitted, and every channel FanaBridge lit is dark if its hardware could be verified and the blank was delivered. A blank that fails on the wire at close is logged and not retried. A channel is left alone, whoever lit it, when the plugin closes in the middle of a settle, because nothing can say what hardware is there.

The close waits for nothing but the gate, and through it for the transport write lock.

**Not yet (step 4):** `FinalizePlugin` asks each device to blank itself, running that device's drivers on the finalising thread. A device's `End` blanks outside any gate. Until then, teardown is as [Device Settings Lifecycle](device-settings-lifecycle.md#teardown-ordering) describes. **Not yet (step 1):** the core is disposed without being retired, so a late update call can reopen the transport afterwards.

## Review floor

Concurrency review of this code can always find one more interleaving. This is the line between what gets fixed and what gets written down.

**Fix it** when a finding can:

- lose or corrupt stored user data;
- leave the hardware wrong until someone intervenes: LEDs stuck on, a frozen display, ITM dead until restart;
- write to hardware other than the hardware the writer was admitted for, where an admission or delivery check could have refused it;
- admit a write whose authority was already revoked, or let two parties both believe they own a channel;
- deadlock, or block the plugin update thread or the UI thread beyond the waits this page states;
- let an exception escape into a SimHub callback;
- break a rule on this page.

A finding that meets this list is fixed however it was found. Reading the code is enough.

**Record it, do not patch it,** only when all of these hold:

- there is a recovery path and it has been shown to work, by a test that forces the interleaving or by a captured log;
- it writes to no hardware but its own, leaves nothing wrong once it has recovered, and breaks no ownership;
- what remains is small: a device reported Connected or Scanning one frame late, a status line one tick old, one operation admitted just before a transition and landing just after it, or two SimHub properties read either side of a commit.

A finding that depends on host behaviour nobody has observed is recorded as a question, with what would settle it.

Recorded findings go under [Known limits](#known-limits), one line each.

Three rules apply to every fix:

- A fix for a finding that crosses actors does not add a flag, latch, counter or lock to one of them. Per-actor state is new shared surface for the next review. The fix goes where the actors meet: the snapshot, the gate or the transport. If no such place fits, change the contract.
- A race test must force its interleaving. A test that can pass without the two sides ever overlapping proves nothing, and making it deterministic counts as a fix.
- A change that adds a lock, a thread, or a field read from more than one thread updates this page in the same pull request.

### Known limits

What the host cannot close:

- The first changed identity reading lags the physical change. An operation admitted before that reading is processed can reach the new hardware. The next epoch's full repaint corrects it.
- The firmware does not say which wheel sent an ITM push. A push on the wire when the wheel changes can arrive after the queues were flushed. The lifecycle's rule for adopting handles limits what it can do; how late such a push can arrive is not yet measured.
- Whether the LEDs and the display keep their state across a USB reconnect is not known. The gate drops its lit records when a connection ends, so anything still lit then is left until a device repaints it.
- While a reading is settling, nothing can say what hardware is attached. A plugin closed in that window leaves what a device or a hold had lit, rather than blank a wheel it cannot identify.
- A hold writes without waiting for a stable identity, so a write admitted while a reading is settling has no target the gate can verify. Its lit record is dropped without a blank if the hold is released or the plugin closes before the hold writes again on a stable identity. If that write reached hardware that stays attached, the channel stays lit until a device repaints it or a hold writes to it again.
- The gate knows an attachment only by the raw codes the base reports. Two wheels that report the same codes, such as two of one model, are one target. If one replaces the other and the settle ends where it began, a blank owed to the first is sent to the second.

Recorded:

- An operation admitted just before a new snapshot is published completes on the old one. It is one operation, and it is finished before anything from the new epoch is admitted, so the new epoch's full repaint follows it. It can land late if a tuning read holds the transport.
- The plugin update thread's own transport writes (the identity keep-alive, the SRM query, a pending cleanup) can wait behind a tuning read or a diagnostics capture.
- The transport's health can turn false before the snapshot says disconnected. Writes in that window fail and are retried or dropped.
- Tuning and the diagnostics capture are not bound to a connection. An exchange that straddles a reconnect fails or times out.
- `FanaBridge.*` properties are read one at a time by SimHub and can straddle a commit.

## Gaps today

Against `main`. Each row is removed by the step that closes it.

| Gap | Closed by |
|-----|-----------|
| Identity, stability and capabilities are separate properties, written one at a time and read separately. | Step 1 |
| No state lock: an identity commit and a capability refresh can interleave and overwrite each other. | Step 1 |
| The override map is changed in place on the UI thread and read through a callback on the plugin update thread. | Step 1 |
| Connection, product id and product name are outside the snapshot; Control Mapper reads them apart from the wheel identity. | Step 1 |
| Capability resolution logs, and can load profiles from disk, on the path that will run under the state lock. | Step 1 |
| LED driver construction and the restart notice resolve capabilities separately from the read that decided to use them. | Step 1 |
| After connecting, the identity reads as stable while the first reading is still settling. | Step 1 |
| A disconnect closes the transport before the state says disconnected. | Step 1 |
| `WheelChanged` passes the wheelbase, and handlers read its properties one at a time. | Step 1 |
| The wheelbase logs while holding the ITM staging lock. | Step 1 |
| Nothing fences connection work: a late update call can reconnect a disposed wheelbase, and two update threads can overlap after a manager restart. | Step 1 |
| The LED driver writes from a thread-pool task, remembers a frame before it is accepted, and waits up to two seconds for the task on clear and dispose. | Step 2 |
| The settings-change handler updates a live display driver from the UI thread. | Step 2 |
| A device's lock covers only the drawing part of a frame; its `End` and its stop-driving path run outside it. | Step 2 |
| A device whose attachment has no profile falls back to its registered capabilities and keeps driving. | Step 3 |
| A channel a device's capabilities drop is not blanked. Step 3 stops the writes to it and leaves its last frame. | Step 4 |
| The LED manager cannot tell "no profile" from "nothing seen yet". The LED colour notice reads capabilities once. | Step 3 |
| No output gate in Core. Encoders are public on the plugin, and every writer decides for itself whether it may write. | Step 4 |
| No epochs. Writes are not checked against the attachment they were computed for, and multi-report operations are not ordered against transitions. | Step 4 |
| A send is not tied to the connection it was meant for: an operation can continue onto the streams of a later connection. | Step 4 |
| A device that loses its attachment blanks unconditionally, possibly onto different hardware. Blank retries are latches held by each device. | Step 4 |
| Staged ITM reports are collected without a lease and cleared after the new identity is visible. The raw ITM queue is not flushed at a transition. The ITM encoder's buffer is unlocked. | Step 4 |
| The ITM lifecycle adopts handles from any push, including one that arrived before its bring-up began. | Step 4 |
| Teardown runs each device's drivers on the finalising thread; a device's `End` blanks outside any gate; "lit" is a latch on the display encoder plus a flag per device. | Step 4 |
| The wizard and the display test write from the UI thread, a timer thread and the thread pool with no token, and are released before their workers stop. | Step 5 |
| Device frames poll two flags to stay out of the wizard's and the display test's way. | Step 5 |
| Nothing tells a driver to repaint after someone else wrote to its channel. | Step 5 |

## Steps

1. **State snapshot, state lock and connection turn.** One immutable snapshot including the connection, published under a leaf state lock; copy-on-write overrides; pure resolution; the disconnected snapshot published before the transport closes; connection work fenced by the turn and by retirement. Revises [#147](https://github.com/kelchm/FanaBridge/pull/147). Part of [#137](https://github.com/kelchm/FanaBridge/issues/137).
2. **Synchronous LED driver.** LED frames are written on the device's update thread, and nothing but that device's frames and `End` touches its drivers.
3. **Empty capabilities.** A device whose attachment has no profile, or fewer channels, stops driving instead of falling back. The step removes writes and adds none. Part of [#137](https://github.com/kelchm/FanaBridge/issues/137).
4. **Output gate.** Leases, both epochs, whole-operation admission, sends bound to their connection, lit records and pending cleanup, epoch-checked ITM input, teardown through the gate, and the encoders off the plugin's public surface. A channel a device's capabilities drop is released, which blanks it. The wizard and the display test write through the gate from here on, with holds that have no token yet. Fixes [#137](https://github.com/kelchm/FanaBridge/issues/137).
5. **Holds.** The wizard and the display test take tokened holds with a gated release; drivers repaint on an output revision change; device frames stop polling flags.
