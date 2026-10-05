using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.TestSupport;
using static IDevelop.Desktop.Tests.AppTempFolder;

namespace IDevelop.Desktop.Tests;

/// <summary>Fluent's own controls take the app's look: submenus, tooltips, ticked boxes, and the accent.</summary>
public sealed class FluentDefaultsTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    private static Color Fill(IBrush? brush) => ((ISolidColorBrush)brush!).Color;

    [AvaloniaTheory]
    [InlineData("ThemeLight", "#F8FAFD")]
    [InlineData("ThemeDark", "#26292D")]
    public void The_Replace_With_submenu_draws_as_its_menu_and_its_chevron_turns_white_on_the_highlight(string theme, string surface)
    {
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90)));
        shell.Click(shell.Find<RadioButton>(theme));
        shell.RightClick(shell.Header(shell.Node("Design")));
        var replace = shell.MenuItem("NodeMenuReplace");

        shell.Click(replace);
        shell.Window.MouseMove(shell.Center(replace));
        shell.Render();

        var submenu = shell.Window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.TemplatedParent == replace && border.Name != "PART_LayoutRoot");
        Assert.Equal((Color.Parse(surface), new CornerRadius(10), new Thickness(1)), (Fill(submenu.Background), submenu.CornerRadius, submenu.BorderThickness));
        var chevron = replace.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Name == "PART_ChevronPath");
        Assert.Equal(Colors.White, Fill(chevron.Fill));
    }

    [AvaloniaTheory]
    [InlineData("ThemeLight", "#F8FAFD")]
    [InlineData("ThemeDark", "#26292D")]
    public void A_tooltip_is_a_rounded_popover_on_the_menu_surface(string theme, string surface)
    {
        var shell = Shell.Open(_temp.Create("project"));
        shell.Click(shell.Find<RadioButton>(theme));

        ToolTip.SetIsOpen(shell.Find<Button>("AddTask"), true);
        shell.Render();

        var tip = shell.Window.GetVisualDescendants().OfType<ToolTip>().Single();
        var root = tip.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");
        Assert.Equal((Color.Parse(surface), new CornerRadius(8)), (Fill(root.Background), root.CornerRadius));
    }

    [AvaloniaTheory]
    [InlineData("ThemeLight")]
    [InlineData("ThemeDark")]
    public void A_ticked_box_fills_with_the_accent_and_checks_in_white(string theme)
    {
        var shell = Shell.Open(_temp.Create("project"));
        shell.Click(shell.Find<RadioButton>(theme));
        var box = new CheckBox { IsChecked = true };
        new Window { Content = box }.Show();
        shell.Render();

        var parts = box.GetVisualDescendants();
        Assert.Equal(
            (Color.Parse("#1E6EF4"), Colors.White),
            (Fill(parts.OfType<Border>().Single(border => border.Name == "NormalRectangle").Background),
             Fill(parts.OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Name == "CheckGlyph").Fill)));
    }

    [AvaloniaTheory]
    [InlineData("ThemeLight", "#0088FF")]
    [InlineData("ThemeDark", "#0091FF")]
    public void Fluent_takes_the_apps_blue_as_its_accent_not_the_systems(string theme, string accent)
    {
        var shell = Shell.Open(_temp.Create("project"));
        shell.Click(shell.Find<RadioButton>(theme));

        Assert.Equal(Color.Parse(accent), Fill((IBrush?)shell.Window.FindResource(shell.Window.ActualThemeVariant, "SystemControlHighlightAccentBrush")));
    }
}
