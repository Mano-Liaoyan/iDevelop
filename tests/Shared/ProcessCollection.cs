namespace IDevelop.TestSupport;

/// <summary>
/// Test classes that write shims and start processes run one at a time. On Linux, a process forked by another test while
/// a shim is still open for writing makes running that shim fail with "Text file busy".
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessCollection
{
    public const string Name = "Processes";
}
