using System.Diagnostics;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.RunRacer;

internal static class Program
{
    private static int Main(string[] args)
    {
        var mode = args[0];
        var project = args[1];
        var workflow = new WorkflowId(Guid.Parse(args[2]));
        var run = new RunId(Guid.Parse(args[3]));
        var store = RunStore.Open(project);
        _ = store.Read(workflow, run);
        switch (mode)
        {
            case "control":
            {
                At(args[4]);
                var take = store.TakeControl(workflow, run);
                Console.WriteLine(Describe(take));
                Wait(args[5]);
                if (take is ControlTake.Owned owned) owned.Permit.Dispose();
                return 0;
            }
            case "lease":
            {
                var task = new TaskId(Guid.Parse(args[4]));
                At(args[5]);
                using var lease = StandaloneLease.TryTake(project, task);
                Console.WriteLine(lease is null ? "Busy" : "Taken");
                Wait(args[6]);
                return 0;
            }
            case "permit-lease":
            {
                var task = new TaskId(Guid.Parse(args[4]));
                var owned = (ControlTake.Owned)store.TakeControl(workflow, run);
                At(args[5]);
                var take = owned.Permit.TakeTask(task);
                Console.WriteLine(take is LeaseTake.Taken ? "Taken" : "Busy");
                Wait(args[6]);
                return 0;
            }
            case "own":
            {
                var task = new TaskId(Guid.Parse(args[4]));
                var attempt = args[5];
                var step = args[6];
                var claimAt = args[7];
                var release = args.Length > 8 ? args[8] : "-";
                var take = store.TakeControl(workflow, run);
                Console.WriteLine(Describe(take));
                if (take is not ControlTake.Owned owned) return 2;
                var lease = ((LeaseTake.Taken)owned.Permit.TakeTask(task)).Lease;
                if (attempt != "-")
                {
                    var record = ((RunRead.Loaded)store.Read(workflow, run)).Record;
                    var id = new AttemptId(Guid.Parse(attempt));
                    var key = new LaunchKey(id, 1);
                    if (claimAt != "-") At(claimAt);
                    var claim = store.Claim(lease, new OperationId(Guid.CreateVersion7()), key,
                        record.Inputs[record.Attempts[id].InitialInputs], record.Preparations[key].PromptHash);
                    Console.WriteLine("claim:" + Describe(claim));
                }
                RunLease? moved = null;
                switch (step)
                {
                    case "hold":
                    case "transfer0":
                        break;
                    case "drop-permit":
                        owned.Permit.Dispose();
                        break;
                    case "transfer1":
                        moved = lease.Transfer();
                        break;
                    case "transfer2":
                        moved = lease.Transfer();
                        lease.Dispose();
                        break;
                    case "transfer3":
                        moved = lease.Transfer();
                        moved.Dispose();
                        break;
                    default:
                        return 4;
                }
                Console.WriteLine("ready:" + lease.Held + "," + (moved?.Held.ToString() ?? "-") + "," + owned.Permit.Held);
                if (args.Length > 8) Wait(release);
                GC.KeepAlive(moved);
                GC.KeepAlive(lease);
                GC.KeepAlive(owned);
                return 0;
            }
            case "closeturn":
            {
                var key = new LaunchKey(new AttemptId(Guid.Parse(args[4])), int.Parse(args[5]));
                var checkpoint = new LogCheckpoint(long.Parse(args[6]), new Digest(args[7]));
                At(args[8]);
                var take = store.TakeControl(workflow, run);
                if (take is not ControlTake.Owned owned) { Console.WriteLine(Describe(take)); return 0; }
                using (owned.Permit)
                {
                    Console.WriteLine(Describe(take) + "|" + Describe(store.CloseTurn(owned.Permit,
                        new OperationId(Guid.CreateVersion7()), key, checkpoint)));
                    if (args.Length > 9) Wait(args[9]);
                }
                return 0;
            }
            case "settle-crash":
            {
                var task = new TaskId(Guid.Parse(args[4]));
                var key = new LaunchKey(new AttemptId(Guid.Parse(args[5])), 1);
                var operation = new OperationId(Guid.Parse(args[6]));
                var take = store.TakeControl(workflow, run);
                Console.WriteLine(Describe(take));
                if (take is not ControlTake.Owned owned) return 2;
                using var permit = owned.Permit;
                using var lease = ((LeaseTake.Taken)permit.TakeTask(task)).Lease;
                var record = ((RunRead.Loaded)store.Read(workflow, run)).Record;
                var prepared = record.Preparations[key];
                var claim = store.Claim(lease, OperationIds.Derive(operation, "claim"), key,
                    record.Inputs[prepared.Inputs], prepared.PromptHash);
                if (claim is not RunDecision.Granted) { Console.WriteLine(Describe(claim)); return 4; }
                var checkout = Path.Combine(project, prepared.Location.Owner.RelativePath);
                File.WriteAllText(Path.Combine(checkout, "result.txt"), "done\n");
                var materializer = Materializer.Open(project, store);
                if (materializer.ObserveRootExit(lease, OperationIds.Derive(operation, "root"), key,
                    new RootExit.Exited(0)) is not RootObservation.Observed) return 5;
                var attempt = record.Attempts[key.Attempt];
                var definition = record.Revisions[attempt.Revision].Snapshot.Tasks[task];
                var folder = store.AttemptFolder(workflow, run, task, key.Attempt);
                var at = DateTimeOffset.UtcNow;
                using (var log = AttemptLog.Create(Path.GetDirectoryName(Path.GetDirectoryName(folder))!, new AttemptEvent.Requested(
                    at, key.Attempt, task, definition.Title, definition.Execution!, prepared.Prompt, "codex", [])
                {
                    RunBinding = new(workflow, run, attempt.Revision, prepared.Inputs), Conversation = definition.Conversation,
                }))
                {
                    log.Append(new AttemptEvent.Agent(at, new AgentEvent.SessionStarted("racer")));
                    log.Append(new AttemptEvent.Agent(at, new AgentEvent.Succeeded("Done.\n")));
                    log.Append(new AttemptEvent.Exited(at, 0, ""));
                }
                var bytes = File.ReadAllBytes(Path.Combine(folder, "events.jsonl"));
                var checkpoint = new LogCheckpoint(bytes.LongLength, Revision.Hash(bytes));
                var crashing = Materializer.Open(project, store, null, TimeProvider.System, null, point =>
                {
                    if (point != "journal.capture-1.after") return;
                    Console.WriteLine("capture-1");
                    Console.Out.Flush();
                    Environment.Exit(73);
                });
                var settlement = crashing.Settle(lease, operation, key, checkpoint).AsTask().GetAwaiter().GetResult();
                Console.WriteLine(settlement);
                return 6;
            }
            case "stale-stop":
            {
                var permit = ((ControlTake.Owned)store.TakeControl(workflow, run)).Permit;
                permit.Dispose();
                Console.WriteLine("ready");
                Wait(args[5]);
                At(args[4]);
                var take = store.TakeControl(workflow, run);
                using var fresh = (take as ControlTake.Owned)?.Permit;
                Console.Write(Describe(take) + "|");
                Console.WriteLine(Describe(store.Stop(permit, new OperationId(Guid.CreateVersion7()))));
                return 0;
            }
            case "stale-reserve":
            {
                var permit = ((ControlTake.Owned)store.TakeControl(workflow, run)).Permit;
                var task = new TaskId(Guid.Parse(args[4]));
                var lease = ((LeaseTake.Taken)permit.TakeTask(task)).Lease;
                var record = ((RunRead.Loaded)store.Read(workflow, run)).Record;
                lease.Dispose();
                permit.Dispose();
                Console.WriteLine("ready");
                Wait(args[6]);
                At(args[5]);
                var take = store.TakeControl(workflow, run);
                using var fresh = (take as ControlTake.Owned)?.Permit;
                Console.Write(Describe(take) + "|");
                var plan = new OperationId(Guid.CreateVersion7());
                var decision = store.Plan(lease, plan, record.Revision.Id, new AttemptCause.Initial());
                if (decision is not RunDecision.Rejected)
                    decision = store.Reserve(lease, new OperationId(Guid.CreateVersion7()), plan);
                Console.WriteLine(Describe(decision));
                return 0;
            }
            case "stop":
            {
                At(args[4]);
                var take = store.TakeControl(workflow, run);
                if (take is not ControlTake.Owned owned) { Console.WriteLine(Describe(take)); return 0; }
                using (owned.Permit) Console.WriteLine(Describe(store.Stop(owned.Permit, new OperationId(Guid.CreateVersion7()))));
                return 0;
            }
            case "reserve":
            {
                var take = store.TakeControl(workflow, run);
                if (take is not ControlTake.Owned owned) { Console.WriteLine(Describe(take)); return 0; }
                using var permit = owned.Permit;
                var task = new TaskId(Guid.Parse(args[4]));
                var leaseTake = permit.TakeTask(task);
                if (leaseTake is not LeaseTake.Taken taken) { Console.WriteLine("Busy"); return 0; }
                using var lease = taken.Lease;
                var record = ((RunRead.Loaded)store.Read(workflow, run)).Record;
                var operation = new OperationId(Guid.CreateVersion7());
                var decision = store.Plan(lease, operation, record.Revision.Id, new AttemptCause.Initial());
                if (decision is not RunDecision.Rejected)
                {
                    var (plannedRecord, plannedEvent) = decision switch
                    {
                        RunDecision.Recorded recorded => (recorded.Record, recorded.Event),
                        RunDecision.Existing existing => (existing.Record, existing.Event),
                        _ => throw new InvalidOperationException("Unexpected plan decision."),
                    };
                    var preparation = ((RunEvent.Planned)plannedEvent).Plan;
                    var plan = plannedRecord.Plans.Single(pair => RunReducer.Same(pair.Value, preparation)).Key;
                    decision = store.Reserve(lease, OperationIds.Derive(operation, "reserve"), plan);
                }
                Console.WriteLine(Describe(decision));
                Wait(args[5]);
                return 0;
            }
            default:
                return 3;
        }
    }

    private static string Describe(ControlTake take) => take switch
    {
        ControlTake.Owned owned => "Owned:" + string.Join(";", owned.Fenced.Select(key => key.Attempt.Value.ToString("D") + "/" + key.Turn)),
        ControlTake.Busy => "Busy",
        ControlTake.Rejected rejected => "Rejected:" + rejected.Reason.Problem,
        _ => "?",
    };

    private static string Describe(RunDecision decision) => decision switch
    {
        RunDecision.Rejected rejected => "Rejected:" + rejected.Reason.Problem,
        RunDecision.Granted granted => "Granted@" + granted.Record.Sequence,
        RunDecision.Recorded recorded => "Recorded@" + recorded.Record.Sequence,
        RunDecision.Existing existing => "Existing@" + existing.Record.Sequence,
        RunDecision.Created created => "Created@" + created.Record.Sequence,
        _ => decision.GetType().Name,
    };

    private static void At(string ticks)
    {
        if (ticks == "-") return;
        var target = long.Parse(ticks);
        var limit = Stopwatch.StartNew();
        while (DateTime.UtcNow.Ticks < target && limit.Elapsed < TimeSpan.FromSeconds(30)) Thread.SpinWait(20);
    }

    private static void Wait(string path)
    {
        if (path == "-") return;
        var limit = Stopwatch.StartNew();
        while (!File.Exists(path) && limit.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(5);
    }
}
