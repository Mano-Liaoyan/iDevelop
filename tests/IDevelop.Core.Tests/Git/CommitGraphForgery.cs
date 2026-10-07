using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IDevelop.Core.Tests.Git;

internal static class CommitGraphForgery
{
    public static void Forge(string path, string commit, string? parent = null, string? tree = null)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var data = File.ReadAllBytes(path);
        Assert.Equal("CGPH"u8.ToArray(), data[..4]);
        Assert.Equal(1, data[5]);
        var offsets = new Dictionary<string, int>();
        for (var i = 0; i < data[6]; i++)
            offsets.Add(Encoding.ASCII.GetString(data, 8 + 12 * i, 4),
                checked((int)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(12 + 12 * i, 8))));
        var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offsets["OIDF"] + 255 * 4, 4)));
        var oids = Enumerable.Range(0, count).Select(i => Convert.ToHexStringLower(data, offsets["OIDL"] + 20 * i, 20)).ToList();
        Assert.Contains(commit, oids);
        var record = offsets["CDAT"] + oids.IndexOf(commit) * 36;
        if (parent is not null)
        {
            Assert.Contains(parent, oids);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(record + 20, 4), (uint)oids.IndexOf(parent));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(record + 24, 4), 0x70000000);
        }
        if (tree is not null) Convert.FromHexString(tree).CopyTo(data.AsSpan(record, 20));
        SHA1.HashData(data.AsSpan(0, data.Length - 20)).CopyTo(data.AsSpan(data.Length - 20));
        File.WriteAllBytes(path, data);
    }
}
