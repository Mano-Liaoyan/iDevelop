using Avalonia;
using IDevelop.Projects;
using IDevelop.TestSupport;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Tests;

internal static class AppTempFolder
{
    // Each headless test's App remembers its theme in a folder of its own, so the test's files join it there and
    // leave with it. A test on another thread sees whichever App the headless thread runs, so it takes a new folder.
    public static TempFolder New() =>
        new(Application.Current is App { PreferencesFile: { } file } app && app.CheckAccess() ? Path.GetDirectoryName(file) : null);

    public static WorkflowEdit.PlaceNode TaskAt(
        TaskId id, string title, double x, double y, ExecutionSettings? execution = null, string instructions = "",
        ConversationMode conversation = ConversationMode.Autonomous) =>
        TestNodes.Place(TestNodes.Implement(id, title, instructions, execution: execution, conversation: conversation), new CanvasPoint(x, y));

    /// <summary>
    /// The environment the tests' Git runs with: a scratch global configuration, so the person's own never applies, and a
    /// fixed identity.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Git { get; } = GitEnvironment();

    private static Dictionary<string, string> GitEnvironment()
    {
        var config = Path.Combine(Path.GetTempPath(), $"idevelop-tests-git-{Environment.ProcessId}.config");
        File.WriteAllText(config, "[maintenance]\n\tauto = false\n");
        return new()
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = config,
            ["GIT_AUTHOR_NAME"] = "E3", ["GIT_AUTHOR_EMAIL"] = "e3@example.test", ["GIT_COMMITTER_NAME"] = "E3", ["GIT_COMMITTER_EMAIL"] = "e3@example.test",
            ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C", ["GIT_OPTIONAL_LOCKS"] = "0",
        };
    }

    /// <summary>Makes the folder a Git repository with one empty commit, so a node that edits it can run in its own worktree.</summary>
    public static void InitGit(string folder)
    {
        foreach (var arguments in new[] { new[] { "init", "-q", "-b", "main" }, ["commit", "-q", "--allow-empty", "-m", "base"] })
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in Git) start.Environment[key] = value;
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        }
    }

    /// <summary>
    /// Saves the edits as a project in a folder named "seed", which titles such as "seed - iDevelop" rely on, in a Git
    /// repository of its own.
    /// </summary>
    public static string Seed(this TempFolder temp, params WorkflowEdit[] edits)
    {
        var folder = temp.Create("seed");
        InitGit(folder);
        var document = WorkflowDocument.OpenProject(folder).Single();
        foreach (var edit in edits)
        {
            Assert.IsType<EditResult.Applied>(document.Apply(edit));
        }

        document.Save();
        return folder;
    }
}
