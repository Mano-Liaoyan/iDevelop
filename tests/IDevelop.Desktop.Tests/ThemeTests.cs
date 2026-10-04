using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using IDevelop.TestSupport;

namespace IDevelop.Desktop.Tests;

public sealed class ThemeTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static string PreferencesFile => ((App)Application.Current!).PreferencesFile!;

    // The grid's dots sit on the corners of 24 px tiles, so this point is the middle of a tile.
    private static Color CanvasColor(Shell shell) => shell.ColorAt(shell.Editor, new Point(252, 252));

    [AvaloniaFact]
    public void Choosing_dark_then_light_recolors_the_canvas_and_remembers_each_choice()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        Assert.False(File.Exists(PreferencesFile));

        shell.Click(shell.Find<RadioButton>("ThemeDark"));
        Assert.Equal(Color.Parse("#0D0E0A"), CanvasColor(shell));
        Assert.Equal("""{"theme":"dark"}""", File.ReadAllText(PreferencesFile));

        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        Assert.Equal(Color.Parse("#FBFBF9"), CanvasColor(shell));
        Assert.Equal("""{"theme":"light"}""", File.ReadAllText(PreferencesFile));

        shell.Click(shell.Find<RadioButton>("ThemeSystem"));
        Assert.Equal("""{"theme":"system"}""", File.ReadAllText(PreferencesFile));
    }

    [AvaloniaFact]
    public void Selecting_a_segment_through_ui_automation_applies_and_remembers_its_theme()
    {
        var shell = Shell.Open(_temp.Create("plan"));

        ControlAutomationPeer.CreatePeerForElement(shell.Find<RadioButton>("ThemeDark")).GetProvider<ISelectionItemProvider>()!.Select();
        shell.Render();

        Assert.Equal(Color.Parse("#0D0E0A"), CanvasColor(shell));
        Assert.Equal("""{"theme":"dark"}""", File.ReadAllText(PreferencesFile));
    }

    [AvaloniaTheory]
    [InlineData("""{ "theme": "dark" }""", "ThemeDark")]
    [InlineData("{", "ThemeSystem")]
    public void Launching_with_a_preferences_file_marks_its_theme_and_leaves_the_file_as_it_was(string text, string segment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)!);
        File.WriteAllText(PreferencesFile, text);

        // Initialize is the app's startup, so running it again with the file in place launches the app with that file.
        Application.Current!.Initialize();
        var shell = Shell.Open(_temp.Create("plan"));

        Assert.True(shell.Find<RadioButton>(segment).IsChecked);
        Assert.Equal(text, File.ReadAllText(PreferencesFile));
    }

    [Theory]
    [InlineData("""{"theme":"dark"}""", "Dark")]
    [InlineData("""{"theme":"light"}""", "Light")]
    [InlineData("""{"theme":"system"}""", "Default")]
    [InlineData("""{"theme":"sepia"}""", "Default")]
    [InlineData("[1]", "Default")]
    [InlineData("{", "Default")]
    [InlineData(null, "Default")]
    public void A_preferences_file_reads_as_the_theme_it_names_or_as_system(string? text, string theme)
    {
        var file = Path.Combine(_temp.Create("preferences"), "settings.json");
        if (text is not null)
        {
            File.WriteAllText(file, text);
        }

        Assert.Equal(theme, App.ReadTheme(file).ToString());
    }

    [AvaloniaFact]
    public void The_switch_marks_the_theme_set_in_code()
    {
        var shell = Shell.Open(_temp.Create("plan"));
        shell.Click(shell.Find<RadioButton>("ThemeDark"));

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        shell.Render();

        bool?[] marks = [.. new[] { "ThemeSystem", "ThemeLight", "ThemeDark" }.Select(id => shell.Find<RadioButton>(id).IsChecked)];
        Assert.Equal([true, false, false], marks);
        Assert.Equal("""{"theme":"dark"}""", File.ReadAllText(PreferencesFile));
    }
}
