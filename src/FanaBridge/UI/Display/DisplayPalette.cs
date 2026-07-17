using System.Windows.Media;

namespace FanaBridge.UI
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

        internal static readonly FontFamily Mono = new FontFamily("Consolas");

        private static SolidColorBrush Frozen(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex);
            brush.Freeze();
            return brush;
        }
    }
}
