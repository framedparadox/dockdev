using DevDX.Services;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// One command surfaced in a tool window's command bar and bound to a keyboard accelerator.
/// Declared, not hand-placed (design doc §9.3), so the same action always sits behind the same
/// keystroke across every tool.
/// </summary>
public sealed class ToolCommand(
    string label,
    string glyph,
    Action execute,
    VirtualKey? key = null,
    VirtualKeyModifiers modifiers = VirtualKeyModifiers.None,
    Func<bool>? canExecute = null,
    string id = "")
{
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    public VirtualKey? Key { get; } = key;
    public VirtualKeyModifiers Modifiers { get; } = modifiers;

    /// <summary>
    /// A stable, unlocalized handle for this command, surfaced as the command bar button's
    /// <c>AutomationId</c>. <see cref="Label"/> cannot serve: it is a translated string, so a UI
    /// test keyed on it passes in English and fails in German, and breaks outright the first time
    /// anyone rewords a button. The ids here are the vocabulary <c>tests/DevDX.UITests</c> drives
    /// the app with, so they are part of the contract — rename one and the table row that uses it
    /// stops finding its button.
    /// </summary>
    public string Id { get; } = id;

    /// <summary>A control to seat in the command bar in place of a button — for an option that
    /// belongs beside a particular command rather than in the page's left-hand options strip.
    /// When set, <see cref="Label"/>, <see cref="Glyph"/> and the action are all ignored.</summary>
    public Microsoft.UI.Xaml.FrameworkElement? Content { get; init; }

    /// <summary>Show the glyph alone, with the label demoted to a tooltip. Reserved for the
    /// handful of actions whose icon is universally understood (see the factories below); a
    /// command whose meaning lives in its wording — Format, Minify, Convert — keeps its text.</summary>
    public bool IconOnly { get; init; }

    public bool CanExecute() => canExecute?.Invoke() ?? true;

    public void Invoke()
    {
        if (CanExecute())
            execute();
    }

    // ---- The shared actions -----------------------------------------------------------------
    // Declared once here rather than re-spelled in every page, so "copy the output" is the same
    // glyph, the same wording and the same keystroke in every tool — and changing any of
    // the three is one edit.

    private const VirtualKeyModifiers CtrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;

    /// <summary>The <see cref="Id"/> values commands carry. Constants rather than loose literals so
    /// the app and <c>tests/DevDX.UITests</c> cannot drift apart without the compiler noticing.</summary>
    public static class Ids
    {
        public const string Copy = "copy";
        public const string Save = "save";
        public const string Clear = "clear";
        public const string Validate = "validate";
        public const string Run = "run";
        public const string Swap = "swap";
        public const string Format = "format";
        public const string Minify = "minify";
        public const string Convert = "convert";
        public const string Generate = "generate";
    }

    /// <summary>Copy the tool's result to the clipboard. Ctrl+Shift+C.</summary>
    public static ToolCommand Copy(Action execute) =>
        new(Loc.Get("Tool.CopyOutput"), "", execute, VirtualKey.C, CtrlShift, id: Ids.Copy) { IconOnly = true };

    /// <summary>Save the tool's output straight to the desktop. No accelerator: Ctrl+Shift+C/S/V/X
    /// are already spoken for by Copy, Swap, Validate and Clear.</summary>
    public static ToolCommand Save(Action execute) =>
        new(Loc.Get("Tool.Save"), "\uE74E", execute, id: Ids.Save) { IconOnly = true };

    /// <summary>Empty the tool's panes. Ctrl+Shift+X.</summary>
    public static ToolCommand Clear(Action execute) =>
        new(Loc.Get("Tool.Clear"), "", execute, VirtualKey.X, CtrlShift, id: Ids.Clear) { IconOnly = true };

    /// <summary>Check the input without transforming it — a shield, since the question it
    /// answers is "is this safe to use?". Ctrl+Shift+V.</summary>
    public static ToolCommand Validate(Action execute) =>
        new(Loc.Get("Tool.Validate"), "", execute, VirtualKey.V, CtrlShift, id: Ids.Validate) { IconOnly = true };

    /// <summary>Apply the tool's main transformation. Ctrl+Enter.</summary>
    public static ToolCommand Run(Action execute) =>
        new(Loc.Get("Tool.Run"), "", execute, VirtualKey.Enter, VirtualKeyModifiers.Control, id: Ids.Run) { IconOnly = true };

    /// <summary>Seats a control in the command bar at this position. See <see cref="Content"/>.</summary>
    public static ToolCommand Element(Microsoft.UI.Xaml.FrameworkElement content) =>
        new("", "", static () => { }) { Content = content };

    /// <summary>Feed the output back in as the input. Ctrl+Shift+S.</summary>
    public static ToolCommand Swap(Action execute) =>
        new(Loc.Get("Tool.SwapInOut"), "", execute, VirtualKey.S, CtrlShift, id: Ids.Swap) { IconOnly = true };
}
