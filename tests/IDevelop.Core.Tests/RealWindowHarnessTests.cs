using System.Collections.Immutable;
using System.Text.RegularExpressions;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

/// <summary>The Windows real-window harness, which no Linux check runs, against the commands iDevelop gives Codex.</summary>
public sealed class RealWindowHarnessTests
{
    [Fact]
    public void The_harness_fake_Codex_has_a_rule_for_every_command_iDevelop_runs()
    {
        var harness = File.ReadAllText(Path.Combine(Repository(), "scripts", "real-window.psm1"));
        var fake = harness[harness.IndexOf("function New-FakeCodex", StringComparison.Ordinal)..];
        fake = fake[..fake.IndexOf("ConvertTo-Json", StringComparison.Ordinal)];
        string[][] rules = [.. Regex.Matches(fake, @"when = @\(([^)]*)\)").Select(match => match.Groups[1].Value.Split(',').Select(part => part.Trim().Trim('\'')).ToArray())];
        var codex = Clients.Get(ClientId.Codex);
        ImmutableArray<string>[] commands =
        [
            Assert.IsType<CatalogSource.Probed>(codex.Catalog).Probe.Arguments,
            .. codex.Readiness([]).Select(readiness => readiness.Probe.Arguments),
            codex.Launch(new LaunchRequest("gpt-6-sol", "high", "Draft it.")).Arguments,
            codex.Launch(new LaunchRequest("gpt-6-sol", "high", "banana") { ResumeSession = "thread-1" }).Arguments,
        ];

        Assert.Equal(["debug models", "login status", "app-server", "app-server"],
            commands.Select(arguments => rules.FirstOrDefault(when => arguments.Take(when.Length).SequenceEqual(when)) is { } rule ? string.Join(' ', rule) : "no rule"));
    }

    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "scripts", "real-window.psm1")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The tests run outside the repository.");
    }
}
