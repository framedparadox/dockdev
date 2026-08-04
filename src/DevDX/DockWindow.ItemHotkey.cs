using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDX;

/// <summary>
/// Per-item shortcuts: a system-wide combination that launches (or focuses) one pinned item
/// without the dock having to be visible at all. Partial of <see cref="DockWindow"/>.
/// <para>
/// The combination lives on the item (<see cref="ToolDockItem.Hotkey"/>) and is registered by
/// <see cref="DevDxManager.ApplyItemHotkeys"/>, which owns every registration DevDX holds. This
/// file is only the UI for assigning one.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>
    /// The <c>Shortcut ▸</c> submenu for an item: what it currently has, a way to assign a new
    /// one, and — when the master switch is off — a note saying why nothing is firing. Separators
    /// Separators are excluded by the caller; a separator has nothing to launch.
    /// </summary>
    private MenuFlyoutSubItem BuildItemHotkeyMenu(FrameworkElement target, ToolDockItem item)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get("Menu.Shortcut") };

        var assign = new MenuFlyoutItem
        {
            Text = HotkeyGesture.TryParse(item.Hotkey, out var current)
                ? Loc.Format("Menu.ShortcutAssigned", current.ToString())
                : Loc.Get("Menu.ShortcutAssign"),
        };
        assign.Click += (_, _) => ShowItemHotkeyFlyout(target, item);
        sub.Items.Add(assign);

        if (!string.IsNullOrWhiteSpace(item.Hotkey))
        {
            var clear = new MenuFlyoutItem { Text = Loc.Get("Menu.ShortcutClear") };
            clear.Click += (_, _) => _manager.SetItemHotkey(item, null);
            sub.Items.Add(clear);
        }

        // The per-item shortcuts are off by default (they claim system-wide combinations), so an
        // assignment that quietly does nothing is the likeliest confusion here. Say so, and offer
        // the switch rather than sending the user to Settings to find it.
        if (!_manager.Config.ItemHotkeysEnabled)
        {
            sub.Items.Add(new MenuFlyoutSeparator());
            var enable = new MenuFlyoutItem { Text = Loc.Get("Menu.ShortcutEnableAll") };
            enable.Click += (_, _) => _manager.SetItemHotkeysEnabled(true);
            sub.Items.Add(enable);
        }

        return sub;
    }

    /// <summary>
    /// The capture flyout: press a combination and it is assigned. Anchored on the item's own cell,
    /// like every other in-place edit on the dock.
    /// </summary>
    private void ShowItemHotkeyFlyout(FrameworkElement target, ToolDockItem item)
    {
        var capture = new HotkeyCaptureButton
        {
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Named for the item, so Narrator says which icon the combination is being bound to —
            // the flyout is anchored on that icon, which conveys it visually and not otherwise.
            Label = item.DisplayName,
            Gesture = HotkeyGesture.TryParse(item.Hotkey, out var current) ? current : null,
        };

        var message = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            Text = Loc.Get("Hotkey.ItemHint"),
        };

        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };
        panel.Children.Add(FlyoutHeader(Loc.Get("Menu.Shortcut")));
        panel.Children.Add(capture);
        panel.Children.Add(message);

        var flyout = new Flyout { Content = panel };

        capture.NeedsModifier += () => message.Text = Loc.Get("Hotkey.NeedModifier");
        capture.Assigned += gesture =>
        {
            bool registered = _manager.SetItemHotkey(item, gesture);
            if (gesture is not null && !registered)
            {
                // Keep the flyout open on a clash: the user's next move is to try another
                // combination, and closing it would make them re-open the menu to do that.
                message.Text = Loc.Get("Hotkey.Taken");
                return;
            }
            flyout.Hide();
        };

        // The pointer is about to leave the strip for the flyout; hold auto-hide out the same way
        // the icon picker does.
        PauseAutoHideForDrag();
        flyout.Closed += (_, _) => ResumeAutoHideAfterDrag();

        flyout.ShowAt(target);
        capture.Focus(FocusState.Programmatic);
    }
}
