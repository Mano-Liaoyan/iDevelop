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

    private Point _anchor;
    private Rect _bounds;

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
        (_anchor, _bounds) = (anchor, bounds);
        IsOpen = true;
    }

    // Avalonia hands a custom placement its rectangles in the window's coordinates. The popover flips left or up when its
    // full size would cross the bounds there, then slides inside them. The flip takes the largest height, so the popover
    // stays put while the list shrinks under a search.
    private void Place(CustomPopupPlacement placement)
    {
        var room = Root.Margin;
        var size = placement.PopupSize.Deflate(room);
        var x = _anchor.X + size.Width > _bounds.Right ? _anchor.X - size.Width : _anchor.X;
        var y = _anchor.Y + Root.MaxHeight > _bounds.Bottom ? _anchor.Y - size.Height : _anchor.Y;
        placement.AnchorRectangle = new Rect(
            Math.Clamp(x, _bounds.Left, Math.Max(_bounds.Left, _bounds.Right - size.Width)) - room.Left,
            Math.Clamp(y, _bounds.Top, Math.Max(_bounds.Top, _bounds.Bottom - size.Height)) - room.Top,
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
        if (add.Highlighted is { } row && Rows.ContainerFromItem(row) is { } container)
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
