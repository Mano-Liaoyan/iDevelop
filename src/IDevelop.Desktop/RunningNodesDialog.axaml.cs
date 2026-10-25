using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IDevelop.Desktop;

public enum RunningNodesChoice { StopAndLeave, KeepRunning }

/// <summary>
/// Asked before the window leaves a project whose nodes run. A stopped node reopens as interrupted. Keep running is the
/// default, since it loses nothing.
/// </summary>
public partial class RunningNodesDialog : Window
{
    public RunningNodesDialog() => InitializeComponent();

    public RunningNodesDialog(string question) : this()
    {
        Title = question;
        Question.Text = question;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        KeepButton.Focus();
    }

    private void OnStop(object? sender, RoutedEventArgs e) => Close(RunningNodesChoice.StopAndLeave);

    private void OnKeep(object? sender, RoutedEventArgs e) => Close(RunningNodesChoice.KeepRunning);
}
