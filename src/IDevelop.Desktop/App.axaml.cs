using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using IDevelop.Desktop.Theme;
using IDevelop.Execution;

namespace IDevelop.Desktop;

public partial class App : Application
{
    /// <summary>The per-user file that remembers the theme between runs. Null remembers nothing.</summary>
    public string? PreferencesFile { get; init; }

    /// <summary>The personal blueprint library, beside the preferences file. Null without one.</summary>
    public string? PersonalBlueprints => PreferencesFile is { } file ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "blueprints") : null;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (PreferencesFile is { } file)
        {
            RequestedThemeVariant = ThemePreference.Read(file);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clients = new ClientDirectory(CommandResolver.FromEnvironment());
            _ = clients.RefreshAsync();
            var window = new MainWindow(clients);
            if (desktop.Args is [var folder, ..])
            {
                _ = window.ViewModel.Open(folder);
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the theme the user chose and remembers it. Choosing the current theme does nothing.</summary>
    internal void Choose(ThemeVariant variant)
    {
        if (variant == RequestedThemeVariant)
        {
            return;
        }

        RequestedThemeVariant = variant;
        if (PreferencesFile is { } file)
        {
            try
            {
                ThemePreference.Write(file, variant);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(this, "Couldn't remember the theme in {File}: {Message}", file, e.Message);
            }
        }
    }
}
