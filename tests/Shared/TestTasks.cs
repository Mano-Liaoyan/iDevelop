using IDevelop.Workflows;

namespace IDevelop.TestSupport;

/// <summary>The sample project's task ids. They sort Design, Build, Review, which the sidebar order tests rely on.</summary>
internal static class TestTasks
{
    public static readonly TaskId Design = new(Guid.Parse("019a9d2e-5a02-7c41-9d3e-2b8f6a1c0e11"));
    public static readonly TaskId Build = new(Guid.Parse("019a9d2e-5b77-7e12-a4f0-7c3d9e2b5f22"));
    public static readonly TaskId Review = new(Guid.Parse("019a9d2e-5c9a-7f05-b1c8-4e6a0d3f8c33"));
}
