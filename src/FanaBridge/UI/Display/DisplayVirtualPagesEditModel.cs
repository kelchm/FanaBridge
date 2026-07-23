using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>One virtual-page pill in the editor strip.</summary>
    internal sealed class VirtualPagePillModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Index { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>
    /// The testable core of the Virtual pages editor: holds the working
    /// <see cref="DisplayCustomizationConfig"/> and turns every screen add / update /
    /// delete / base-row edit into a NEW document (immutable-after-load — Screens list
    /// and the mutated screen instance are fresh; everything else carried by reference).
    /// No SimHub or WPF — sibling of <see cref="DisplayTriggersEditModel"/>.
    /// </summary>
    internal sealed class DisplayVirtualPagesEditModel
    {
        /// <summary>Synthetic choice id for the base row's Blank option.</summary>
        public const string BaseBlankId = "__blank__";

        private DisplayCustomizationConfig _config;
        private string _selectedScreenId;

        public DisplayVirtualPagesEditModel(DisplayCustomizationConfig current)
        {
            _config = current;
            var screens = Screens;
            _selectedScreenId = screens.Count > 0 ? screens[0].Id : null;
        }

        /// <summary>The current working document (null until the first screen is added
        /// on an empty start).</summary>
        public DisplayCustomizationConfig Config => _config;

        /// <summary>Legacy screens in document order (empty when there is no document).</summary>
        public IReadOnlyList<LegacyScreen> Screens
            => _config?.Legacy?.Screens ?? (IReadOnlyList<LegacyScreen>)Array.Empty<LegacyScreen>();

        /// <summary>Selected screen id, or null when the library is empty.</summary>
        public string SelectedScreenId => _selectedScreenId;

        /// <summary>The selected screen instance, or null.</summary>
        public LegacyScreen SelectedScreen
        {
            get
            {
                if (_selectedScreenId == null)
                    return null;
                foreach (var s in Screens)
                    if (string.Equals(s.Id, _selectedScreenId, StringComparison.Ordinal))
                        return s;
                return null;
            }
        }

        /// <summary>Configured base screen id, or null for Blank.</summary>
        public string BaseScreenId => _config?.Legacy?.BaseScreenId;

        // ── Pills / selection ────────────────────────────────────────────

        public IReadOnlyList<VirtualPagePillModel> PagePills()
        {
            var screens = Screens;
            var result = new List<VirtualPagePillModel>(screens.Count);
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                result.Add(new VirtualPagePillModel
                {
                    Id = s.Id,
                    Name = DisplayName(s),
                    Index = i + 1,
                    IsSelected = string.Equals(s.Id, _selectedScreenId, StringComparison.Ordinal),
                });
            }
            return result;
        }

        public void SelectScreen(string id)
        {
            if (id == null)
                return;
            foreach (var s in Screens)
            {
                if (string.Equals(s.Id, id, StringComparison.Ordinal))
                {
                    _selectedScreenId = id;
                    return;
                }
            }
        }

        // ── Mutations (each returns the NEW document) ────────────────────

        /// <summary>Appends a new static Text screen (default name/text "NEW") and
        /// selects it. A GUID id is assigned.</summary>
        public DisplayCustomizationConfig AddScreen()
        {
            string id = Guid.NewGuid().ToString("N");
            var screen = new LegacyScreen
            {
                Id = id,
                Name = "NEW",
                Text = "NEW",
                ContentKind = LegacyContentKind.Text,
                Effect = LegacyEffect.None,
            };
            var list = CurrentScreens();
            list.Add(screen);
            var cfg = CommitScreens(list, BaseScreenId);
            _selectedScreenId = id;
            return cfg;
        }

        /// <summary>Removes the screen and any base-screen reference to it. Immediate —
        /// no confirm (undo deferred). Selects a neighbour when the removed row was
        /// selected. No-op when the id is unknown.</summary>
        public DisplayCustomizationConfig RemoveScreen(string id)
        {
            var list = CurrentScreens();
            int i = IndexOf(list, id);
            if (i < 0)
                return _config;

            list.RemoveAt(i);
            string baseId = BaseScreenId;
            if (string.Equals(baseId, id, StringComparison.Ordinal))
                baseId = null;

            if (string.Equals(_selectedScreenId, id, StringComparison.Ordinal))
            {
                if (list.Count == 0)
                    _selectedScreenId = null;
                else
                    _selectedScreenId = list[Math.Min(i, list.Count - 1)].Id;
            }

            return CommitScreens(list, baseId);
        }

        /// <summary>Sets the display name of a screen (keeps id/text/kind/effect/source).</summary>
        public DisplayCustomizationConfig SetName(string id, string name)
            => Mutate(id, s => s.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim());

        /// <summary>Sets content kind. Text is required only for Text/Message; dynamic
        /// kinds keep Text for round-trip but the validator ignores it. Switching to
        /// Property without a source is allowed in the editor (preview blanks; Apply
        /// may skip the screen with a warn).</summary>
        public DisplayCustomizationConfig SetContentKind(string id, LegacyContentKind kind)
        {
            if (kind == LegacyContentKind.Unknown)
                return _config;
            return Mutate(id, s =>
            {
                s.ContentKind = kind;
                // Seed a usable default text when entering Text/Message from a blank
                // dynamic kind so the preview/validator have something to show.
                if ((kind == LegacyContentKind.Text || kind == LegacyContentKind.Message)
                    && string.IsNullOrEmpty(s.Text))
                    s.Text = string.IsNullOrEmpty(s.Name) ? "NEW" : TruncateText(s.Name, 3);
            });
        }

        /// <summary>Sets static text (Text/Message kinds). Does not validate charset —
        /// ApplyDisplayConfig's normalizer warns and may skip.</summary>
        public DisplayCustomizationConfig SetText(string id, string text)
            => Mutate(id, s => s.Text = text);

        /// <summary>Sets the Property-kind source. Other kinds ignore source on the wire;
        /// the editor still stores it so a round-trip to Property keeps the pick.</summary>
        public DisplayCustomizationConfig SetSource(string id, PropertyKind kind, string name)
            => Mutate(id, s =>
            {
                if (string.IsNullOrEmpty(name))
                    s.Source = null;
                else
                    s.Source = new PropertySpec
                    {
                        Kind = kind,
                        Name = name,
                        ExtensionData = s.Source?.ExtensionData,
                    };
            });

        /// <summary>Sets the presentation effect (None/Scroll/Blink). Flash is not offered
        /// in the UI (validator coerces it to Blink if loaded from disk).</summary>
        public DisplayCustomizationConfig SetEffect(string id, LegacyEffect effect)
        {
            if (effect == LegacyEffect.Unknown || effect == LegacyEffect.Flash)
                return _config;
            return Mutate(id, s => s.Effect = effect);
        }

        /// <summary>Sets the base screen ("When nothing's firing → …"). Null / blank id
        /// → Blank (null BaseScreenId).</summary>
        public DisplayCustomizationConfig SetBaseScreenId(string screenId)
        {
            if (string.IsNullOrEmpty(screenId)
                || string.Equals(screenId, BaseBlankId, StringComparison.Ordinal))
                return CommitScreens(CurrentScreens(), null);

            // Only accept an id that is currently in the library.
            bool found = false;
            foreach (var s in Screens)
            {
                if (string.Equals(s.Id, screenId, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return _config;
            return CommitScreens(CurrentScreens(), screenId);
        }

        // ── Preview / labels ─────────────────────────────────────────────

        /// <summary>LIVE preview segment frame for the selected screen (pure model).</summary>
        public byte[] PreviewSegments(long nowMs = 0)
            => SevenSegmentFaceRender.PreviewSegments(SelectedScreen, nowMs);

        /// <summary>Whether the text field is shown for the selected kind.</summary>
        public static bool ShowsTextField(LegacyContentKind kind)
            => kind == LegacyContentKind.Text || kind == LegacyContentKind.Message;

        /// <summary>Whether the property picker row is shown for the selected kind.</summary>
        public static bool ShowsPropertyField(LegacyContentKind kind)
            => kind == LegacyContentKind.Property;

        public static string ContentKindLabel(LegacyContentKind kind)
        {
            switch (kind)
            {
                case LegacyContentKind.Text: return "Text";
                case LegacyContentKind.Speed: return "Speed";
                case LegacyContentKind.Gear: return "Gear";
                case LegacyContentKind.GearBrackets: return "Gear (brackets)";
                case LegacyContentKind.Rpm: return "RPM";
                case LegacyContentKind.Position: return "Position";
                case LegacyContentKind.Fuel: return "Fuel";
                case LegacyContentKind.Message: return "Message";
                case LegacyContentKind.Property: return "Property";
                default: return kind.ToString();
            }
        }

        public static string EffectLabel(LegacyEffect effect)
        {
            switch (effect)
            {
                case LegacyEffect.None: return "None";
                case LegacyEffect.Scroll: return "Scroll";
                case LegacyEffect.Blink: return "Blink";
                default: return effect.ToString();
            }
        }

        /// <summary>CONTENT dropdown options (the four absorbed modes + Rpm/Position/Fuel/
        /// Message/Property + static Text). Unknown/Flash never offered.</summary>
        public static ChoiceList ContentKindChoices(LegacyContentKind selected)
        {
            var b = ChoiceList.Build();
            foreach (var k in ContentKinds)
                b.Add(EnumText.Write(k), ContentKindLabel(k));
            return b.Selected(EnumText.Write(selected == LegacyContentKind.Unknown
                ? LegacyContentKind.Text
                : selected));
        }

        /// <summary>EFFECT dropdown (None / Scroll / Blink).</summary>
        public static ChoiceList EffectChoices(LegacyEffect selected)
        {
            var b = ChoiceList.Build();
            b.Add(EnumText.Write(LegacyEffect.None), EffectLabel(LegacyEffect.None));
            b.Add(EnumText.Write(LegacyEffect.Scroll), EffectLabel(LegacyEffect.Scroll));
            b.Add(EnumText.Write(LegacyEffect.Blink), EffectLabel(LegacyEffect.Blink));
            LegacyEffect sel = selected == LegacyEffect.Unknown || selected == LegacyEffect.Flash
                ? LegacyEffect.None
                : selected;
            return b.Selected(EnumText.Write(sel));
        }

        /// <summary>Base-row choices: Blank + each library screen by display name.</summary>
        public ChoiceList BaseScreenChoices()
        {
            var b = ChoiceList.Build();
            b.Add(BaseBlankId, "Blank");
            foreach (var s in Screens)
                b.Add(s.Id, DisplayName(s));
            string selected = string.IsNullOrEmpty(BaseScreenId) ? BaseBlankId : BaseScreenId;
            // If the configured base was dropped from the library, show Blank selected.
            bool known = string.Equals(selected, BaseBlankId, StringComparison.Ordinal);
            if (!known)
            {
                foreach (var s in Screens)
                    if (string.Equals(s.Id, selected, StringComparison.Ordinal))
                    {
                        known = true;
                        break;
                    }
            }
            if (!known)
                selected = BaseBlankId;
            return b.Selected(selected);
        }

        /// <summary>Display name for a screen: Name, else Text, else id.</summary>
        public static string DisplayName(LegacyScreen screen)
        {
            if (screen == null)
                return "?";
            if (!string.IsNullOrWhiteSpace(screen.Name))
                return screen.Name.Trim();
            if (!string.IsNullOrWhiteSpace(screen.Text))
                return screen.Text.Trim();
            return screen.Id ?? "?";
        }

        /// <summary>Survivor screens rules may target: every library entry whose content
        /// kind is known (unknown-kind screens stay in the document for EnumText survival
        /// but are not offered as SHOW targets — matching the validator's survivor set).</summary>
        public IReadOnlyList<LegacyScreen> SurvivorScreens()
        {
            var result = new List<LegacyScreen>();
            foreach (var s in Screens)
            {
                if (s != null && s.ContentKind != LegacyContentKind.Unknown)
                    result.Add(s);
            }
            return result;
        }

        // ── Internals ────────────────────────────────────────────────────

        private static readonly LegacyContentKind[] ContentKinds =
        {
            LegacyContentKind.Text,
            LegacyContentKind.Speed,
            LegacyContentKind.Gear,
            LegacyContentKind.GearBrackets,
            LegacyContentKind.Rpm,
            LegacyContentKind.Position,
            LegacyContentKind.Fuel,
            LegacyContentKind.Message,
            LegacyContentKind.Property,
        };

        private List<LegacyScreen> CurrentScreens()
            => new List<LegacyScreen>(Screens);

        private DisplayCustomizationConfig Mutate(string id, Action<LegacyScreen> edit)
        {
            var list = CurrentScreens();
            int i = IndexOf(list, id);
            if (i < 0)
                return _config;
            var clone = CloneScreen(list[i]);
            edit(clone);
            list[i] = clone;
            return CommitScreens(list, BaseScreenId);
        }

        private DisplayCustomizationConfig CommitScreens(List<LegacyScreen> screens, string baseScreenId)
        {
            var src = _config;
            var cfg = new DisplayCustomizationConfig
            {
                SchemaVersion = src?.SchemaVersion ?? DisplayCustomizationConfig.CurrentSchemaVersion,
                ProfileId = src?.ProfileId,
                Itm = src?.Itm ?? new ItmRuleSet(),
                Legacy = new LegacyRuleSet
                {
                    Rules = src?.Legacy?.Rules ?? new List<DisplayRule>(),
                    Screens = screens,
                    BaseScreenId = baseScreenId,
                    ExtensionData = src?.Legacy?.ExtensionData,
                },
                FieldMappings = src?.FieldMappings ?? new Dictionary<ushort, FieldMapping>(),
                ExtensionData = src?.ExtensionData,
            };
            _config = cfg;
            return cfg;
        }

        private static int IndexOf(List<LegacyScreen> list, string id)
        {
            if (id == null)
                return -1;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Id, id, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static LegacyScreen CloneScreen(LegacyScreen s)
        {
            var clone = new LegacyScreen
            {
                Id = s.Id,
                Name = s.Name,
                Text = s.Text,
                ContentKindRaw = s.ContentKindRaw,
                EffectRaw = s.EffectRaw,
                InRotation = s.InRotation,
                Format = s.Format,
                ExtensionData = s.ExtensionData,
            };
            // Force the cached enums from raw so a subsequent ContentKind/Effect set writes
            // both raw and cache (same EnumText pattern as the rest of the schema).
            _ = clone.ContentKind;
            _ = clone.Effect;
            if (s.Source != null)
            {
                clone.Source = new PropertySpec
                {
                    KindRaw = s.Source.KindRaw,
                    Name = s.Source.Name,
                    ExtensionData = s.Source.ExtensionData,
                };
            }
            return clone;
        }

        private static string TruncateText(string text, int maxPositions)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxPositions)
                return text;
            return text.Substring(0, maxPositions);
        }
    }
}
