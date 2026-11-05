using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.TestSupport;

/// <summary>The turns of the one fake Antigravity CLI that the fixtures install.</summary>
internal static class AgyTurn
{
    /// <summary>The steps of a turn that reports <paramref name="session"/> and replies <paramref name="reply"/>, after <paramref name="first"/>.</summary>
    public static FakeRule Reply(string session, string reply, FakeRule? first = null) =>
        (first ?? On()).Print(FakeAgents.SessionLine(ClientId.Antigravity, session)).Print(FakeAgents.ReplyLines(ClientId.Antigravity, reply));

    /// <summary>The start of the stdin line of a fresh turn of the node titled <paramref name="title"/>.</summary>
    public static string Fresh(string title) => """{"event":"user","message":{"role":"user","content":"# """ + title + "\\n";
}
