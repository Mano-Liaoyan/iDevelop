using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace IDevelop.Desktop.Inspector;

/// <summary>
/// A section or a row of the inspector. It shows only when its data says so through <see cref="IsShown"/>, and when the
/// window's filter lets it, so it sets its own <see cref="Visual.IsVisible"/>, and XAML binds IsShown instead.
/// </summary>
public abstract class InspectorPart : ContentControl
{
    public static readonly StyledProperty<bool> IsShownProperty = AvaloniaProperty.Register<InspectorPart, bool>(nameof(IsShown), true);

    private InspectorState? _state;

    public bool IsShown
    {
        get => GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    /// <summary>The window's inspector state while the part is in the inspector, or null.</summary>
    protected InspectorState? State => _state;

    protected bool IsFiltering => _state is { IsFiltering: true };

    internal abstract void Refresh();

    // A hidden control is never measured, so its template and content would wait until it shows. The editors in a part
    // keep their automation ids findable while hidden, as they were when a plain panel held them.
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        ApplyTemplate();
        Presenter?.ApplyTemplate();
        Watch(InspectorState.GetCurrent(this));
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        Watch(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InspectorState.CurrentProperty && ((ILogical)this).IsAttachedToLogicalTree)
        {
            Watch(InspectorState.GetCurrent(this));
        }
        else if (change.Property == IsShownProperty)
        {
            Refresh();
        }
    }

    // A part listens only while it is in the tree, so the parts of a node the inspector no longer shows can go.
    private void Watch(InspectorState? state)
    {
        if (_state is not null)
        {
            _state.Changed -= OnStateChanged;
        }

        _state = state;
        if (state is not null)
        {
            state.Changed += OnStateChanged;
        }

        Refresh();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();
}
