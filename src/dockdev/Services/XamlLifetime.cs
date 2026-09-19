using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace dockdev.Services;

/// <summary>
/// Safe access to inherited XAML properties once a window has started closing.
/// <para>
/// A sibling WinUI dock crashed in <c>Microsoft.UI.Xaml.dll</c> at
/// <c>DirectUI::DependencyObject::GetValueByKnownIndex_enum
/// ABI::Microsoft::UI::Xaml::FlowDirection_</c>, surfaced as a <c>StowedException</c> with no
/// managed frames. <see cref="FrameworkElement.ActualTheme"/>, flyout placement and
/// <c>ItemsRepeater</c> measure all walk inherited dependency properties — <c>FlowDirection</c>
/// among them — and that walk is not valid after the content island has gone. This type is the
/// one place those reads are guarded, the same way <see cref="HighContrast"/> is the one place
/// the accessibility-theme check lives.
/// </para>
/// </summary>
public static class XamlLifetime
{
    /// <summary>
    /// True when <paramref name="element"/> still has a <see cref="XamlRoot"/>. A null root is
    /// the usual sign the host window has started teardown; it is also the ordinary state of a
    /// control constructed before it is attached, so callers that run from a constructor must
    /// not treat this as "the element is dead" — only event handlers that fire during or after
    /// close should.
    /// </summary>
    public static bool HasRoot(FrameworkElement? element) => element?.XamlRoot is not null;

    /// <summary>
    /// Reads <see cref="FrameworkElement.ActualTheme"/> without letting a torn-down peer take
    /// the process with it. The getter walks inherited DPs including <c>FlowDirection</c>; after
    /// the island is gone that walk is the stowed exception named in the type remarks.
    /// </summary>
    public static bool TryGetActualTheme(FrameworkElement? element, out ElementTheme theme)
    {
        theme = ElementTheme.Default;
        if (element is null)
            return false;
        try
        {
            theme = element.ActualTheme;
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log("XamlLifetime.ActualTheme: " + ex.Message);
            return false;
        }
    }

    /// <summary>Walks up from <paramref name="start"/> looking for an ancestor of type
    /// <typeparamref name="T"/>. Returns null if the tree is already gone.</summary>
    public static T? FindAncestor<T>(DependencyObject? start) where T : class
    {
        try
        {
            for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is T match)
                    return match;
            }
        }
        catch (Exception ex)
        {
            Diag.Log("XamlLifetime.FindAncestor: " + ex.Message);
        }
        return null;
    }
}
