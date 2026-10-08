using System.Diagnostics;
using System.Text.Json;
using IDevelop.Execution;
using IDevelop.TestSupport;
using static IDevelop.Core.Tests.Runs.RunFixtures;

namespace IDevelop.Core.Tests.Materialization;

public sealed class FileIdentityTests
{
    [LinuxOrWindowsFact]
    public void FileIdentity_reads_platform_identity() => VerifyPlatformIdentities();

    [LinuxFact]
    public void Linux_removal_checks_identity_and_bytes() => VerifyRemoval();

    [WindowsFact]
    public void Windows_removal_checks_identity_and_bytes()
    {
        VerifyPlatformIdentities();
        VerifyRemoval();
    }

    [OtherPlatformFact]
    public void Unsupported_platforms_have_no_identity_and_cannot_remove_locks()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Create("files"), "index.lock");
        File.WriteAllBytes(path, [108, 111, 99, 107]);
        using var handle = File.OpenHandle(path);
        Assert.Null(FileIdentities.Of(handle));
        Assert.Equal(LockRemoval.Unavailable, FileIdentities.Remove(path, [108, 111, 99, 107], new(1, 2, 3)));
        Assert.Equal(new byte[] { 108, 111, 99, 107 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_128_bit_file_identity_round_trips_with_the_run_journal_options_and_canonical_encoding()
    {
        var identity = new FileIdentity(2, ((UInt128)1 << 64) + 1, 3);
        Assert.Equal("{\"changeTicks\":3,\"file\":18446744073709551617,\"volume\":2}", RunJournal.Canonical(identity));
        var evidence = new LockEvidence(new("lock", Revision.Hash([108, 111, 99, 107]), 4), identity);
        var canonical = RunJournal.Canonical(evidence);
        Assert.Equal(evidence, JsonSerializer.Deserialize<LockEvidence>(canonical, RunJournal.Options));
        var entry = new RunEntry(3, 1, new(Id(901)), Revision.Hash([1]), At,
            new RunEvent.CaptureDisposed(new(Id(902)), new(new(Id(903)), 1),
                new CaptureDisposition.Failed(MaterializationProblem.InputUnavailable, canonical, [evidence.Bytes])));
        var encoded = RunJournal.Encode(entry);
        var decoded = RunJournal.Decode(encoded);
        Assert.Null(decoded.Rejection);
        var retained = Assert.IsType<CaptureDisposition.Failed>(Assert.IsType<RunEvent.CaptureDisposed>(Assert.Single(decoded.Entries).Event).Disposition);
        var restored = JsonSerializer.Deserialize<LockEvidence>(retained.Detail, RunJournal.Options);
        Assert.Equal(evidence, restored);
        Assert.Equal(canonical, RunJournal.Canonical(restored));
        Assert.Equal(encoded, RunJournal.Encode(decoded.Entries[0]));
    }

    private static void VerifyPlatformIdentities()
    {
        using var temp = new TempFolder();
        var folder = temp.Create("files");
        var path = Path.Combine(folder, "index.lock");
        var link = Path.Combine(folder, "hardlink");
        var copy = Path.Combine(folder, "copy");
        File.WriteAllBytes(path, [108, 111, 99, 107]);
        using (var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "fsutil" : "ln", UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true,
        } })
        {
            if (OperatingSystem.IsWindows())
            {
                process.StartInfo.ArgumentList.Add("hardlink");
                process.StartInfo.ArgumentList.Add("create");
                process.StartInfo.ArgumentList.Add(link);
                process.StartInfo.ArgumentList.Add(path);
            }
            else
            {
                process.StartInfo.ArgumentList.Add(path);
                process.StartInfo.ArgumentList.Add(link);
            }
            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        File.Copy(path, copy);
        var original = Read(path);
        Assert.NotNull(original.Identity);
        Assert.Equal(new byte[] { 108, 111, 99, 107 }, original.Bytes);
        Assert.Equal(original.Identity, Read(link).Identity);
        Assert.NotEqual(original.Identity, Read(copy).Identity);
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.Equal(original.Identity, FileIdentities.Of(handle));
        File.WriteAllBytes(path + ".new", [108, 111, 99, 107]);
        File.Move(path + ".new", path, overwrite: true);
        var replacement = Read(path);
        Assert.Equal(original.Bytes, replacement.Bytes);
        Assert.NotNull(replacement.Identity);
        Assert.NotEqual(original.Identity, replacement.Identity);
        var volume = FileIdentities.VolumeOf(temp.Create("first"));
        Assert.NotNull(volume);
        Assert.Equal(volume, FileIdentities.VolumeOf(temp.Create("second")));
        Assert.Equal(volume, replacement.Identity!.Value.Volume);
    }

    private static void VerifyRemoval()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Create("files"), "index.lock");
        File.WriteAllBytes(path, [108, 111, 99, 107]);
        var original = Read(path);
        Assert.NotNull(original.Identity);
        File.WriteAllBytes(path + ".new", [108, 111, 99, 107]);
        File.Move(path + ".new", path, overwrite: true);
        Assert.Equal(LockRemoval.Changed, FileIdentities.Remove(path, original.Bytes, original.Identity.Value));
        Assert.Equal(new byte[] { 108, 111, 99, 107 }, File.ReadAllBytes(path));
        var replacement = Read(path);
        Assert.NotNull(replacement.Identity);
        Assert.NotEqual(original.Identity, replacement.Identity);
        Assert.Equal(LockRemoval.Changed, FileIdentities.Remove(path, [120], replacement.Identity.Value));
        Assert.Equal(new byte[] { 108, 111, 99, 107 }, File.ReadAllBytes(path));
        Assert.Equal(LockRemoval.Removed, FileIdentities.Remove(path, replacement.Bytes, replacement.Identity.Value));
        Assert.False(File.Exists(path));
        Assert.Equal(LockRemoval.Absent, FileIdentities.Remove(path, replacement.Bytes, replacement.Identity.Value));
        Assert.Null(FileIdentities.ReadFile(path));
    }

    private static (byte[] Bytes, FileIdentity? Identity) Read(string path)
    {
        var read = FileIdentities.ReadFile(path);
        Assert.NotNull(read);
        return read.Value;
    }
}
