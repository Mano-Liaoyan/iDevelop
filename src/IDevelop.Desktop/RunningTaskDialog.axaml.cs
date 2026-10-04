using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IDevelop.Desktop;

public enum RunningTaskChoice { StopAndLeave, KeepRunning }

/// <summary>Asked before the window leaves a project whose task it runs. Keep running is the default, since it loses nothing.</summary>
public partial class RunningTaskDialog : Window
{
    public RunningTaskDialog() => InitializeComponent();

    public RunningTaskDialog(string question) : this()
    {
        Title = question;
        Question.Text = question;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        KeepButton.Focus();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => Close(RunningTaskChoice.StopAndLeave);

    private void OnKeep(object? sender, RoutedEventArgs e) => Close(RunningTaskChoice.KeepRunning);
}
