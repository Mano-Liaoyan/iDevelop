using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal sealed partial class Materializer
{
    private ImmutableArray<ArtifactRecord> FreezeOutbox(WorkflowId workflow, RunId run, OperationId operation, AttemptId attempt,
        string destination, string checkout, ref ImmutableArray<EvidenceFile> evidence)
    {
        var manifest = RunLayout.Outbox(attempt) + "/manifest.json";
        var storage = new RunStorage(_project, workflow, run);
        try
        {
            _probe?.Invoke("outbox.before");
            var path = RunStorage.SafePath(checkout, manifest);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _probe?.Invoke("outbox.after");
                return [];
            }
            RegularFile.Verify(path);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (!Members(root, "schema", "artifacts") || root.GetProperty("schema").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 || root.GetProperty("artifacts").ValueKind != JsonValueKind.Array)
                throw new IOException("The outbox manifest must contain schema 1 and an artifacts array only.");
            var declarations = new List<(string Name, string Path)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
            {
                if (!Members(artifact, "name", "path") || artifact.GetProperty("name").ValueKind != JsonValueKind.String ||
                    artifact.GetProperty("path").ValueKind != JsonValueKind.String)
                    throw new IOException("Each artifact must contain a string name and path only.");
                var name = artifact.GetProperty("name").GetString()!;
                var relative = artifact.GetProperty("path").GetString()!;
                if (name.Contains('/') || !names.Add(name)) throw new IOException($"Invalid or duplicate artifact name {name}.");
                RunStorage.SafePath(storage.Folder, destination + "/" + name);
                var source = RunStorage.SafePath(checkout, RunLayout.Outbox(attempt) + "/" + relative);
                RegularFile.Verify(source);
                declarations.Add((name, source));
            }
            var records = ImmutableArray.CreateBuilder<ArtifactRecord>();
            foreach (var declaration in declarations)
            {
                _probe?.Invoke("artifact." + declaration.Name + ".before");
                var bytes = File.ReadAllBytes(declaration.Path);
                var relative = destination + "/" + declaration.Name;
                var digest = Revision.Hash(bytes);
                RunStorage.Publish(storage.Folder, relative, bytes, digest, bytes.LongLength);
                records.Add(new(declaration.Name, relative, digest, bytes.LongLength));
                _probe?.Invoke("artifact." + declaration.Name + ".after");
            }
            _probe?.Invoke("outbox.after");
            return records.ToImmutable();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            var detail = $"Attempt {attempt.Value:D}, manifest {manifest}: {error.Message}";
            var bytes = Encoding.UTF8.GetBytes(detail);
            evidence = [storage.WriteEvidence(OperationIds.Derive(operation, "outbox-" + Revision.Hash(bytes).Sha256), "outbox-rejection.txt", bytes)];
            throw Fault(MaterializationProblem.InputUnavailable, detail);
        }
    }

    private static bool Members(JsonElement value, params string[] members) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(members.Order(StringComparer.Ordinal));
}
