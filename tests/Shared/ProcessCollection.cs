namespace IDevelop.TestSupport;

/// <summary>
/// Desktop test classes that start processes run one at a time. Executables are written through <see cref="Executable"/>,
/// so this collection is not what prevents "Text file busy".
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessCollection
{
    public const string Name = "Processes";
}

/// <summary>Classes that change this process's environment run alone, after the parallel classes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment";
}
