using IDevelop.Core.Tests.Git;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class PublicationOwnershipTests
{
    [Theory]
    [InlineData("detached")]
    [InlineData("rewritten")]
    [InlineData("lock")]
    public async Task Checkout_ownership_blocks_detachment_history_rewrites_and_foreign_registration_locks(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f);
        var operation = f.Op();
        if (mode == "detached") Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--detach", "HEAD").ExitCode);
        else if (mode == "rewritten") f.Git.Git("update-ref", ready.Execution.Location.Owner.Branch, "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        else
        {
            f.Git.Git("worktree", "unlock", ready.Checkout);
            f.Git.Git("worktree", "lock", "--reason", "foreign owner", ready.Checkout);
        }
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Empty(f.Read().Results);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(ready.Checkout, "new.txt")));
    }

    [Theory]
    [InlineData("stash")]
    [InlineData("foreign-ref")]
    [InlineData("delete")]
    [InlineData("final-stash")]
    public async Task Unexplained_shared_changes_preserve_before_and_after_evidence(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        f.Git.Git("update-ref", "refs/stash", "7c64b20d5be53b5c1a291863ef191aa28f6f4d51");
        var ready = await PublicationTests.ChangedWriter(f);
        if (mode == "stash") f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        else if (mode == "foreign-ref") f.Git.Git("update-ref", "refs/idp/93f23689/foreign", f.A.Hex);
        else if (mode == "delete") f.Git.Git("update-ref", "-d", "refs/idp/93f23689/base");
        var operation = f.Op();
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (mode == "final-stash" && step == "git.result.after") f.Git.Git("update-ref", "refs/stash", f.A.Hex);
        }).Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal(2, blocked.Block.Evidence.Length);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var before = System.Text.Encoding.UTF8.GetString(RunStorage.Read(storage.Folder, blocked.Block.Evidence[0].RelativePath,
            blocked.Block.Evidence[0].Content, blocked.Block.Evidence[0].ByteLength));
        Assert.Contains("refs/stash", before);
        Assert.Contains("7c64b20d5be53b5c1a291863ef191aa28f6f4d51", before);
        Assert.Empty(f.Read().Results);
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    [Fact]
    public async Task Overlapping_sibling_fast_forward_and_observed_publication_are_explained_by_the_run_journal()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T), Writer(C)));
        var writer = await PublicationTests.ChangedWriter(f);
        var sibling = Assert.IsType<Preparation.Ready>(await f.Prepare(C));
        f.Git.Write("c.txt", "C\n", sibling.Checkout);
        Assert.Equal(0, f.Git.Run(sibling.Checkout, "add", "c.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(sibling.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "c").ExitCode);
        var first = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), writer.Execution.Launch.Attempt));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(first.Result.Code).Code.Commit.Hex);
        f.Close(sibling, "C ready.\n");
        var second = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, f.Op(), sibling.Execution.Launch.Attempt));
        Assert.Equal("e954b83b974db2a85981aa86b3a2eabee42d7803", Assert.IsType<CodeOutput.Produced>(second.Result.Code).Code.Commit.Hex);
        Assert.Equal(2, f.Read().Results.Count);
        Assert.Equal("B\n", File.ReadAllText(Path.Combine(writer.Checkout, "b.txt")));
        Assert.Equal("C\n", File.ReadAllText(Path.Combine(sibling.Checkout, "c.txt")));
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("head")]
    [InlineData("result")]
    public async Task Late_ref_and_head_changes_block_as_uncertain_ownership_even_when_status_also_changes(string mode)
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f);
        var reference = RunLayout.ResultRef(f.Read().RunKey!, f.Read().TaskKeys[T], ready.Execution.Launch.Attempt);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step != "git.result.after") return;
            if (mode == "head") Assert.Equal(0, f.Git.Run(ready.Checkout, "checkout", "--detach", "HEAD").ExitCode);
            else f.Git.Git("update-ref", mode == "branch" ? ready.Execution.Location.Owner.Branch : reference, f.A.Hex);
        }).Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt));
        Assert.Equal("UncertainOwnership", blocked.Block.Problem.ToString());
        Assert.Equal("A captured\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task Unmerged_stages_are_preserved_and_block_publication()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        f.Git.Git("checkout", "-q", "-b", "other");
        f.Git.Write("a.txt", "right\n");
        f.Git.Git("add", "a.txt");
        f.Git.Git("-c", "commit.gpgSign=false", "commit", "-q", "-m", "right");
        f.Git.Write("a.txt", "left\n", ready.Checkout);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "add", "a.txt").ExitCode);
        Assert.Equal(0, f.Git.Run(ready.Checkout, "-c", "commit.gpgSign=false", "commit", "-q", "-m", "left").ExitCode);
        Assert.Equal(1, f.Git.Run(ready.Checkout, "merge", "--no-edit", "other").ExitCode);
        f.Close(ready);
        Assert.Equal("DirtyWorktree", Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt))
            .Block.Problem.ToString());
        Assert.Equal("UU a.txt\0", System.Text.Encoding.UTF8.GetString(GitFixture.Read(f.Git.Open().Status(ready.Checkout))));
        Assert.Empty(f.Read().Results);
    }
}
