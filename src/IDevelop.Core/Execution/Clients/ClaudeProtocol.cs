using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

internal sealed class ClaudeProtocol(LaunchRequest launch) : TurnProtocol
{
    private readonly Dictionary<string, JsonElement> _requests = [];
    private readonly Dictionary<string, string> _tools = [];
    private readonly Dictionary<string, int> _blocks = [];
    private string? _messageId;
    private bool _initialized;
    private bool _interrupted;

    public override ProtocolOutput Start() => new([], [Control("init-1", "initialize")], false);

    public override ProtocolOutput Read(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        switch (root.String("type"))
        {
            case "control_response" when root.Property("response") is { } response && response.String("request_id") == "init-1":
                if (_initialized)
                {
                    return ProtocolOutput.Empty;
                }

                if (response.String("subtype") != "success")
                {
                    throw new ProtocolException("Claude Code initialization failed.");
                }

                _initialized = true;
                if (_interrupted)
                {
                    return ProtocolOutput.Empty;
                }

                return new([], [Frame(new { type = "user", message = new { role = "user", content = launch.Prompt }, parent_tool_use_id = (string?)null })], false);
            case "control_request":
                return Request(root);
            case "control_cancel_request" when root.String("request_id") is { } cancelled:
                return new([new AgentEvent.RequestClosed("s:" + cancelled)], [], false);
            case "user":
                return new([.. root.Property("message")?.Items("content").Where(part => part.String("type") == "tool_result")
                    .Select(part => part.String("tool_use_id")).OfType<string>().Where(_tools.ContainsKey)
                    .Select(tool => new AgentEvent.RequestClosed(_tools[tool])) ?? []], [], false);
            case "stream_event" when root.Property("event") is { } stream:
                if (stream.String("type") == "message_start")
                {
                    _messageId = stream.Property("message")?.String("id");
                }

                if (_messageId is { } id && stream.Property("index") is { } index)
                {
                    _blocks[id] = index.GetInt32();
                    if (stream.String("type") == "content_block_delta" && stream.Property("delta") is { } delta
                        && delta.String("type") == "text_delta" && delta.String("text") is { } text)
                    {
                        return new([new AgentEvent.MessageDelta($"{id}:{index.GetInt32()}", text)], [], false);
                    }
                }

                return ProtocolOutput.Empty;
            case "assistant" when root.Property("message") is { } message:
                var events = ClaudeCode.Events(root);
                var messageId = message.String("id");
                var parts = message.Items("content").ToArray();
                var textIndexes = new Queue<int>(parts.Select((part, index) => (part, index)).Where(pair => pair.part.String("type") == "text" && NonBlank(pair.part.String("text")) is not null)
                    .Select(pair => parts.Length == 1 && messageId is not null && _blocks.TryGetValue(messageId, out var block) ? block : pair.index));
                return new([.. events.Select(e => e is AgentEvent.Message text
                    ? text with { Id = messageId is null ? null : $"{messageId}:{textIndexes.Dequeue()}", Partial = _interrupted } : e)], [], false);
            case "result":
                return new(ClaudeCode.Events(root), [], true);
            default:
                return new(ClaudeCode.Events(root), [], false);
        }
    }

    private ProtocolOutput Request(JsonElement root)
    {
        var rawId = root.String("request_id") ?? throw new ProtocolException("Claude Code supplied no request id.");
        var id = "s:" + rawId;
        var request = root.Property("request") ?? throw new ProtocolException("Claude Code supplied no request.");
        if (_requests.TryGetValue(id, out var prior))
        {
            if (!JsonElement.DeepEquals(prior, request))
            {
                throw new ProtocolException($"Claude Code changed request {id} within the turn.");
            }

            return ProtocolOutput.Empty;
        }

        _requests.Add(id, request.Clone());
        if (request.String("subtype") != "can_use_tool")
        {
            return new([new AgentEvent.Notice("Claude Code requested an unsupported control method.")],
                [Frame(new { type = "control_response", response = new { subtype = "error", request_id = rawId, error = "Method not supported." } })], false);
        }

        if (request.String("tool_use_id") is { } tool)
        {
            _tools[tool] = id;
        }

        var input = request.Property("input");
        if (request.String("tool_name") == "AskUserQuestion")
        {
            ImmutableArray<AskedQuestion> questions = [.. (input?.Items("questions") ?? []).Select((question, index) => new AskedQuestion(
                $"q:{index}", question.String("header") ?? "", question.String("question") ?? "",
                [.. question.Items("options").Select((option, ordinal) => new QuestionOption($"o:{ordinal}", option.String("label") ?? "", option.String("description")))],
                question.Bool("multiSelect") == true, AllowsOther: true))];
            return new([new AgentEvent.QuestionAsked(id, questions)], [], false);
        }

        return new([new AgentEvent.PermissionRequested(id, new PermissionAction(request.String("tool_name") ?? "unknown",
            input?.GetRawText() ?? "{}", input?.String("file_path") ?? input?.String("command") ?? request.String("description") ?? "This turn"))], [], false);
    }

    public override ProtocolOutput Answer(string requestId, QuestionsReply reply)
    {
        var request = _requests[requestId];
        var input = JsonNode.Parse(request.Property("input")?.GetRawText() ?? "{}")!.AsObject();
        var answers = new JsonObject();
        var questions = request.Property("input")?.Items("questions").ToArray() ?? [];
        foreach (var answer in reply.Answers)
        {
            var index = int.Parse(answer.QuestionId.AsSpan(2));
            var question = questions[index];
            var options = question.Items("options").ToArray();
            var labels = answer.OptionIds.Select(id => options[int.Parse(id.AsSpan(2))].String("label")!);
            answers[question.String("question")!] = string.Join(", ", labels.Concat(answer.Text is { Length: > 0 } text ? [text] : []));
        }

        input["answers"] = answers;
        return Response(requestId, new { behavior = "allow", updatedInput = input });
    }

    public override ProtocolOutput Decline(string requestId) => Response(requestId, new { behavior = "deny", message = "iDevelop declined this request by policy." });

    private static ProtocolOutput Response(string id, object response) => new([], [Frame(new
    {
        type = "control_response", response = new { subtype = "success", request_id = id[2..], response },
    })], false);

    public override ProtocolOutput? Interrupt()
    {
        _interrupted = true;
        return new([], [Control("stop-1", "interrupt")], false);
    }

    private static string Control(string id, string subtype) => Frame(new { type = "control_request", request_id = id, request = new { subtype } });
}
