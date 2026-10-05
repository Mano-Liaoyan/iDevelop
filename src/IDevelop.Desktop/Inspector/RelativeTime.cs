using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using IDevelop.Desktop.Execution;

namespace IDevelop.Desktop.Inspector;

/// <summary>A time said relative to now, such as "5 min ago", which keeps itself current while it shows.</summary>
public sealed class RelativeTime : TextBlock
{
    public static readonly StyledProperty<DateTimeOffset?> AtProperty = AvaloniaProperty.Register<RelativeTime, DateTimeOffset?>(nameof(At));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };

    public RelativeTime() => _timer.Tick += (_, _) => Show();

    public DateTimeOffset? At
    {
        get => GetValue(AtProperty);
        set => SetValue(AtProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AtProperty)
        {
            Show();
        }
    }

    private void Show() => Text = At is { } at ? RunText.Ago(at, DateTimeOffset.Now) : null;
}
