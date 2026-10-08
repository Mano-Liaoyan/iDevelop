using System.Text.Json;
using IDevelop.Workflows;
using static IDevelop.Execution.JsonFields;

namespace IDevelop.Execution;

internal sealed class CodexProtocol(LaunchRequest launch) : TurnProtocol
{
    private readonly Dictionary<string, JsonElement> _requests = [];
    private string? _thread;
    private string? _turn;
    private bool _initialized;
    private bool _started;
    private bool _interruptPending;
    private bool _interrupted;

    public override ProtocolOutput Start() => new([], [Rpc("init-1", "initialize", new
    {
        clientInfo = new { name = "idevelop", version = "1.0.0" }, capabilities = new { experimentalApi = false },
    })], false);

    public override ProtocolOutput Read(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        var method = root.String("method");
        var parameters = root.Property("params");
        if (method is null && root.String("id") is { } id)
        {
            if (id is "init-1" or "thread-1" or "turn-1" && root.Property("error") is { } error)
            {
                throw new ProtocolException(error.String("message") ?? "Codex initialization failed.");
            }

            if (id == "init-1" && !_initialized)
            {
                _initialized = true;
                if (_interrupted)
                {
                    return ProtocolOutput.Empty;
                }

                var native = (NativePolicy.Codex)launch.PolicyFor(ClientId.Codex).Native;
                var args = new Dictionary<string, object?>
                {
                    ["model"] = launch.Model,
                    ["approvalPolicy"] = native.ApprovalPolicy,
                    ["approvalsReviewer"] = native.ApprovalsReviewer,
                    ["sandbox"] = native.Sandbox,
                };
                if (launch.WorkingFolder is { } folder)
                {
                    args["cwd"] = folder;
                }

                if (launch.ResumeSession is { } session)
                {
                    args["threadId"] = session;
                    args["excludeTurns"] = true;
                }

                return new([], [Frame(new { method = "initialized" }), Rpc("thread-1", launch.ResumeSession is null ? "thread/start" : "thread/resume", args)], false);
            }

            if (id == "thread-1" && !_started)
            {
                _started = true;
                if (_interrupted)
                {
                    return ProtocolOutput.Empty;
                }

                _thread = root.Property("result")?.Property("thread")?.String("id") ?? throw new ProtocolException("Codex reported no thread id.");
                return new(NonBlank(_thread) is { } session ? [new AgentEvent.SessionStarted(session)] : [], [Rpc("turn-1", "turn/start", new
                {
                    threadId = _thread, input = new[] { new { type = "text", text = launch.Prompt, text_elements = Array.Empty<object>() } }, effort = launch.Reasoning,
                })], false);
            }

            if (id == "turn-1")
            {
                _turn = root.Property("result")?.Property("turn")?.String("id");
                return _interruptPending ? Interrupt() ?? ProtocolOutput.Empty : ProtocolOutput.Empty;
            }

            return ProtocolOutput.Empty;
        }

        if (method is not null && root.Property("id") is { } requestId)
        {
            return Request(root, method, requestId);
        }

        switch (method)
        {
            case "turn/started":
                _turn = parameters?.Property("turn")?.String("id");
                return _interruptPending ? Interrupt() ?? ProtocolOutput.Empty : ProtocolOutput.Empty;
            case "thread/started" when parameters?.Property("thread")?.String("id") is { } session:
                _thread = session;
                return new([new AgentEvent.SessionStarted(session)], [], false);
            case "item/agentMessage/delta" when parameters?.String("itemId") is { } itemId && parameters?.String("delta") is { } delta:
                return new([new AgentEvent.MessageDelta(itemId, delta)], [], false);
            case "item/completed" when parameters?.Property("item") is { } item && item.String("type") == "agentMessage" && item.String("text") is { } text:
                return new([new AgentEvent.Message(text) { Id = item.String("id"), Partial = _interrupted }], [], false);
            case "item/started" when parameters?.Property("item") is { } command && command.String("type") == "commandExecution":
                return new([new AgentEvent.ToolStarted("command", command.String("command"))], [], false);
            case "error":
                return new([new AgentEvent.Notice(Codex.InnerMessage(parameters?.Property("error")?.String("message") ?? "Codex reported an error."))], [], false);
            case "serverRequest/resolved" when parameters?.Property("requestId") is { } resolved:
                return new([new AgentEvent.RequestClosed(RequestId(resolved))], [], false);
            case "turn/completed" when parameters?.Property("turn") is { } turn:
                return new([turn.String("status") switch
                {
                    "completed" => new AgentEvent.Succeeded(null),
                    "interrupted" => new AgentEvent.Failed("Codex stopped the turn."),
                    "failed" => new AgentEvent.Failed(Codex.InnerMessage(turn.Property("error")?.String("message") ?? "Codex reported a failed turn.")),
                    _ => throw new ProtocolException("Codex reported an unknown terminal turn status."),
                }], [], true);
            default:
                return ProtocolOutput.Empty;
        }
    }

    private ProtocolOutput Request(JsonElement root, string method, JsonElement rawId)
    {
        var id = RequestId(rawId);
        if (_requests.TryGetValue(id, out var prior))
        {
            var before = prior.Property("params");
            var after = root.Property("params");
            var sameInput = before is { } input ? after is { } next && JsonElement.DeepEquals(input, next) : after is null;
            if (prior.String("method") != method || !sameInput)
            {
                throw new ProtocolException($"Codex changed request {id} within the turn.");
            }

            return ProtocolOutput.Empty;
        }

        _requests[id] = root.Clone();
        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            var input = root.Property("params");
            return new([new AgentEvent.PermissionRequested(id, new PermissionAction(method, input?.GetRawText() ?? "{}",
                input?.String("command") ?? input?.String("grantRoot") ?? input?.String("reason") ?? "This turn"))], [], false);
        }

        return new([new AgentEvent.Notice($"Codex requested unsupported method {method}.")],
            [Frame(new { id = rawId, error = new { code = -32601, message = "Method not supported." } })], false);
    }

    public override ProtocolOutput Answer(string requestId, QuestionsReply reply) => throw new ProtocolException("Codex live questions are disabled.");

    public override ProtocolOutput Decline(string requestId) => new([], [Frame(new
    {
        id = _requests[requestId].GetProperty("id"), result = new { decision = "decline" },
    })], false);

    public override ProtocolOutput? Interrupt()
    {
        _interrupted = true;
        _interruptPending = true;
        if (_thread is null || _turn is null)
        {
            return ProtocolOutput.Empty;
        }

        _interruptPending = false;
        return new([], [Rpc("stop-1", "turn/interrupt", new { threadId = _thread, turnId = _turn })], false);
    }

    private static string Rpc(string id, string method, object parameters) => Frame(new { id, method, @params = parameters });
}
