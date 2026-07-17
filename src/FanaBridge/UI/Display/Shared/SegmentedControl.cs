using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// A code-built segmented toggle (the design's segmented control): a rounded dark
    /// container holding N side-by-side segments with exactly one selected — the selected
    /// segment fills <see cref="DisplayPalette.AccentBg"/> with white text, the rest sit on
    /// transparent with <see cref="DisplayPalette.ToggleIdleText"/>. It replaces the two
    /// hand-rolled segmented implementations behaviour-preservingly: the Display tab's
    /// DISPLAY MODE header pair and the Triggers editor's ELIGIBLE strip. House style — no
    /// DataTemplate, no styles; the visual tree is built in code and every segment is
    /// keyboard-activatable (Enter/Space), exactly as the originals were.
    ///
    /// Selection is identity-based: <see cref="SetItems"/> takes (id, label) pairs,
    /// <see cref="SelectedId"/> reflects and sets the chosen segment, and
    /// <see cref="SelectionChanged"/> fires on user activation ONLY. A programmatic
    /// SelectedId set re-styles without raising the event, so a caller can mirror external
    /// state (e.g. DisplaySettings.ItmEnabled) into the control without re-entrancy.
    ///
    /// The two call sites differ only in chrome the originals set inline — segment padding,
    /// label font size, and whether the end segments round their outer corners — exposed
    /// here as <see cref="SegmentPadding"/>, <see cref="SegmentFontSize"/>, and
    /// <see cref="OuterCornerRadius"/> so each reproduces its shipped look pixel-for-pixel.
    /// </summary>
    public class SegmentedControl : Border
    {
        private readonly StackPanel _row;
        private readonly List<Segment> _segments = new List<Segment>();
        private string _selectedId;

        private sealed class Segment
        {
            public string Id;
            public Border Host;
            public TextBlock Text;
        }

        public SegmentedControl()
        {
            // The container: SegBarBg fill, SegBorder outline, 5px radius, left-aligned —
            // the same values both original strips used (the mode header inlined #1A1A1B /
            // #45454A, which are exactly SegBarBg / SegBorder).
            Background = DisplayPalette.SegBarBg;
            BorderBrush = DisplayPalette.SegBorder;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(5);
            HorizontalAlignment = HorizontalAlignment.Left;
            _row = new StackPanel { Orientation = Orientation.Horizontal };
            Child = _row;
        }

        /// <summary>Per-segment padding (14,6 for the mode header; 13,5 for ELIGIBLE).
        /// Read by the next <see cref="SetItems"/>.</summary>
        public Thickness SegmentPadding { get; set; } = new Thickness(13, 5, 13, 5);

        /// <summary>Segment label font size (12 for the mode header; 11.5 for ELIGIBLE).</summary>
        public double SegmentFontSize { get; set; } = 11.5;

        /// <summary>When &gt; 0, the first and last segments round their outer corners by this
        /// radius — the mode header's rounded 4px ends. 0 (default) leaves the segments square
        /// inside the rounded container, matching the ELIGIBLE strip.</summary>
        public double OuterCornerRadius { get; set; }

        /// <summary>Raised when the user activates a segment (mouse or keyboard) with its id.
        /// NOT raised by a programmatic <see cref="SelectedId"/> set.</summary>
        public event EventHandler<string> SelectionChanged;

        public string SelectedId
        {
            get { return _selectedId; }
            set
            {
                _selectedId = value;
                ApplySelection();
            }
        }

        /// <summary>Populate the segments in order. Re-reads the chrome properties, so set
        /// <see cref="SegmentPadding"/> / <see cref="SegmentFontSize"/> /
        /// <see cref="OuterCornerRadius"/> first.</summary>
        public void SetItems(IReadOnlyList<(string id, string label)> items)
        {
            _row.Children.Clear();
            _segments.Clear();
            int n = items != null ? items.Count : 0;
            for (int i = 0; i < n; i++)
            {
                string segId = items[i].id;
                var text = new TextBlock
                {
                    Text = items[i].label,
                    FontSize = SegmentFontSize,
                    Foreground = DisplayPalette.ToggleIdleText,
                };
                var host = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = SegmentPadding,
                    CornerRadius = SegmentCorner(i, n),
                    Cursor = Cursors.Hand,
                    Focusable = true,
                    Child = text,
                };
                host.MouseLeftButtonUp += (s, e) => Activate(segId);
                host.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space)
                    {
                        Activate(segId);
                        e.Handled = true;
                    }
                };
                _segments.Add(new Segment { Id = segId, Host = host, Text = text });
                _row.Children.Add(host);
            }
            ApplySelection();
        }

        private CornerRadius SegmentCorner(int index, int count)
        {
            double r = OuterCornerRadius;
            if (r <= 0 || count == 0)
                return new CornerRadius(0);
            bool first = index == 0;
            bool last = index == count - 1;
            return new CornerRadius(first ? r : 0, last ? r : 0, last ? r : 0, first ? r : 0);
        }

        private void Activate(string id)
        {
            _selectedId = id;
            ApplySelection();
            var handler = SelectionChanged;
            if (handler != null)
                handler(this, id);
        }

        private void ApplySelection()
        {
            foreach (var seg in _segments)
            {
                bool active = string.Equals(seg.Id, _selectedId, StringComparison.Ordinal);
                seg.Host.Background = active ? DisplayPalette.AccentBg : Brushes.Transparent;
                seg.Text.Foreground = active ? Brushes.White : DisplayPalette.ToggleIdleText;
            }
        }
    }
}
