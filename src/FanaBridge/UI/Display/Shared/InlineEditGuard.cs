using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// Poll-time guard for the v2 display views: a non-forced repaint must never
    /// tear down the control the user is typing in or the list a dropdown is
    /// reading from. Views skip their poll while this reports an inline edit.
    /// </summary>
    internal static class InlineEditGuard
    {
        /// <summary>
        /// True when keyboard focus sits on a text-entry or dropdown control that
        /// lives inside <paramref name="view"/> (popup content included — the walk
        /// crosses popup boundaries via the logical tree).
        /// </summary>
        internal static bool IsEditingWithin(DependencyObject view)
        {
            if (view == null)
                return false;

            var focused = Keyboard.FocusedElement as DependencyObject;
            if (!(focused is TextBoxBase)
                && !(focused is PasswordBox)
                && !(focused is ComboBox)
                && !(focused is ComboBoxItem))
            {
                return false;
            }

            for (var node = focused; node != null;)
            {
                if (ReferenceEquals(node, view))
                    return true;
                DependencyObject parent = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : null;
                node = parent ?? LogicalTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
