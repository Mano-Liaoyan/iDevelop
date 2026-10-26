using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using static IDevelop.Clients.JsonFields;

namespace IDevelop.Clients;

internal sealed class ClaudeProtocol(LaunchRequest launch) : TurnProtocol
{
    private readonly Dictionary<string, JsonElement> _requests = [];
    private readonly Dictionary<string, string> _tools = [];
    private readonly Dictionary<string, int> _blocks = [];
    // Each subagent by the tool call that started it, and each task the client reported by its id.
    private readonly Dictionary<string, string> _subagents = [];
    private readonly Dictionary<string, BackgroundTask> _tasks = [];
    // The background tasks that still run, then those that ended and whose answer the agent has yet to give in a result.
    private readonly List<string> _running = [];
    private readonly List<string> _answering = [];
    private ImmutableArray<BackgroundTask> _reported = [];
    private string? _messageId;
    private bool _initialized;
    private bool _interrupted;

    // A node's skill choice is an allow-list, which a resumed session does not keep, so every turn's initialize sends it.
    public override ProtocolOutput Start() => new([], [launch.Skills is { } skills
        ? Frame(new { type = "control_request", request_id = "init-1", request = new { subtype = "initialize", skills = skills.Chosen.Select(skill => skill.Name).ToArray() } })
        : Control("init-1", "initialize")], false);

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
            // A subagent's text shows once its message is complete, so it never streams as the agent's own.
            case "stream_event" when root.String("parent_tool_use_id") is not null:
                return ProtocolOutput.Empty;
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
            case "assistant" when root.Property("message") is { } message && root.String("parent_tool_use_id") is { } parent:
                return new([.. Subagent(message, _subagents.GetValueOrDefault(parent) ?? "Subagent")], [], false);
            case "assistant" when root.Property("message") is { } message:
                foreach (var call in message.Items("content").Where(part => part.String("type") == "tool_use" && part.String("name") is "Agent" or "Task"))
                {
                    if (call.String("id") is { } callId)
                    {
                        _subagents[callId] = NonBlank(call.Property("input")?.String("subagent_type")) ?? NonBlank(call.Property("input")?.String("description")) ?? "Subagent";
                    }
                }

                var events = ClaudeCode.Events(root);
                var messageId = message.String("id");
                var parts = message.Items("content").ToArray();
                var textIndexes = new Queue<int>(parts.Select((part, position) => (part, index: position)).Where(pair => pair.part.String("type") == "text" && NonBlank(pair.part.String("text")) is not null)
                    .Select(pair => parts.Length == 1 && messageId is not null && _blocks.TryGetValue(messageId, out var block) ? block : pair.index));
                return new([.. events.Select(e => e is AgentEvent.Message text
                    ? text with { Id = messageId is null ? null : $"{messageId}:{textIndexes.Dequeue()}", Partial = _interrupted } : e)], [], false);
            case "system" when root.String("subtype") is "task_started" or "task_updated" or "task_notification" or "background_tasks_changed":
                return new(Track(root), [], false);
            case "result":
                _answering.Clear();
                return new([.. ClaudeCode.Events(root), .. Background()], [], true);
            default:
                return new(ClaudeCode.Events(root), [], false);
        }
    }

    /// <summary>A subagent's complete message, as its own: its text, but no model of its own to report for the turn.</summary>
    private static IEnumerable<AgentEvent> Subagent(JsonElement message, string name)
    {
        var id = message.String("id");
        return message.Items("content").Select((part, index) => (Text: part.String("type") == "text" ? NonBlank(part.String("text")) : null, index))
            .Where(pair => pair.Text is not null)
            .Select(pair => new AgentEvent.Message(pair.Text!) { Id = id is null ? null : $"{id}:{pair.index}", Subagent = name });
    }

    /// <summary>
    /// Follows the client's background tasks. A -p run answers each one that ends in a turn of its own after stdin has
    /// closed, so a task counts until the result that follows its notification.
    /// </summary>
    private ImmutableArray<AgentEvent> Track(JsonElement root)
    {
        var id = root.String("task_id");
        switch (root.String("subtype"))
        {
            case "task_started" when id is not null:
                var subagent = NonBlank(root.String("subagent_type"));
                _tasks[id] = new BackgroundTask(NonBlank(root.String("description")) ?? subagent ?? "Background task", subagent is not null);
                if (subagent is not null && root.String("tool_use_id") is { } call)
                {
                    _subagents.TryAdd(call, subagent);
                }

                if (root.Bool("is_backgrounded") == true && root.Bool("awaited") != true)
                {
                    Run(id);
                }

                break;
            case "task_updated" when id is not null && root.Property("patch") is { } patch:
                if (patch.Bool("is_backgrounded") == true && _tasks.ContainsKey(id))
                {
                    Run(id);
                }

                if (patch.String("status") is "completed" or "failed" or "killed")
                {
                    Ended(id);
                }

                break;
            case "task_notification" when id is not null:
                Ended(id);
                break;
            case "background_tasks_changed":
                var live = root.Items("tasks").Where(task => task.Bool("ambient") != true && task.Bool("awaited") != true)
                    .Select(task => (Id: task.String("task_id"), Task: task)).Where(pair => pair.Id is not null).ToArray();
                foreach (var (taskId, task) in live)
                {
                    _tasks.TryAdd(taskId!, new BackgroundTask(NonBlank(task.String("description")) ?? "Background task", task.String("task_type") == "local_agent"));
                }

                // A task that left the list ended, so the agent answers it as it would a notification.
                foreach (var ended in _running.Where(running => live.All(pair => pair.Id != running)).ToArray())
                {
                    Ended(ended);
                }

                foreach (var (taskId, _) in live)
                {
                    Run(taskId!);
                }

                break;
        }

        return Background();
    }

    private void Run(string id)
    {
        if (!_running.Contains(id) && !_answering.Contains(id))
        {
            _running.Add(id);
        }
    }

    private void Ended(string id)
    {
        if (_running.Remove(id))
        {
            _answering.Add(id);
        }
    }

    private ImmutableArray<AgentEvent> Background()
    {
        ImmutableArray<BackgroundTask> now = [.. _running.Concat(_answering).Select(id => _tasks.GetValueOrDefault(id) ?? new BackgroundTask("Background task", false))];
        if (now.SequenceEqual(_reported))
        {
            return [];
        }

        _reported = now;
        return [new AgentEvent.BackgroundTasks(now)];
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
            answers[question.String("question")!] = answer.Words(answer.OptionIds.Select(id => options[int.Parse(id.AsSpan(2))].String("label")!));
        }

        input["answers"] = answers;
        return Response(requestId, new { behavior = "allow", updatedInput = input });
    }

    public override ProtocolOutput Decline(string requestId) => Response(requestId, new { behavior = "deny", message = "iDevelop declined this request by policy." });

    private static ProtocolOutput Response(string id, object response) => new([], [Frame(new
    {
        type = "control_response", response = new { subtype = "success", request_id = id[2..], response },
    })], false);

    public override ProtocolOutput Interrupt()
    {
        _interrupted = true;
        return new([], [Control("stop-1", "interrupt")], false);
    }

    private static string Control(string id, string subtype) => Frame(new { type = "control_request", request_id = id, request = new { subtype } });
}
