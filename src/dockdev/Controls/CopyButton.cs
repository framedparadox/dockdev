using dockdev.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace dockdev.Controls;

/// <summary>A button that copies text to the clipboard and shows a brief "Copied" confirmation —
/// every form-shaped result row gets one (design doc §9.2).</summary>
public sealed class CopyButton : Button
{
    private readonly FontIcon _icon = new() { Glyph = "\uE8C8", FontSize = 14 };
    private readonly TextBlock _label = new() { Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };

    /// <summary>
    /// The caption this button carries when it is not confirming a copy — the constructor's, kept
    /// as a field rather than recovered from <see cref="_label"/> at copy time. Reading it back off
    /// the label meant a second click inside the confirmation window captured "Copied" as the text
    /// to restore, and the button then said "Copied" for the rest of its life.
    /// </summary>
    private readonly string _restingLabel;

    private DispatcherQueueTimer? _resetTimer;

    public Func<string> GetText { get; set; } = () => "";

    public CopyButton(string label = "")
    {
        _icon.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
        _restingLabel = label;
        _label.Text = label;
        _panel.Children.Add(_icon);
        if (label.Length > 0)
            _panel.Children.Add(_label);
        Content = _panel;

        // An icon-only copy button still has to say what it is to Narrator and on hover.
        var name = label.Length > 0 ? label : Loc.Get("Tool.CopyOutput");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, name);
        ToolTipService.SetToolTip(this, name);

        Click += (_, _) => Copy();
        // A tool page can be torn down while the confirmation timer is still armed; stop it on
        // unload so a Tick never fires against a detached visual tree.
        Unloaded += (_, _) => _resetTimer?.Stop();
    }

    public void Copy()
    {
        var text = GetText();
        if (string.IsNullOrEmpty(text))
            return;

        // The clipboard can be locked by another process (RDP, a clipboard manager, Excel); a
        // failed copy must not throw onto the UI thread, and must not claim success.
        if (!ClipboardService.TrySetText(text))
            return;

        _label.Text = Loc.Get("Tool.Copied");
        if (!_panel.Children.Contains(_label))
            _panel.Children.Add(_label);

        // Created and subscribed once. Re-subscribing per click — which is what `+=` inside this
        // method did — left one live Tick handler per copy, so the tenth copy ran ten resets.
        if (_resetTimer is null)
        {
            var dq = DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            _resetTimer = dq?.CreateTimer();
            if (_resetTimer is not null)
            {
                _resetTimer.IsRepeating = false;
                _resetTimer.Interval = TimeSpan.FromSeconds(1.5);
                _resetTimer.Tick += (_, _) => ResetLabel();
            }
        }
        _resetTimer?.Stop();
        _resetTimer?.Start();
    }

    private void ResetLabel()
    {
        _label.Text = _restingLabel;
        if (_restingLabel.Length == 0)
            _panel.Children.Remove(_label);
    }
}
