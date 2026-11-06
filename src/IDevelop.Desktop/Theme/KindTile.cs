using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using IDevelop.Desktop.Canvas;

namespace IDevelop.Desktop.Theme;

/// <summary>
/// A node kind's rounded square in the kind's hue with its white glyph. Kinds.axaml draws it in a 32 unit frame and
/// scales the frame to TileSize. A library blueprint's tile carries a badge, unless the tile is too small to show one.
/// </summary>
public sealed class KindTile : TemplatedControl
{
    public static readonly StyledProperty<NodeKind> KindProperty = AvaloniaProperty.Register<KindTile, NodeKind>(nameof(Kind));

    public static readonly StyledProperty<double> TileSizeProperty = AvaloniaProperty.Register<KindTile, double>(nameof(TileSize), 32);

    public static readonly StyledProperty<bool> IsLibraryProperty = AvaloniaProperty.Register<KindTile, bool>(nameof(IsLibrary));

    public static readonly StyledProperty<bool> KeepsBadgeProperty = AvaloniaProperty.Register<KindTile, bool>(nameof(KeepsBadge));

    private const double SmallestBadgedSize = 24;

    // The badge reaches past the tile's top right corner.
    static KindTile() => ClipToBoundsProperty.OverrideDefaultValue<KindTile>(false);

    public KindTile() => AutomationProperties.SetName(this, NodeKinds.Info(Kind).Label);

    public NodeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The tile's width and height: 16 on a card's band, 32 on the inspector header, 24 in the Add popover, 18 in lists.</summary>
    public double TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    /// <summary>Whether the node's blueprint comes from a project or personal library rather than being built in.</summary>
    public bool IsLibrary
    {
        get => GetValue(IsLibraryProperty);
        set => SetValue(IsLibraryProperty, value);
    }

    /// <summary>The tile keeps its badge below the size that lists use, as on a card's band.</summary>
    public bool KeepsBadge
    {
        get => GetValue(KeepsBadgeProperty);
        set => SetValue(KeepsBadgeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            AutomationProperties.SetName(this, NodeKinds.Info(Kind).Label);
        }
        else if (change.Property == TileSizeProperty || change.Property == IsLibraryProperty || change.Property == KeepsBadgeProperty)
        {
            PseudoClasses.Set(":library", IsLibrary && (KeepsBadge || TileSize >= SmallestBadgedSize));
        }
    }
}
