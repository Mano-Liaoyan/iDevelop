using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Execution.AgentEvent;

namespace IDevelop.Core.Tests;

public sealed class TurnProtocolTests
{
    [Fact]
    public void Claude_stream_blocks_keep_the_provider_message_and_block_index()
    {
        var protocol = Protocol(ClientId.ClaudeCode);
        var events = Read(protocol, "claude-stream");
        Assert.Equal(
            [new MessageDelta("msg_011CfmXizzDgRoimu3MfUfFj:1", "Rivers are flowing"),
             new MessageDelta("msg_011CfmXizzDgRoimu3MfUfFj:1", " bodies of water that"),
             new MessageDelta("msg_011CfmXizzDgRoimu3MfUfFj:1", " transport")], events.OfType<MessageDelta>());
        var message = Assert.Single(events.OfType<Message>());
        Assert.Equal("msg_011CfmXizzDgRoimu3MfUfFj:1", message.Id);
        Assert.Equal("Rivers are flowing bodies of water that transport water from higher elevations to oceans, seas, or lakes, shaping landscapes through erosion and sediment deposition. They sustain ecosystems by providing freshwater habitats for diverse wildlife and plants, while also serving as essential resources for human communities. Throughout history, major rivers like the Nile, Amazon, and Yangtze have supported civilizations by enabling agriculture, transportation, and trade.", message.Text);
    }

    [Fact]
    public void Claude_two_text_blocks_have_two_complete_message_ids()
    {
        var output = Protocol(ClientId.ClaudeCode).Read("""{"type":"assistant","message":{"id":"msg-1","content":[{"type":"text","text":"First"},{"type":"text","text":"Second"}]}}""");
        Assert.Equal([new Message("First") { Id = "msg-1:0" }, new Message("Second") { Id = "msg-1:1" }], output.Events.ToArray());
    }

    [Fact]
    public void Claude_initializes_before_sending_the_user_envelope_and_only_once()
    {
        var protocol = Protocol(ClientId.ClaudeCode);
        Assert.Equal(["""{"type":"control_request","request_id":"init-1","request":{"subtype":"initialize"}}"""], protocol.Start().Writes.ToArray());
        const string response = """{"type":"control_response","response":{"subtype":"success","request_id":"init-1","response":{}}}""";
        Assert.Equal(["""{"type":"user","message":{"role":"user","content":"banana"},"parent_tool_use_id":null}"""], protocol.Read(response).Writes.ToArray());
        Assert.Equal(ProtocolOutput.Empty, protocol.Read(response));
    }

    [Fact]
    public void Claude_question_has_literal_fields_and_answers_preserve_the_input()
    {
        var protocol = Protocol(ClientId.ClaudeCode);
        var asked = Assert.Single(Read(protocol, "claude-question").OfType<QuestionAsked>());
        Assert.Equal("s:ada321fa-6765-4224-9487-d38b0bba8d54", asked.RequestId);
        var question = Assert.Single(asked.Questions);
        Assert.Equal(("q:0", "Fruit choice", "Which fruit should go into the answer?", false, true),
            (question.Id, question.Header, question.Text, question.MultiSelect, question.AllowsOther));
        Assert.Equal([new QuestionOption("o:0", "Apple", "A crisp, sweet-tart fruit"), new QuestionOption("o:1", "Banana", "A creamy, naturally sweet fruit")], question.Options.ToArray());
        using var answer = JsonDocument.Parse(Assert.Single(protocol.Answer(asked.RequestId, new QuestionsReply([new QuestionAnswer("q:0", ["o:1"], null)])).Writes.ToArray()));
        var input = answer.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput");
        Assert.Equal("Banana", input.GetProperty("answers").GetProperty("Which fruit should go into the answer?").GetString());
        Assert.Equal("Fruit choice", input.GetProperty("questions")[0].GetProperty("header").GetString());
        var multi = Assert.IsType<QuestionAsked>(Assert.Single(Protocol(ClientId.ClaudeCode).Read("""{"type":"control_request","request_id":"q","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"header":"Checks","question":"Which checks?","multiSelect":true,"options":[{"label":"Build"},{"label":"Tests"}]}]}}}""").Events.ToArray()));
        Assert.True(Assert.Single(multi.Questions).MultiSelect);
    }

    [Fact]
    public void Claude_permissions_include_ExitPlanMode_and_decline_uses_deny()
    {
        var protocol = Protocol(ClientId.ClaudeCode);
        var recorded = Assert.Single(Read(protocol, "claude-permission").OfType<PermissionRequested>());
        Assert.Equal(("s:bb00425b-9a51-4a07-a7b5-bb4be1a930f2", "Write", "/project/probe2.txt"), (recorded.RequestId, recorded.Action.Tool, recorded.Action.Scope));
        var permission = Assert.IsType<PermissionRequested>(Assert.Single(protocol.Read("""{"type":"control_request","request_id":"plan-1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"Change files"}}}""").Events.ToArray()));
        Assert.Equal("ExitPlanMode", permission.Action.Tool);
        Assert.Equal(["""{"type":"control_response","response":{"subtype":"success","request_id":"plan-1","response":{"behavior":"deny","message":"iDevelop declined this request by policy."}}}"""], protocol.Decline(permission.RequestId).Writes.ToArray());
    }

    [Fact]
    public void Claude_cancel_and_tool_result_close_the_correlated_request()
    {
        var protocol = Protocol(ClientId.ClaudeCode);
        Assert.Equal([new RequestClosed("s:638f7046-62e9-41a5-962d-8b4c96059bbd")], Read(protocol, "claude-cancel"));
        Read(protocol, "claude-question");
        Assert.Equal([new RequestClosed("s:ada321fa-6765-4224-9487-d38b0bba8d54")], protocol.Read("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_01Qi5ARATkSKCLVJcJZW8zqE"}]}}""").Events.ToArray());
    }

    [Fact]
    public void Claude_interrupt_acknowledgement_leaves_the_aborted_tail_partial_until_result()
    {
        var protocol = Protocol(ClientId.ClaudeCode);
        var lines = Fixture.Lines("c1/claude-stop.jsonl").ToArray();
        foreach (var line in lines.Take(3)) protocol.Read(line);
        Assert.Equal(["""{"type":"control_request","request_id":"stop-1","request":{"subtype":"interrupt"}}"""], protocol.Interrupt()!.Writes.ToArray());
        Assert.False(protocol.Read(lines[3]).CloseInput);
        var tail = Assert.IsType<Message>(Assert.Single(protocol.Read(lines[4]).Events.ToArray().OfType<Message>()));
        Assert.Equal(("msg_011CfmXoknpVZYbTR5TqHgDB:1", "1 apple\n2 ball\n3 cat\n4 dog\n5 egg\n6 fish\n7 gate\n8 hat\n9 ice\n10", true), (tail.Id, tail.Text, tail.Partial));
        Assert.True(protocol.Read(lines[5]).CloseInput);
    }

    [Fact]
    public void Codex_initializes_resumes_and_starts_a_turn_with_the_schema_fields()
    {
        var protocol = Clients.Get(ClientId.Codex).Protocol(new LaunchRequest("gpt-6-sol", "high", "banana") { ResumeSession = "thread-a", ReadOnly = true });
        Assert.Equal(["""{"id":"init-1","method":"initialize","params":{"clientInfo":{"name":"idevelop","version":"1.0.0"},"capabilities":{"experimentalApi":false}}}"""], protocol.Start().Writes.ToArray());
        Assert.Equal(["""{"method":"initialized"}""", """{"id":"thread-1","method":"thread/resume","params":{"model":"gpt-6-sol","approvalPolicy":"never","approvalsReviewer":"user","sandbox":"read-only","threadId":"thread-a"}}"""], protocol.Read("""{"id":"init-1","result":{}}""").Writes.ToArray());
        var started = protocol.Read("""{"id":"thread-1","result":{"thread":{"id":"thread-a"}}}""");
        Assert.Equal([new SessionStarted("thread-a")], started.Events.ToArray());
        Assert.Equal(["""{"id":"turn-1","method":"turn/start","params":{"threadId":"thread-a","input":[{"type":"text","text":"banana","text_elements":[]}],"effort":"high"}}"""], started.Writes.ToArray());
        protocol.Read("""{"id":"turn-1","result":{"turn":{"id":"turn-a"}}}""");
        Assert.Equal(["""{"id":"stop-1","method":"turn/interrupt","params":{"threadId":"thread-a","turnId":"turn-a"}}"""], protocol.Interrupt()!.Writes.ToArray());
    }

    [Fact]
    public void Codex_recorded_agent_deltas_complete_with_the_item_id()
    {
        var events = Read(Protocol(ClientId.Codex), "codex-stream");
        Assert.Equal(["You", " chose", " **", "banana", "**", "."], events.OfType<MessageDelta>().Select(e => e.Text));
        Assert.Equal(new Message("You chose **banana**.") { Id = "msg_00f079ffd4a00a6b016ac54b93c15487d28b63e565c27d9469" }, Assert.Single(events.OfType<Message>()));
        Assert.Equal(new Succeeded(null), events.Last());
    }

    [Fact]
    public void Codex_numeric_zero_approval_declines_and_string_zero_remains_distinct()
    {
        var protocol = Protocol(ClientId.Codex);
        var request = Assert.Single(Read(protocol, "codex-approval").OfType<PermissionRequested>());
        Assert.Equal("n:0", request.RequestId);
        Assert.Equal("item/commandExecution/requestApproval", request.Action.Tool);
        Assert.Equal(["""{"id":0,"result":{"decision":"decline"}}"""], protocol.Decline(request.RequestId).Writes.ToArray());
        var other = Assert.IsType<PermissionRequested>(Assert.Single(protocol.Read("""{"id":"0","method":"item/fileChange/requestApproval","params":{"itemId":"file-1"}}""").Events.ToArray()));
        Assert.Equal("s:0", other.RequestId);
        Assert.Equal(["""{"id":"0","result":{"decision":"decline"}}"""], protocol.Decline(other.RequestId).Writes.ToArray());
        Assert.Equal([new RequestClosed("n:0")], protocol.Read("""{"method":"serverRequest/resolved","params":{"requestId":0,"threadId":"thread-a"}}""").Events.ToArray());
    }

    [Fact]
    public void Unknown_Codex_requests_get_a_method_error_and_notice()
    {
        var output = Protocol(ClientId.Codex).Read("""{"id":7,"method":"item/tool/requestUserInput","params":{}}""");
        Assert.Equal([new Notice("Codex requested unsupported method item/tool/requestUserInput.")], output.Events.ToArray());
        Assert.Equal(["""{"id":7,"error":{"code":-32601,"message":"Method not supported."}}"""], output.Writes.ToArray());
    }

    [Theory]
    [InlineData(ClientId.ClaudeCode, "claude-question")]
    [InlineData(ClientId.Codex, "codex-approval")]
    public void Repeated_requests_are_idempotent_and_changed_content_is_a_protocol_error(ClientId client, string fixture)
    {
        var protocol = Protocol(client);
        var line = Fixture.Lines($"c1/{fixture}.jsonl").Single();
        Assert.Single(protocol.Read(line).Events.ToArray());
        Assert.Equal(ProtocolOutput.Empty, protocol.Read(line));
        Assert.Throws<ProtocolException>(() => protocol.Read(line.Replace(client == ClientId.Codex ? "probe.txt" : "Apple", "Changed", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pi_and_Antigravity_stream_text_using_their_message_boundaries()
    {
        var pi = Read(Protocol(ClientId.Pi), "pi-stream");
        Assert.Equal([new MessageDelta("local:0", "R"), new MessageDelta("local:0", "ivers")], pi.OfType<MessageDelta>());
        Assert.Equal(new Message("Rivers flow across landscapes.  \nThey provide water for people and wildlife.  \nMany rivers eventually meet the sea.") { Id = "local:0" }, Assert.Single(pi.OfType<Message>()));
        var agy = Read(Protocol(ClientId.Antigravity), "agy-stream");
        Assert.Equal("Rivers flow continuously", agy.OfType<MessageDelta>().First().Text);
        Assert.Equal("step:1", agy.OfType<MessageDelta>().First().MessageId);
        Assert.Equal(new Message("Rivers flow continuously from high elevations toward lakes, seas, or oceans. They shape diverse landscapes and provide vital freshwater for ecosystems and human civilizations. Over time, their currents carve deep valleys and fertile plains across the earth.") { Id = "step:1" }, Assert.Single(agy.OfType<Message>()));
    }

    [Fact]
    public void Antigravity_done_without_a_delta_completes_its_buffer()
    {
        var protocol = Protocol(ClientId.Antigravity);
        Assert.Equal([new MessageDelta("step:2", "Hello")], protocol.Read("""{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"Hello"}}""").Events.ToArray());
        Assert.Equal([new Message("Hello") { Id = "step:2" }], protocol.Read("""{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response"}}""").Events.ToArray());
    }

    private static TurnProtocol Protocol(ClientId client) => Clients.Get(client).Protocol(new LaunchRequest("m", "low", "banana"));
    private static AgentEvent[] Read(TurnProtocol protocol, string name) => [.. Fixture.Lines($"c1/{name}.jsonl").SelectMany(line => protocol.Read(line).Events.ToArray())];
}
