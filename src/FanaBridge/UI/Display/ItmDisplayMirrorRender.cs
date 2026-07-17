using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Display.Session;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>What the mirror panel shows for the current snapshot state.</summary>
    internal enum MirrorPanelState
    {
        /// <summary>Not synced / ITM off / synced on a page the twin has no layout
        /// for: a dimmed empty panel (the state caption lives in the card header,
        /// outside the panel — the panel stays clean).</summary>
        Empty,

        /// <summary>The legacy ITM page: no telemetry field slots; the panel shows a
        /// centered muted caption (its 3-character surface is out of scope here).</summary>
        Legacy,

        /// <summary>A synced telemetry page: slots, gear glyph, and speed.</summary>
        Live,
    }

    /// <summary>One field of a mirror slot, ready to draw: its own label (dual TC/ABS
    /// style, null otherwise), the display string, and the DRS-dot flag (dots draw as
    /// circles, not text).</summary>
    internal sealed class MirrorFieldModel
    {
        public ushort ParamId { get; set; }

        /// <summary>The field's own label, or null when the slot label covers it.</summary>
        public string Label { get; set; }

        /// <summary>The rendered display string (value, placeholder, or dot constant).</summary>
        public string Value { get; set; }

        /// <summary>True when the field draws as a DRS dot (filled/hollow circle).</summary>
        public bool IsDot { get; set; }

        /// <summary>For dot fields: filled (on) vs hollow (off).</summary>
        public bool DotFilled { get; set; }
    }

    /// <summary>One of the four field slots, ready to draw.</summary>
    internal sealed class MirrorSlotModel
    {
        public ItmSlotPosition Position { get; set; }

        /// <summary>The slot-level label, or null when each field carries its own.</summary>
        public string Label { get; set; }

        public List<MirrorFieldModel> Fields { get; } = new List<MirrorFieldModel>();

        public bool IsDual => Fields.Count == 2;
    }

    /// <summary>The mirror control's full render model for one snapshot.</summary>
    internal sealed class MirrorModel
    {
        public MirrorPanelState PanelState { get; set; }

        /// <summary>The populated slots (only those the page has); empty unless Live.</summary>
        public List<MirrorSlotModel> Slots { get; } = new List<MirrorSlotModel>();

        /// <summary>The gear character for the segmented glyph, or null/empty for blank.</summary>
        public string GearText { get; set; }

        /// <summary>The center-zone speed string, or null for blank.</summary>
        public string SpeedText { get; set; }
    }

    /// <summary>
    /// Maps a <see cref="DisplayValuesSnapshot"/> into the mirror control's render model
    /// — every rendering decision that is not literally WPF lives here, so it is
    /// unit-testable with no UI thread (the same pattern as
    /// <see cref="DisplayOverviewRender"/>). Pure functions of their inputs.
    /// </summary>
    internal static class ItmDisplayMirrorRender
    {
        /// <summary>
        /// The panel content for a snapshot: a live slot model while synced on a
        /// telemetry page (values or placeholders — the snapshot's field strings already
        /// carry whichever applies), the legacy caption on the legacy page, and the
        /// dimmed empty panel for everything else (off, bring-up, switching, recovery —
        /// the header's state caption tells the user why).
        /// </summary>
        public static MirrorModel Build(DisplayValuesSnapshot snapshot)
        {
            var model = new MirrorModel();
            if (snapshot == null || snapshot.State != ItmLifecycleState.Synced)
                return model;   // Empty

            if (snapshot.Page == ItmPage.Legacy)
            {
                model.PanelState = MirrorPanelState.Legacy;
                return model;
            }

            if (snapshot.LeftTop == null && snapshot.LeftBottom == null
                && snapshot.RightTop == null && snapshot.RightBottom == null)
                return model;   // synced but no page adopted yet — nothing to draw

            model.PanelState = MirrorPanelState.Live;
            AddSlot(model, ItmSlotPosition.LeftTop, snapshot.LeftTop);
            AddSlot(model, ItmSlotPosition.LeftBottom, snapshot.LeftBottom);
            AddSlot(model, ItmSlotPosition.RightTop, snapshot.RightTop);
            AddSlot(model, ItmSlotPosition.RightBottom, snapshot.RightBottom);
            model.GearText = snapshot.GearText;
            model.SpeedText = snapshot.SpeedText;
            return model;
        }

        private static void AddSlot(MirrorModel model, ItmSlotPosition position,
            DisplayValueSlot slot)
        {
            if (slot == null)
                return;
            var slotModel = new MirrorSlotModel { Position = position, Label = slot.Label };
            foreach (var field in slot.Fields)
            {
                bool isDot = field.Value == ItmValueRenderer.DrsDotOn
                    || field.Value == ItmValueRenderer.DrsDotOff;
                slotModel.Fields.Add(new MirrorFieldModel
                {
                    ParamId = field.ParamId,
                    Label = field.Label,
                    Value = field.Value,
                    IsDot = isDot,
                    DotFilled = field.Value == ItmValueRenderer.DrsDotOn,
                });
            }
            model.Slots.Add(slotModel);
        }

        /// <summary>
        /// The segment bit pattern for the gear glyph (bit n = segment n, the shared
        /// 7-segment encoding). Blank when there is no gear to show.
        /// </summary>
        public static byte GearSegmentBits(string gearText)
            => string.IsNullOrEmpty(gearText)
                ? SevenSegment.Blank
                : SevenSegment.CharToSegment(gearText[0]);

        /// <summary>
        /// The small state caption shown in the LIVE card's header while the panel is
        /// not live — null while synced on a known page (the panel speaks for itself,
        /// like the hardware). Wordings match <see cref="DisplayOverviewRender.CurrentPageCaption"/>.
        /// </summary>
        public static string StateCaption(DisplayValuesSnapshot snapshot)
        {
            if (snapshot == null)
                return "ITM off";
            switch (snapshot.State)
            {
                case ItmLifecycleState.Synced:
                    // Synced on a page the catalog doesn't know (the firmware can
                    // subscribe sets outside the built-in layouts): the hardware is
                    // showing values the twin has no layout for, so the panel stays
                    // empty — the header must say why, like every other empty state.
                    return snapshot.Page == null ? "Unrecognized page" : null;
                case ItmLifecycleState.Idle: return "ITM idle";
                case ItmLifecycleState.Disabled: return "ITM off";
                case ItmLifecycleState.BringUp:
                case ItmLifecycleState.AwaitPush: return "Bringing up…";
                case ItmLifecycleState.Switching: return "Switching page…";
                case ItmLifecycleState.Recovery: return "Recovering…";
                default: return "Display unavailable";
            }
        }

        /// <summary>
        /// The "Page N · Name" caption under the mirror: from the values snapshot's page
        /// when one is known, else the pre-mirror status-line path
        /// (<see cref="DisplayOverviewRender.CurrentPageCaption"/>) so the caption keeps
        /// working when no values snapshot exists.
        /// </summary>
        public static string PageCaption(DisplayValuesSnapshot values, string itmStatus,
            byte itmDeviceId)
        {
            if (values?.PageName != null)
            {
                if (values.WirePage == 0 || values.PageName == "Page " + values.WirePage)
                    return values.PageName;   // uncataloged page — no name to append
                return "Page " + values.WirePage + " · " + values.PageName;
            }
            return DisplayOverviewRender.CurrentPageCaption(itmStatus, itmDeviceId);
        }
    }
}
