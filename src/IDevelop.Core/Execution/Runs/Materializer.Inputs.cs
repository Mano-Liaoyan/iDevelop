using System.Text;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private static void VerifyArtifacts(RunRecord record, MaterializationPlan.Preparation plan, RunStorage storage)
    {
        foreach (var binding in plan.Bindings.OfType<InputBinding.Provided>().Where(binding => binding.Kind == Workflows.ConnectionKind.Dependency))
        {
            var result = record.Results.Single(result => result.Id == binding.Result);
            foreach (var artifact in result.Artifacts)
            {
                try { storage.ReadArtifact(result.Id, artifact); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw Fault(MaterializationProblem.InputUnavailable, $"Result {result.Id.Value:D}, stored path {artifact.StoredPath}: {error.Message}"); }
            }
        }
    }

    private static byte[] DeliveredBytes(RunRecord record, DeliveredFile file, RunStorage storage)
    {
        var result = record.Results.Single(result => result.Id == file.Source);
        if (file.RelativePath.EndsWith($"/{result.Id.Value:D}/report.md", StringComparison.Ordinal)) return Encoding.UTF8.GetBytes(result.Report);
        var artifact = result.Artifacts.Single(artifact => file.RelativePath.EndsWith("/artifacts/" + artifact.Name, StringComparison.Ordinal));
        try { return storage.ReadArtifact(result.Id, artifact); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Fault(MaterializationProblem.InputUnavailable, $"Result {result.Id.Value:D}, stored path {artifact.StoredPath}: {error.Message}"); }
    }

    private static void Deliver(RunRecord record, InputRecord input, GitRepository repository, WorktreeOwner owner, RunStorage storage)
    {
        var checkout = Checkout(repository, owner);
        foreach (var file in input.Files)
        {
            try { RunStorage.Publish(checkout, file.RelativePath, DeliveredBytes(record, file, storage), file.Content, file.ByteLength); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Fault(MaterializationProblem.InputUnavailable, $"Result {file.Source.Value:D}, delivery {file.RelativePath}: {error.Message}"); }
            if (repository.CheckIgnore(checkout, file.RelativePath).ExitCode != 0 ||
                repository.CheckIgnore(repository.ProjectFolder, file.RelativePath).ExitCode != 0)
                throw Fault(MaterializationProblem.InputUnavailable, "Delivered input is not ignored: " + file.RelativePath);
        }
    }

    private static void VerifyDelivery(RunRecord record, PreparedExecution execution, GitRepository repository)
    {
        var checkout = Checkout(repository, execution.Location.Owner);
        foreach (var file in record.Inputs[execution.Inputs].Files)
        {
            RunStorage.Read(checkout, file.RelativePath, file.Content, file.ByteLength);
            if (repository.CheckIgnore(checkout, file.RelativePath).ExitCode != 0 ||
                repository.CheckIgnore(repository.ProjectFolder, file.RelativePath).ExitCode != 0)
                throw Fault(MaterializationProblem.InputUnavailable, "Delivered input is not ignored: " + file.RelativePath);
        }
        if (execution.OutboxPath.Length != 0 && !Directory.Exists(RunStorage.SafePath(checkout, execution.OutboxPath)))
            throw Fault(MaterializationProblem.InputUnavailable, "The prepared outbox is absent.");
        VerifyOutbox(repository, checkout, execution.OutboxPath);
    }

    private static void VerifyOutbox(GitRepository repository, string checkout, string outbox)
    {
        if (outbox.Length != 0 && (repository.CheckIgnore(checkout, outbox + "/manifest.json").ExitCode != 0 ||
            repository.CheckIgnore(repository.ProjectFolder, outbox + "/manifest.json").ExitCode != 0))
            throw Fault(MaterializationProblem.InputUnavailable, "The outbox is not ignored: " + outbox);
    }

    private static EvidenceFile Snapshot(RunStorage storage, OperationId operation, GitRepository repository, RunRecord record)
    {
        var refs = SharedRefSnapshot(repository, record);
        return storage.WriteEvidence(operation, "shared-refs.json", Encoding.UTF8.GetBytes(RunJournal.Canonical(refs)));
    }
}
