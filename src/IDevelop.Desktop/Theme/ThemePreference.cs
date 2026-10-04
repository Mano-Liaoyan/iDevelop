using System.Text;
using System.Text.Json;
using Avalonia.Styling;
using IDevelop.Projects;

namespace IDevelop.Desktop.Theme;

/// <summary>The theme the user chose, as the per-user preferences file stores it: {"theme":"system"|"light"|"dark"}.</summary>
internal static class ThemePreference
{
    // The names stored on disk. ThemeVariant.Default means "follow the operating system".
    private static readonly (string Name, ThemeVariant Variant)[] Themes =
        [("system", ThemeVariant.Default), ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark)];

    /// <summary>Parses the preferences file. A missing or unreadable file, malformed JSON,
    /// a root that is not an object, or an unknown name reads as ThemeVariant.Default.</summary>
    public static ThemeVariant Read(string file)
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

    public static void Write(string file, ThemeVariant variant)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        AtomicFile.Replace(file, Encoding.UTF8.GetBytes($$"""{"theme":"{{Themes.Single(theme => theme.Variant == variant).Name}}"}"""));
    }
}
