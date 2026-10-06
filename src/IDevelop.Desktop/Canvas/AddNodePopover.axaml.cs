using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace IDevelop.Desktop.Canvas;

public partial class AddNodePopover : Popup
{
    /// <summary>The glyph geometry for an icon resource key, such as IconFit.</summary>
    public static readonly IValueConverter Glyph = new FuncValueConverter<string?, Geometry?>(key =>
        key is not null && Application.Current!.TryGetResource(key, null, out var value) ? value as Geometry : null);

    // The popover keeps this far from the top and bottom of its bounds, so its largest height leaves a gap at each end.
    private const double Gap = 8;

    private Point _anchor;
    private Rect _room;
    private double _tallest;

    public AddNodePopover()
    {
        InitializeComponent();
        CustomPopupPlacementCallback = Place;
        Root.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Search.Focus();
    }

    private AddNodeViewModel? ViewModel => DataContext as AddNodeViewModel;

    /// <summary>Opens the popover at <paramref name="anchor"/>, inside <paramref name="bounds"/>, both in the window's coordinates.</summary>
    internal void Show(AddNodeViewModel add, Control target, Point anchor, Rect bounds)
    {
        IsOpen = false;
        DataContext = add;
        PlacementTarget = target;
        _anchor = anchor;
        _room = bounds.Deflate(new Thickness(0, Gap));
        _tallest = 0;
        Root.MaxHeight = Math.Max(0, _room.Height);
        IsOpen = true;
    }

    // Avalonia hands a custom placement its rectangles in the window's coordinates. The popover opens below and to the
    // right of its anchor, flips left or up where its size would cross the room, then slides inside it. It places by its
    // tallest size since it opened, so it stays put while a search shortens the list.
    private void Place(CustomPopupPlacement placement)
    {
        var shadow = Root.Margin;
        var size = placement.PopupSize.Deflate(shadow);
        _tallest = Math.Max(_tallest, size.Height);
        var x = _anchor.X + size.Width > _room.Right ? _anchor.X - size.Width : _anchor.X;
        var y = _anchor.Y + _tallest > _room.Bottom ? _anchor.Y - _tallest : _anchor.Y;
        placement.AnchorRectangle = new Rect(
            Math.Clamp(x, _room.Left, Math.Max(_room.Left, _room.Right - size.Width)) - shadow.Left,
            Math.Clamp(y, _room.Top, Math.Max(_room.Top, _room.Bottom - _tallest)) - shadow.Top,
            1,
            1);
        placement.Anchor = PopupAnchor.TopLeft;
        placement.Gravity = PopupGravity.BottomRight;
        placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.None;
        placement.Offset = default;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } add)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                add.Move(1);
                break;
            case Key.Up:
                add.Move(-1);
                break;
            case Key.Enter:
                add.Choose();
                break;
            case Key.Escape:
                add.Close();
                break;
            default:
                return;
        }

        e.Handled = true;
        if (add.Highlighted is { } row && (Rows.ContainerFromItem(row) ?? ActionRows.ContainerFromItem(row)) is { } container)
        {
            container.BringIntoView();
        }
    }

    private void OnRowEntered(object? sender, PointerEventArgs e)
    {
        if (ViewModel is { } add && sender is Control { DataContext: AddNodeRow row })
        {
            add.Highlighted = row;
        }
    }
}
