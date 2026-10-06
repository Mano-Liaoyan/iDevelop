using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IDevelop.Workflows;

namespace IDevelop.Execution;

internal static class Revision
{
    public static ApprovedRevision Capture(Workflow workflow) => new(new RevisionId(Hash(Canonical(workflow)).Sha256), workflow);

    public static RunRejection? Check(ApprovedRevision revision) =>
        Capture(revision.Snapshot).Id == revision.Id ? null : new(RunProblem.RevisionMismatch);

    public static string Canonical(Workflow workflow)
    {
        using var stream = new MemoryStream();
        using (var writer = Writer(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("connections");
            foreach (var connection in workflow.Connections.OrderBy(pair => pair.Key))
            {
                writer.WriteStartObject();
                writer.WriteString("from", connection.Key.From.ToString());
                writer.WriteString("kind", connection.Value switch
                {
                    ConnectionKind.Dependency => "dependency",
                    ConnectionKind.Context => "context"
                });
                writer.WriteString("to", connection.Key.To.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("schema", 1);
            writer.WriteStartArray("tasks");
            foreach (var task in workflow.Tasks.Values.OrderBy(task => task.Id))
            {
                WriteTask(writer, task);
            }
            writer.WriteEndArray();
            writer.WriteString("workflow", workflow.Id.ToString());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string CanonicalTask(TaskDefinition task)
    {
        using var stream = new MemoryStream();
        using (var writer = Writer(stream))
        {
            WriteTask(writer, task);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static Digest Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    internal static Digest Hash(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    internal static bool IsHash(string? value) => IsHex(value, 64);

    internal static bool IsCommit(string? value) => IsHex(value, 40) || IsHex(value, 64);

    private static bool IsHex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Utf8JsonWriter Writer(Stream stream) =>
        new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static void WriteTask(Utf8JsonWriter writer, TaskDefinition task)
    {
        writer.WriteStartObject();
        writer.WriteString("blueprint", task.Blueprint.Key.ToString());
        writer.WriteString("conversation", task.Conversation switch
        {
            ConversationMode.Autonomous => "autonomous",
            ConversationMode.MayAsk => "mayAsk",
            ConversationMode.Chat => "chat",
        });
        writer.WritePropertyName("execution");
        if (task.Execution is not { } execution)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("client", Clients.WireName(execution.Client));
            writer.WriteString("model", execution.Model);
            writer.WriteString("reasoning", execution.Reasoning);
            writer.WriteEndObject();
        }
        writer.WriteStartArray("fields");
        foreach (var field in task.Blueprint.Fields.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", field.Key);
            writer.WriteBoolean("required", field.Required);
            writer.WriteString("value", task.Field(field.Key));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("id", task.Id.ToString());
        writer.WriteString("title", task.Title);
        writer.WriteStartObject("work");
        switch (task.Blueprint.Work)
        {
            case WorkSpec.Agent agent:
                writer.WriteString("access", agent.Access switch
                {
                    AgentAccess.ReadOnly => "readOnly",
                    AgentAccess.Edit => "edit"
                });
                writer.WriteString("kind", "agent");
                writer.WriteBoolean("proposes", agent.Proposes);
                writer.WriteString("template", agent.Template.Text);
                break;
            case WorkSpec.Review review:
                writer.WriteString("fix", review.Fix.Text);
                writer.WriteString("kind", "review");
                writer.WriteString("reviewer", review.Reviewer.Text);
                break;
            case WorkSpec.Person:
                writer.WriteString("kind", "person");
                break;
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
