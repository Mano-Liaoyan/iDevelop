using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
        UseAppAccent();
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

    // Fluent's controls take their accent from the operating system unless its palette names one.
    private void UseAppAccent()
    {
        foreach (var fluent in Styles.OfType<FluentTheme>())
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (TryGetResource("FluentAccentColor", theme, out var accent) && accent is Color color)
                {
                    fluent.Palettes[theme] = new ColorPaletteResources { Accent = color };
                }
            }
        }
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
