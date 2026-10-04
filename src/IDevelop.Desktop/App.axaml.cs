using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using IDevelop.Execution;

namespace IDevelop.Desktop;

public partial class App : Application
{
    // The names stored on disk. ThemeVariant.Default means "follow the operating system".
    private static readonly (string Name, ThemeVariant Variant)[] Themes =
        [("system", ThemeVariant.Default), ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark)];

    /// <summary>The per-user file that remembers the theme between runs. Null remembers nothing.</summary>
    public string? PreferencesFile { get; init; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (PreferencesFile is { } file)
        {
            RequestedThemeVariant = ReadTheme(file);
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
                WriteTheme(file, variant);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.TryGet(LogEventLevel.Warning, LogArea.Control)?.Log(this, "Couldn't remember the theme in {File}: {Message}", file, e.Message);
            }
        }
    }

    /// <summary>Parses the preferences file. A missing or unreadable file, malformed JSON,
    /// a root that is not an object, or an unknown name reads as ThemeVariant.Default.</summary>
    internal static ThemeVariant ReadTheme(string file)
    {
        string? name;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            name = json.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String
                ? theme.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return ThemeVariant.Default;
        }

        return Themes.SingleOrDefault(theme => theme.Name == name).Variant ?? ThemeVariant.Default;
    }

    /// <summary>Writes {"theme":"name"} to a temporary file beside the target, then moves it over the target,
    /// so a crash leaves either the old choice or the new one.</summary>
    internal static void WriteTheme(string file, ThemeVariant variant)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, $$"""{"theme":"{{Themes.Single(theme => theme.Variant == variant).Name}}"}""");
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
