using IDevelop.Execution;
using IDevelop.Projects;

namespace IDevelop.RunRacer;

/// <summary>Confirms a preview of a workflow and exits at one durable step of the approval, as a crash ends it.</summary>
internal static class ApprovalRacer
{
    /// <param name="args">
    /// The mode, the project, the workflow file, the fake clients' folder, the confirmation, the base, the crash point, and
    /// optionally <c>include</c>, which includes every planner the preview offers on the base.
    /// </param>
    public static int Crash(string[] args)
    {
        var project = args[1];
        var workflow = WorkflowFile.Parse(File.ReadAllBytes(args[2]), args[2]).Workflow;
        var clients = new ClientDirectory(CommandResolver.Create([args[3]], OperatingSystem.IsWindows() ? [".COM", ".EXE", ".BAT", ".CMD"] : []));
        clients.RefreshAsync().GetAwaiter().GetResult();
        var runs = ProjectRuns.Open(project, clients);
        var crashPoint = args[6];
        runs.Probe = point =>
        {
            if (point != crashPoint) return;
            Console.WriteLine(point);
            Console.Out.Flush();
            Environment.Exit(73);
        };
        var preview = runs.Preflight(workflow);
        Console.WriteLine("Previewed");
        Console.Out.Flush();
        var choice = Enum.Parse<BaseChoice>(args[5]);
        var confirmation = new RunConfirmation(preview, choice, new OperationId(Guid.Parse(args[4])))
        {
            Include = args.Length > 7 && args[7] == "include"
                ? [.. preview.Planners.Where(planner => planner.Bases.Contains(choice)).Select(planner => new ReportInclusion(planner.Task, planner.Source, planner.Turn))]
                : [],
        };
        Console.WriteLine(runs.StartWorkflow(workflow, confirmation).GetAwaiter().GetResult());
        runs.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 6;
    }
}
