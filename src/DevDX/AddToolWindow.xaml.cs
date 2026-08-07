using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DevDX;

/// <summary>
/// The Add-Tool window (design doc §18): a tile gallery grouped by the six categories, with a
/// search box. Each tile is a <see cref="ToggleButton"/> reflecting whether that tool is
/// currently shown on the target dock, so "Add" means "customise what's visible and in what
/// order" — toggling a tile adds or removes it from the dock immediately, live, with no separate
/// submit step. Plus the one structural tile, Separator.
/// <para>
/// There is no "add an arbitrary target" form here: DevDX's catalog is closed and code-defined, so
/// there is nothing to browse to and nothing to name; the only decision is which of the nineteen
/// built-in tools are pinned right now.
/// </para>
/// </summary>
public sealed partial class AddToolWindow : Window
{
    private readonly DevDxManager _manager;
    private readonly DockWindow _dock;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    /// <summary>
    /// An <see cref="AutoSuggestBox"/> rather than a bare TextBox: it is the WinUI 3 search
    /// control, so it brings the magnifier query button, the clear "✕" once there is text, and the
    /// Search automation role for free — none of which a TextBox has. Its suggestion list stays
    /// unused (<c>IsSuggestionListOpen</c> is never set) because the results are the tile gallery
    /// below, which is always visible; a popup would just cover it.
    /// </summary>
    private readonly AutoSuggestBox _search = new()
    {
        QueryIcon = new SymbolIcon(Symbol.Find),
        Width = 260,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly StackPanel _categories = new() { Spacing = 20 };

    public AddToolWindow(DevDxManager manager, DockWindow dock)
    {
        _manager = manager;
        _dock = dock;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("Add.Title");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        // After ExtendsContentIntoTitleBar, never before — see WindowChrome.UseTallTitleBar.
        WindowChrome.UseTallTitleBar(_appWindow);

        var titleBar = new AppTitleBar("", Loc.Get("Add.Title"), accentGlyph: true);

        // The header row: the hero on the left, the search box on the right of it, the two centred
        // against each other. Search belongs on the line whose results it filters, not stacked
        // under it — that layout left a full-width box doing nothing with the space and pushed the
        // first row of tiles further down the window.
        var hero = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var heroIcon = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] };
        heroIcon.Child = new FontIcon { Glyph = "\uE710", FontSize = 22, Foreground = new SolidColorBrush(Colors.White) };
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(new TextBlock { Text = Loc.Get("Add.Title"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        heroText.Children.Add(new TextBlock { Text = Loc.Get("Add.Subtitle"), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        hero.Children.Add(heroIcon);
        hero.Children.Add(heroText);

        _search.PlaceholderText = Loc.Get("Add.SearchPlaceholder");
        AutomationProperties.SetName(_search, Loc.Get("Add.SearchPlaceholder"));
        // UserInput only: filtering on every reason would also re-render the gallery when the
        // control echoes its own text back.
        _search.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                RenderTiles();
        };
        _search.QuerySubmitted += (_, _) => RenderTiles();

        var header = new Grid { ColumnSpacing = 16, Padding = new Thickness(24, 8, 24, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(hero, 0);
        Grid.SetColumn(_search, 1);
        header.Children.Add(hero);
        header.Children.Add(_search);

        // The gallery carries the same 24px side inset as the header, so the tiles line up under
        // the hero rather than running into the window edge, and there is 24px of air below the
        // last row instead of the tiles ending flush against the bottom.
        var scroller = new ScrollViewer
        {
            Content = _categories,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(24, 0, 24, 24),
        };

        var bodyGrid = new Grid();
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        bodyGrid.Children.Add(header);
        bodyGrid.Children.Add(scroller);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(bodyGrid, 1);
        root.Children.Add(titleBar);
        root.Children.Add(bodyGrid);
        RootGrid.Children.Add(root);
        SetTitleBar(titleBar);

        ApplyTheme(manager.Config.Theme);
        RootGrid.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        Activated += (_, _) => ApplyChromeTheme();

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = true;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = dock.Profile.Snapped || dock.Profile.AlwaysOnTop;
        }
        _appWindow.IsShownInSwitchers = true;

        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 780, 640);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        _manager.ItemsChanged += OnItemsChanged;
        Closed += (_, _) => _manager.ItemsChanged -= OnItemsChanged;

        RenderTiles();
    }

    private void OnItemsChanged() => RenderTiles();

    internal void ApplyTheme(DockTheme theme)
    {
        RootGrid.RequestedTheme = DockWindow.ResolveTheme(theme);
        ApplyChromeTheme();
    }

    private void ApplyChromeTheme()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        WindowChrome.SetTitleBarTheme(_appWindow, dark);
        WindowChrome.HideWindowBorder(_hwnd, dark);
    }

    // ---- Tile gallery -------------------------------------------------------------------

    /// <summary>Segoe Fluent Icons "GripperBarVertical" — the structural Separator tile.</summary>
    private const string SeparatorGlyph = "\uE76F";

    private void RenderTiles()
    {
        _categories.Children.Clear();
        var query = (_search.Text ?? string.Empty).Trim();

        // Which section the lone Separator tile rides along with: the last one that actually
        // rendered, not the last in the catalog. Deciding by catalog position meant a query that
        // filtered the final category away attached the tile to a section that was never added to
        // the tree, and it simply vanished.
        WrapGridPanel? lastWrap = null;

        foreach (var group in ToolCatalog.ByCategory())
        {
            var tools = query.Length == 0
                ? group.ToList()
                : group.Where(t => Matches(t, query)).ToList();
            if (tools.Count == 0)
                continue;

            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = Loc.Get("Category." + group.Key),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });

            var wrap = new WrapGridPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            foreach (var tool in tools)
                wrap.Children.Add(BuildToolTile(tool));

            section.Children.Add(wrap);
            _categories.Children.Add(section);
            lastWrap = wrap;
        }

        // The one structural tile only makes sense once, so it rides along with whichever category
        // rendered last rather than getting a category of its own. Hidden while a query is active:
        // it is not a tool and matches nothing the user could have typed.
        if (query.Length == 0 && lastWrap is not null)
            lastWrap.Children.Add(BuildActionTile(Loc.Get("Add.TypeSeparator"), SeparatorGlyph, _dock.AddSeparator));

        // A query that matches nothing has to say so, rather than leaving a blank sheet under the
        // search box with no clue whether the window broke or the word is simply not a tool.
        if (_categories.Children.Count == 0)
        {
            _categories.Children.Add(new TextBlock
            {
                Text = Loc.Get("Search.NoResults"),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private static bool Matches(ToolDefinition tool, string query) =>
        tool.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        tool.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        tool.SearchAliases.Any(a => a.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// One tool, as a <see cref="ToggleButton"/> whose checked state <em>is</em> "this tool is on
    /// the dock". Asking the dock rather than scanning its items directly matters: a tool switched
    /// off in Settings ▸ Tools can leave a hidden item behind, and counting that as still-added
    /// showed the tile checked while the icon was nowhere on the strip.
    /// </summary>
    private FrameworkElement BuildToolTile(ToolDefinition tool)
    {
        var toggle = new ToggleButton
        {
            Width = TileWidth,
            Height = TileHeight,
            Padding = new Thickness(6, 8, 6, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = _dock.IsToolActive(tool.Kind),
        };
        AutomationProperties.SetName(toggle, tool.DisplayName);
        ToolTipService.SetToolTip(toggle, tool.Description);

        toggle.Content = TileContent(tool.Glyph, tool.DisplayName);

        // Routed through the dock's own SetToolActive so this tile and the Settings ▸ Tools switch
        // are the same operation — un-hiding what is already pinned instead of pinning a second
        // copy of it, which is what the old inline add/remove here did.
        toggle.Click += (_, _) => _dock.SetToolActive(tool, toggle.IsChecked == true);
        return toggle;
    }

    private static FrameworkElement BuildActionTile(string label, string glyph, Action onClick)
    {
        var button = new Button
        {
            Width = TileWidth,
            Height = TileHeight,
            Padding = new Thickness(6, 8, 6, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = TileContent(glyph, label),
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, Loc.Get("Add.Separator.Hint"));
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Every tile is the same size and the same shape — glyph above a two-line caption. Fixed
    /// dimensions rather than content-sized ones because a gallery of tiles that each size
    /// themselves to their own name is a ragged grid: "UUID" next to "Data Formatter" left rows
    /// that did not line up either across a row or between one category and the next.
    /// </summary>
    private const double TileWidth = 112;

    private const double TileHeight = 96;

    private static StackPanel TileContent(string glyph, string label)
    {
        bool isTextGlyph = GlyphFonts.IsTextGlyph(glyph);
        var content = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = isTextGlyph
                ? new FontFamily("Segoe UI")
                : (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = isTextGlyph ? 22 * 0.8 : 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            // Two lines, then an ellipsis: without the cap a long name grows the tile past its
            // neighbours and knocks the whole row out of alignment.
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 14,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        return content;
    }
}

/// <summary>
/// A minimal left-to-right wrapping panel — WinUI ships no built-in WrapPanel, and the tile
/// gallery wants rows that fill the width and wrap, not the uniform grid an
/// <c>ItemsRepeater</c> + <c>UniformGridLayout</c> would impose.
/// <para>
/// Measure and arrange share one pass so the two cannot disagree: the previous version measured
/// against the available width and arranged against the final one, so whenever a ScrollViewer
/// handed back a different width (which it does the moment a vertical scrollbar appears) the row
/// breaks computed during measure were not the ones used to arrange, and the last tile of a row
/// was laid out past the right edge.
/// </para>
/// </summary>
public sealed class WrapGridPanel : Microsoft.UI.Xaml.Controls.Panel
{
    private double _horizontalSpacing = 8;
    private double _verticalSpacing = 8;

    /// <summary>Gap between tiles on the same row.</summary>
    public double HorizontalSpacing
    {
        get => _horizontalSpacing;
        set
        {
            if (_horizontalSpacing.Equals(value))
                return;
            _horizontalSpacing = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Gap between rows.</summary>
    public double VerticalSpacing
    {
        get => _verticalSpacing;
        set
        {
            if (_verticalSpacing.Equals(value))
                return;
            _verticalSpacing = value;
            InvalidateMeasure();
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        // Height is unconstrained during measure: the panel is inside a vertical ScrollViewer, so
        // it is entitled to be as tall as its rows need, and passing a finite height down would
        // let a tile shrink itself to fit a viewport it is going to be scrolled through anyway.
        var childConstraint = new Windows.Foundation.Size(availableSize.Width, double.PositiveInfinity);
        foreach (var child in Children)
            child.Measure(childConstraint);

        var (width, height) = Layout(availableSize.Width, arrange: false);
        // An unconstrained width (a horizontally scrolling parent) has no wrap point to report
        // against, so the panel asks for the width its single row actually came to.
        return new Windows.Foundation.Size(
            double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>
    /// Walks the children into rows for the given width, optionally placing them. The single
    /// source of truth for where a tile ends up, used by both passes.
    /// </summary>
    private (double Width, double Height) Layout(double availableWidth, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            // Wrap before placing, never after: a row break decided on the way out would leave the
            // offending tile already arranged past the edge. `x > 0` keeps a tile wider than the
            // panel on its own row rather than looping forever trying to find one that fits.
            if (x > 0 && x + size.Width > availableWidth)
            {
                widest = Math.Max(widest, x - HorizontalSpacing);
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            if (arrange)
                child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));

            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        widest = Math.Max(widest, x > 0 ? x - HorizontalSpacing : 0);
        return (widest, y + rowHeight);
    }
}
