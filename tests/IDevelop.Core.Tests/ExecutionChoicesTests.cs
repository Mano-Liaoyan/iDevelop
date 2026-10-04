using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Core.Tests;

public class ExecutionChoicesTests
{
    [Fact]
    public void Choosing_a_client_picks_its_first_usable_model_and_that_models_default_level()
    {
        var pi = new ClientStatus.Ready(
            new ResolvedCommand("pi", IsBatchShim: false),
            [
                new ModelOption("openai-codex/gpt-5.5", "GPT-5.5 (openai-codex)", ["low", "medium"])
                {
                    DefaultReasoning = "medium",
                    Problem = "Pi's sign-in for openai-codex is invalid. Sign in to openai-codex in Pi again.",
                },
                new ModelOption("deepseek/deepseek-v4-pro", "DeepSeek V4 Pro (deepseek)", ["high", "max"]) { DefaultReasoning = "high" },
            ]);

        Assert.Equal(
            new ExecutionSettings(ClientId.Pi) { Model = "deepseek/deepseek-v4-pro", Reasoning = "high" },
            ExecutionChoices.ForClient(ClientId.Pi, pi));
        Assert.Equal(new ExecutionSettings(ClientId.Codex), ExecutionChoices.ForClient(ClientId.Codex, new ClientStatus.Checking()));
    }

    [Fact]
    public void Choosing_a_model_keeps_an_offered_level_and_otherwise_takes_the_models_default()
    {
        var current = new ExecutionSettings(ClientId.Antigravity) { Model = "gemini-3.8-flash", Reasoning = "low" };

        Assert.Equal(
            current with { Model = "gemini-3.1-pro" },
            ExecutionChoices.ForModel(current, new ModelOption("gemini-3.1-pro", "Gemini 3.1 Pro", ["low", "high"]) { DefaultReasoning = "high" }));
        Assert.Equal(
            current with { Model = "gpt-oss-120b", Reasoning = "medium" },
            ExecutionChoices.ForModel(current, new ModelOption("gpt-oss-120b", "GPT-OSS 120B", ["medium"]) { DefaultReasoning = "medium" }));
        Assert.Equal(
            new ExecutionSettings(ClientId.Antigravity) { Model = "claude-opus-4-6-thinking" },
            ExecutionChoices.ForModel(current, new ModelOption("claude-opus-4-6-thinking", "Claude Opus 4.6 (Thinking)", [])));
    }

    [Fact]
    public void The_model_picker_shows_a_stored_model_this_machine_does_not_offer()
    {
        var codex = new ClientStatus.Ready(new ResolvedCommand("codex", IsBatchShim: false), [new ModelOption("gpt-5.5", "GPT-5.5", ["low"])]);
        var stored = new ExecutionSettings(ClientId.Codex) { Model = "gpt-7" };

        Assert.Equal(
            new (string, bool)[] { ("gpt-5.5", true), ("gpt-7", false) },
            ExecutionChoices.Models(stored, codex).Select(choice => (choice.Model.Id, choice.Offered)));
        Assert.Equal(
            new (string, bool)[] { ("gpt-7", false) },
            ExecutionChoices.Models(stored, new ClientStatus.Missing("No codex command was found on PATH.")).Select(choice => (choice.Model.Id, choice.Offered)));
    }
}
