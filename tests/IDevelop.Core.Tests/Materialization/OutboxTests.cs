using System.Runtime.InteropServices;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;
using static IDevelop.Core.Tests.Materialization.PreparationFixture;

namespace IDevelop.Core.Tests.Materialization;

[Collection(ProcessCollection.Name)]
public sealed class OutboxTests
{
    [Fact]
    public async Task Full_report_and_binary_artifact_reach_the_successor_without_a_result_block()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var result = await f.Publish(T, f.A, artifact: true);
        Assert.Equal("B ready.\n", result.Report);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("results/0a07c9c1-332b-8f5f-a0fb-3d93fb49f99b/artifacts/payload", artifact.StoredPath);
        Assert.Equal("134f4812acb8aa0b274fd71f834f9d36a21ab174e3fbab2f7956eac4b0a469c7", artifact.Content.Sha256);
        Assert.Equal(3, artifact.ByteLength);
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(U));
        const string folder = ".idp/inputs/00000000-0000-0000-0000-000000000103/0a07c9c1-332b-8f5f-a0fb-3d93fb49f99b/";
        Assert.Equal("B ready.\n", File.ReadAllText(Path.Combine(ready.Checkout, folder + "report.md")));
        Assert.Equal(new byte[] { 67, 0, 127 }, File.ReadAllBytes(Path.Combine(ready.Checkout, folder + "artifacts/payload")));
        var writer = Path.Combine(f.Git.Folder, ".worktrees", f.Read().RunKey!, f.Read().TaskKeys[T]);
        File.WriteAllBytes(Path.Combine(writer, RunLayout.Outbox(A1), "payload.bin"), [1]);
        Assert.Equal(new byte[] { 67, 0, 127 }, new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(result.Id, artifact));
    }

    [Theory]
    [InlineData("payload", "../x")]
    [InlineData("payload", "missing.bin")]
    [InlineData("payload", "/x")]
    [InlineData("payload", "C:/x")]
    [InlineData("payload", "via\\x")]
    [InlineData("payload", "")]
    [InlineData("payload", ".")]
    [InlineData("payload", "./payload.bin")]
    [InlineData("payload", "via//x")]
    [InlineData("payload", "directory")]
    [InlineData("CON.txt", "payload.bin")]
    [InlineData("lpt9", "payload.bin")]
    [InlineData("com1.bin", "payload.bin")]
    [InlineData("NUL", "payload.bin")]
    [InlineData("payload.", "payload.bin")]
    [InlineData("payload ", "payload.bin")]
    [InlineData("folder/payload", "payload.bin")]
    [InlineData("", "payload.bin")]
    public async Task Invalid_declared_paths_and_names_block_with_attempt_and_manifest_evidence(string name, string path)
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
        Directory.CreateDirectory(Path.Combine(outbox, "directory"));
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), JsonSerializer.Serialize(new { schema = 1, artifacts = new[] { new { name, path } } }));
        f.Close(ready);
        CheckRejected(f, ready);
    }

    [Theory]
    [InlineData("{\"schema\":2,\"artifacts\":[]}")]
    [InlineData("{\"schema\":1,\"artifacts\":[],\"extra\":0}")]
    [InlineData("{\"schema\":1,\"schema\":1,\"artifacts\":[]}")]
    [InlineData("{\"schema\":\"1\",\"artifacts\":[]}")]
    [InlineData("{\"schema\":1,\"artifacts\":{}}")]
    [InlineData("{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\",\"extra\":0}]}")]
    [InlineData("{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"name\":\"payload\",\"path\":\"payload.bin\"}]}")]
    [InlineData("{\"schema\":1,\"artifacts\":[{\"name\":0,\"path\":\"payload.bin\"}]}")]
    [InlineData("{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":null}]}")]
    [InlineData("{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"},{\"name\":\"PAYLOAD\",\"path\":\"payload.bin\"}]}")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public async Task The_manifest_is_a_strict_schema_and_not_a_result_message(string manifest)
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        File.WriteAllBytes(Path.Combine(outbox, "payload.bin"), [67, 0, 127]);
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), manifest);
        f.Close(ready);
        CheckRejected(f, ready);
    }

    [UnixFact]
    public async Task Directory_symlinks_and_file_symlinks_cannot_escape_the_outbox()
    {
        foreach (var directory in new[] { true, false })
        {
            using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
            var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
            var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
            var target = Directory.CreateDirectory(Path.Combine(f.Git.Folder, "external")).FullName;
            File.WriteAllBytes(Path.Combine(target, "payload.bin"), [67, 0, 127]);
            var path = directory ? "via/payload.bin" : "payload.bin";
            if (directory) Directory.CreateSymbolicLink(Path.Combine(outbox, "via"), target);
            else File.CreateSymbolicLink(Path.Combine(outbox, "payload.bin"), Path.Combine(target, "payload.bin"));
            File.WriteAllText(Path.Combine(outbox, "manifest.json"), JsonSerializer.Serialize(new { schema = 1, artifacts = new[] { new { name = "payload", path } } }));
            f.Close(ready);
            CheckRejected(f, ready);
            Assert.Equal(new byte[] { 67, 0, 127 }, File.ReadAllBytes(Path.Combine(target, "payload.bin")));
        }
    }

    [WindowsFact]
    public async Task A_directory_junction_cannot_escape_the_outbox()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        var target = Directory.CreateDirectory(Path.Combine(f.Git.Folder, "external")).FullName;
        File.WriteAllBytes(Path.Combine(target, "payload.bin"), [67, 0, 127]);
        Junction.Create(Path.Combine(outbox, "via"), target);
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"via/payload.bin\"}]}");
        f.Close(ready);
        CheckRejected(f, ready);
        Assert.Equal(new byte[] { 67, 0, 127 }, File.ReadAllBytes(Path.Combine(target, "payload.bin")));
    }

    [UnixFact]
    public async Task A_fifo_is_rejected_before_opening_it()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var ready = Assert.IsType<Preparation.Ready>(await f.Prepare(T));
        var outbox = Path.Combine(ready.Checkout, ready.Execution.OutboxPath);
        Assert.Equal(0, Mkfifo(Path.Combine(outbox, "payload.bin"), 0x180));
        File.WriteAllText(Path.Combine(outbox, "manifest.json"), "{\"schema\":1,\"artifacts\":[{\"name\":\"payload\",\"path\":\"payload.bin\"}]}");
        f.Close(ready);
        CheckRejected(f, ready);
    }

    [Fact]
    public async Task Missing_manifest_ignores_markdown_urls_and_empty_outboxes_publish_no_artifacts()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var result = await f.Publish(T, f.A, "See [payload](https://example.invalid/file).\n");
        Assert.Equal("See [payload](https://example.invalid/file).\n", result.Report);
        Assert.Empty(result.Artifacts);
        Assert.Equal("d4d26ecdf72779dbc9c5c983025fb51546c8f9ea", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex);
    }

    [Fact]
    public async Task Different_existing_artifact_bytes_block_without_overwriting_them()
    {
        using var f = new PreparationFixture(Connect(FixtureWorkflow(Writer(T), Writer(U)), T, U));
        var ready = await PublicationTests.ChangedWriter(f, artifact: true);
        var operation = new OperationId(Id(2000));
        var path = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "results/f7d21fe0-9370-801e-a122-38820dfbc203/artifacts/payload");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Contains("manifest.json", blocked.Block.Detail);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Empty(f.Read().Results);
    }

    [Fact]
    public async Task Resume_keeps_the_frozen_artifact_even_when_the_ignored_outbox_changes()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f, artifact: true);
        var operation = new OperationId(Id(2000));
        Assert.Throws<PublicationTests.Crash>(() => f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") throw new PublicationTests.Crash();
        }).Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        File.WriteAllBytes(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "payload.bin"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(ready.Checkout, ready.Execution.OutboxPath, "manifest.json"), "{broken");
        var result = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt)).Result;
        Assert.Equal("f7d21fe0-9370-801e-a122-38820dfbc203", result.Id.Value.ToString("D"));
        Assert.Equal(new byte[] { 67, 0, 127 }, new RunStorage(f.Git.Folder, W, f.RunId).ReadArtifact(result.Id, result.Artifacts.Single()));
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex);
    }

    [Fact]
    public async Task Stored_bytes_changed_after_the_plan_block_acceptance_and_resume_after_they_are_restored()
    {
        using var f = new PreparationFixture(FixtureWorkflow(Writer(T)));
        var ready = await PublicationTests.ChangedWriter(f, artifact: true);
        var operation = new OperationId(Id(2000));
        var path = Path.Combine(new RunStorage(f.Git.Folder, W, f.RunId).Folder, "results/f7d21fe0-9370-801e-a122-38820dfbc203/artifacts/payload");
        var blocked = Assert.IsType<Publication.Blocked>(f.Materializer(probe: step =>
        {
            if (step == "journal.plan.after") File.WriteAllBytes(path, [1, 2, 3]);
        }).Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt));
        Assert.Equal("InputUnavailable", blocked.Block.Problem.ToString());
        Assert.Contains("Attempt 00000000-0000-0000-0000-000000000102", blocked.Block.Detail);
        Assert.Contains("results/f7d21fe0-9370-801e-a122-38820dfbc203/artifacts/payload", blocked.Block.Detail);
        Assert.Empty(f.Read().Results);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        File.WriteAllBytes(path, [67, 0, 127]);
        var result = Assert.IsType<Publication.Accepted>(f.Materializer().Publish(W, f.RunId, operation, ready.Execution.Launch.Attempt)).Result;
        Assert.Equal("81cae59086bf9597301026b65f1bb57380b74686", Assert.IsType<CodeOutput.Produced>(result.Code).Code.Commit.Hex);
        Assert.True(Assert.Single(f.Read().Blocks).Value.Resolved);
    }

    private static void CheckRejected(PreparationFixture f, Preparation.Ready ready)
    {
        var block = Assert.IsType<Publication.Blocked>(f.Materializer().Publish(W, f.RunId, f.Op(), ready.Execution.Launch.Attempt)).Block;
        Assert.Equal("InputUnavailable", block.Problem.ToString());
        Assert.Contains("Attempt 00000000-0000-0000-0000-000000000102", block.Detail);
        Assert.Contains(".idp/outbox/00000000-0000-0000-0000-000000000102/manifest.json", block.Detail);
        Assert.Single(block.Evidence);
        var storage = new RunStorage(f.Git.Folder, W, f.RunId);
        var evidence = block.Evidence[0];
        Assert.Equal(block.Detail, System.Text.Encoding.UTF8.GetString(RunStorage.Read(storage.Folder, evidence.RelativePath, evidence.Content, evidence.ByteLength)));
        Assert.Empty(f.Read().Results);
        Assert.Equal("MissingDependencyResult", Assert.IsType<Preparation.Rejected>(f.Prepare(U).GetAwaiter().GetResult()).Reason.Problem.ToString());
        Assert.Equal(0, f.Read().Preparations.Values.Count(p => f.Read().Attempts[p.Launch.Attempt].Task == U));
        Assert.Equal("A\n", File.ReadAllText(Path.Combine(ready.Checkout, "a.txt")));
    }

    private static class Junction
    {
        public static void Create(string path, string target)
        {
            Directory.CreateDirectory(path);
            using var handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.False(handle.IsInvalid, $"CreateFile failed with {Marshal.GetLastPInvokeError()}.");
            var substitute = System.Text.Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
            var display = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            var buffer = new byte[16 + substitute.Length + display.Length + 4];
            BitConverter.GetBytes(0xa0000003u).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)display.Length).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            display.CopyTo(buffer, 18 + substitute.Length);
            Assert.True(DeviceIoControl(handle, 0x900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                $"FSCTL_SET_REPARSE_POINT failed with {Marshal.GetLastPInvokeError()}.");
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, int length,
            IntPtr output, int capacity, out int written, IntPtr overlapped);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
