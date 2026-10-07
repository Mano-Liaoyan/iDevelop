namespace IDevelop.Desktop;

/// <summary>A remembered project folder that the start could not open, such as one on a drive that is not mounted.</summary>
public sealed class UnopenedProject
{
    internal UnopenedProject(string folder, string reason)
    {
        Folder = folder;
        Name = ProjectFolders.Name(folder);
        Reason = reason;
        Summary = Directory.Exists(folder) ? "Couldn't open" : "Folder not found";
    }

    public string Folder { get; }

    public string Name { get; }

    /// <summary>The whole message, which names the folder.</summary>
    public string Reason { get; }

    /// <summary>The few words the sidebar row has room for.</summary>
    public string Summary { get; }
}
