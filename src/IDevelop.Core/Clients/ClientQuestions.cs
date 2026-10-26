using System.Collections.Immutable;

namespace IDevelop.Clients;

public sealed record QuestionOption(string Id, string Label, string? Detail);

/// <summary>A question a client asks on its own channel, which a client that surfaces questions passes to iDevelop.</summary>
public sealed record AskedQuestion(
    string Id, string Header, string Text, ImmutableArray<QuestionOption> Options,
    bool MultiSelect, bool AllowsOther);

public sealed record QuestionAnswer(
    string QuestionId, ImmutableArray<string> OptionIds, string? Text);

public sealed record QuestionsReply(ImmutableArray<QuestionAnswer> Answers);

/// <summary>A tool call the client asks permission for.</summary>
public sealed record PermissionAction(string Tool, string InputJson, string Scope);
