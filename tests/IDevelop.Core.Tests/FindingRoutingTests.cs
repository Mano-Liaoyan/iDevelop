using System.Collections.Immutable;
using IDevelop.Nodes;

namespace IDevelop.Core.Tests;

/// <summary>
/// Which implementers behind a Merge node get each finding of its review (ADR 0041): the one whose change touched the
/// finding's file, and every implementer when several or none touched it.
/// </summary>
public sealed class FindingRoutingTests
{
    private static Finding At(string id, string? location) => new(id, FindingState.Open, "text", "change", null, 1, "high", location);

    private static ImmutableHashSet<string> Files(params string[] files) => [.. files];

    private static string[][] Ids(ImmutableArray<ImmutableArray<Finding>> routed) => [.. routed.Select(findings => findings.Select(finding => finding.Id).ToArray())];

    [Fact]
    public void A_finding_goes_to_the_one_implementer_whose_change_touched_its_file()
    {
        var engine = Files("src/Calculator.Core/Engine.fs");
        var ui = Files("src/Calculator.App/Views.fs", "src/Calculator.App/App.fs");

        var routed = FindingRouting.Route([At("1", "src/Calculator.App/Views.fs:80"), At("2", "src/Calculator.Core/Engine.fs:12-14")], [engine, ui]);

        Assert.Equal([["2"], ["1"]], Ids(routed));
    }

    [Fact]
    public void A_finding_whose_file_both_or_neither_touched_or_that_names_no_file_goes_to_every_implementer()
    {
        var engine = Files("shared.fs", "engine.fs");
        var ui = Files("shared.fs", "views.fs");

        var routed = FindingRouting.Route([At("both", "shared.fs:3"), At("neither", "docs/new.md:1"), At("none", null), At("blank", " ")], [engine, ui]);

        Assert.Equal([["both", "neither", "none", "blank"], ["both", "neither", "none", "blank"]], Ids(routed));
    }

    [Fact]
    public void Every_finding_goes_to_every_implementer_while_one_implementer_s_files_cannot_be_read()
    {
        var routed = FindingRouting.Route([At("1", "engine.fs:1")], [Files("engine.fs"), null]);

        Assert.Equal([["1"], ["1"]], Ids(routed));
    }

    [Theory]
    [InlineData("src/Calc.cs:12", "src/Calc.cs")]
    [InlineData("src/Calc.cs:12:5", "src/Calc.cs")]
    [InlineData("src/Calc.cs#L12-L14", "src/Calc.cs")]
    [InlineData("./src\\Calc.cs", "src/Calc.cs")]
    [InlineData("/src/Calc.cs", "src/Calc.cs")]
    [InlineData("README", "README")]
    [InlineData(":12", null)]
    [InlineData(null, null)]
    public void A_location_names_its_file_without_the_line(string? location, string? file) =>
        Assert.Equal(file, FindingRouting.File(location));

    [Fact]
    public void A_location_relative_to_a_project_below_the_repository_s_top_matches_the_path_from_the_top()
    {
        var routed = FindingRouting.Route([At("1", "Views.fs:80")], [Files("app/src/Engine.fs"), Files("app/src/Views.fs")]);

        Assert.Equal([[], ["1"]], Ids(routed));
    }
}
