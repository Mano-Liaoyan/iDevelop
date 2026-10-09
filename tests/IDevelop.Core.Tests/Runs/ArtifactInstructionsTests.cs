using IDevelop.Execution;

namespace IDevelop.Core.Tests.Runs;

/// <summary>
/// The paragraph that tells an editing task where to declare its artifacts: the prompt carries it word for word as
/// before, so recorded prompts still match, and the conversation can set it apart from the task's own text.
/// </summary>
public sealed class ArtifactInstructionsTests
{
    private static readonly AttemptId Attempt = new(Guid.Parse("00000000-0000-0000-0000-000000000102"));

    // As the recorded journals under tests/Shared/Fixtures hold it.
    private const string Recorded =
        "Declare artifacts in .idp/outbox/00000000-0000-0000-0000-000000000102/manifest.json using " +
        "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}. Artifact paths are relative to that folder.";

    [Fact]
    public void The_paragraph_reads_as_the_recorded_prompts_do_and_names_the_artifacts_an_earlier_result_keeps()
    {
        Assert.Equal(Recorded, ArtifactInstructions.For(Attempt, []));
        var kept = new ArtifactRecord("payload", "results/payload", Revision.Hash("C"), 1);
        Assert.Equal(
            Recorded + " Your earlier result's artifacts stay with your new result unless you declare one with the same name: payload.",
            ArtifactInstructions.For(Attempt, [kept]));
    }

    [Fact]
    public void A_prompt_splits_into_its_own_text_and_the_paragraph_and_a_prompt_without_one_stays_whole()
    {
        Assert.Equal(("# Write docs\n\nDocument the export.\n\nReport: the design is ready.", Recorded),
            ArtifactInstructions.Split("# Write docs\n\nDocument the export.\n\nReport: the design is ready.\n\n" + Recorded));
        Assert.Equal(("Inspect", Recorded), ArtifactInstructions.Split("Inspect\n\n" + Recorded));
        Assert.Equal(("Use the fixture.", null), ArtifactInstructions.Split("Use the fixture."));
        // Text that only mentions the outbox stays the task's own.
        Assert.Equal(("Read .idp/outbox/ first. Declare artifacts in the manifest.", null),
            ArtifactInstructions.Split("Read .idp/outbox/ first. Declare artifacts in the manifest."));
    }
}
