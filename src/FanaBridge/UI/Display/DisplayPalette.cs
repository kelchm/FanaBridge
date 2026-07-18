using System.Windows.Media;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// The Display tab's shared brush + font palette (the design mock's SimHub-dark
    /// values), frozen once. Both the Display tab shell (Overview rendering) and the
    /// per-view editors reference these — a single source so the collapsed Overview
    /// rows and the live editor rows can never drift apart.
    /// </summary>
    internal static class DisplayPalette
    {
        // ── Shell / Overview brushes ─────────────────────────────────────
        internal static readonly SolidColorBrush AccentBg = Frozen("#1E8FD5");
        /// <summary>Selected-state fill for the DISPLAY MODE "Off" segment (mock #8a5a2a).</summary>
        internal static readonly SolidColorBrush OffAccentBg = Frozen("#8A5A2A");
        internal static readonly SolidColorBrush ToggleIdleText = Frozen("#B6B6B6");
        internal static readonly SolidColorBrush RowBg = Frozen("#303032");
        internal static readonly SolidColorBrush RowBorder = Frozen("#3D3D3F");
        internal static readonly SolidColorBrush RowText = Frozen("#EAEAEA");
        internal static readonly SolidColorBrush OnScreenBg = Frozen("#22321F");
        internal static readonly SolidColorBrush OnScreenBorder = Frozen("#3F7A4A");
        internal static readonly SolidColorBrush OnScreenText = Frozen("#FFFFFF");
        internal static readonly SolidColorBrush GreenAccent = Frozen("#8FE0A8");
        internal static readonly SolidColorBrush GreenRank = Frozen("#7FCE9A");
        internal static readonly SolidColorBrush MutedRank = Frozen("#7A7A7A");
        internal static readonly SolidColorBrush ChipText = Frozen("#8F8F8F");
        internal static readonly SolidColorBrush BaseRank = Frozen("#C9A24A");
        internal static readonly SolidColorBrush BaseText = Frozen("#C8C8C8");
        internal static readonly SolidColorBrush BaseBg = Frozen("#2A2A2B");
        internal static readonly SolidColorBrush BaseDash = Frozen("#4A4A4A");
        internal static readonly SolidColorBrush AgeText = Frozen("#7A7A7A");
        internal static readonly SolidColorBrush ActivityText = Frozen("#E6E6E6");

        // ── Triggers editor brushes ──────────────────────────────────────
        internal static readonly SolidColorBrush RemoveText = Frozen("#E88F6A");
        internal static readonly SolidColorBrush AddCardBg = Frozen("#1E2833");
        internal static readonly SolidColorBrush AddCardBorder = Frozen("#2F6D9E");
        internal static readonly SolidColorBrush AddTitle = Frozen("#CFE0EC");
        internal static readonly SolidColorBrush SegBarBg = Frozen("#1A1A1B");
        internal static readonly SolidColorBrush SegBorder = Frozen("#45454A");
        internal static readonly SolidColorBrush HandColor = Frozen("#6A6A6A");
        internal static readonly SolidColorBrush PropMono = Frozen("#9FB4C4");
        internal static readonly SolidColorBrush EligChip = Frozen("#7AA88A");
        internal static readonly SolidColorBrush DetailBg = Frozen("#2A2A2B");
        internal static readonly SolidColorBrush KLabelBrush = Frozen("#8F8F8F");
        internal static readonly SolidColorBrush ChevronBrush = Frozen("#8A8A8A");

        // ── v9 Triggers workbench (dense grid + expand-to-edit drawer) ───
        internal static readonly SolidColorBrush ThHeaderBg = Frozen("#242425");
        internal static readonly SolidColorBrush TableDivider = Frozen("#333333");
        internal static readonly SolidColorBrush DrawerBg = Frozen("#141C26");
        internal static readonly SolidColorBrush DrawerBar = Frozen("#1E8FD5");
        internal static readonly SolidColorBrush DrawerInset = Frozen("#3C8CD2");
        internal static readonly SolidColorBrush DrawerSep = Frozen("#2B333A");
        internal static readonly SolidColorBrush WhenTitle = Frozen("#8FB0C8");
        internal static readonly SolidColorBrush ShowTitle = Frozen("#A2C8A8");
        internal static readonly SolidColorBrush SubLabel = Frozen("#9A9A9A");
        internal static readonly SolidColorBrush TargetText = Frozen("#CFCFCF");
        internal static readonly SolidColorBrush DeleteText = Frozen("#E0836A");
        internal static readonly SolidColorBrush ThLabel = Frozen("#7F7F84");
        internal static readonly SolidColorBrush Caret = Frozen("#7FB2D8");
        internal static readonly SolidColorBrush GreenDot = Frozen("#45B85A");
        internal static readonly SolidColorBrush NowDotIdle = Frozen("#5A5A5C");
        internal static readonly SolidColorBrush FieldBg = Frozen("#2C3239");
        internal static readonly SolidColorBrush FieldBorder = Frozen("#565658");
        internal static readonly SolidColorBrush FieldText = Frozen("#E6E6E6");
        internal static readonly SolidColorBrush PropMonoField = Frozen("#9FB4C4");
        internal static readonly SolidColorBrush PencilBlue = Frozen("#6FB3DD");

        // ── Property grammar (v9; consumed by the Shared property label in 1b) ──
        internal static readonly SolidColorBrush NsDim = Frozen("#6F6F74");
        internal static readonly SolidColorBrush LeafBright = Frozen("#9FD0E6");
        /// <summary>Search-match highlight on a property label (mock #ffe9a6).</summary>
        internal static readonly SolidColorBrush MatchHighlight = Frozen("#FFE9A6");

        // ── Property picker rails (v9 phase 5) ───────────────────────────
        /// <summary>Favorites rail glyph / star-on (mock #d8c98a).</summary>
        internal static readonly SolidColorBrush FavoritesGold = Frozen("#D8C98A");
        /// <summary>"On your ITM pages" rail glyph (mock #7fce9a — same green family as GreenRank).</summary>
        internal static readonly SolidColorBrush ItmPagesGreen = Frozen("#7FCE9A");

        internal static readonly FontFamily Mono = new FontFamily("Consolas");

        private static SolidColorBrush Frozen(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex);
            brush.Freeze();
            return brush;
        }
    }
}
