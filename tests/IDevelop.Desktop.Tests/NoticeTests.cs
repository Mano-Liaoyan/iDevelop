namespace IDevelop.Desktop.Tests;

public sealed class NoticeTests
{
    [Fact]
    public void The_fluent_icons_license_notice_ships_beside_the_app()
    {
        var notice = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!, "THIRD-PARTY-NOTICES.md");

        var text = File.ReadAllText(notice);

        Assert.Contains("Fluent UI System Icons", text);
        Assert.Contains("`a563cf9166f4f91aa617557ed272612b7f0a2f72`", text);
        Assert.Contains("Copyright (c) 2020 Microsoft Corporation", text);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND", text);
    }
}
