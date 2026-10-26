using IDevelop.TestSupport;

namespace IDevelop.Desktop.Tests;

/// <summary>
/// Desktop test classes that start processes run one at a time. Executables are written through <see cref="Executable"/>,
/// so this collection is not what prevents "Text file busy".
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessCollection
{
    public const string Name = "Processes";
}
