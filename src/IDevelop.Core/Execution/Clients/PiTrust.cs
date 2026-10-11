using System.Text.Json;

namespace IDevelop.Execution;

/// <summary>
/// The person's own folders that a client reads, as the client finds them. Only the app's start reads them from the
/// environment, so a test never reads the real home folder.
/// </summary>
/// <param name="PiAgent">Pi's agent folder: <c>PI_CODING_AGENT_DIR</c>, or <c>~/.pi/agent</c>.</param>
public sealed record UserFolders(string Home, string PiAgent)
{
    public static UserFolders FromEnvironment() =>
        From(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"));

    /// <summary>Pi's agent folder as Pi 1.1.0's <c>getAgentDir</c> finds it, with a leading <c>~</c> for the home folder.</summary>
    internal static UserFolders From(string home, string? piAgentVariable) => new(home, piAgentVariable switch
    {
        null or "" => Path.Combine(home, ".pi", "agent"),
        "~" => home,
        var folder when folder.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && folder.StartsWith(@"~\", StringComparison.Ordinal) =>
            Path.Combine(home, folder[2..]),
        var folder => folder,
    });
}

/// <summary>
/// Whether Pi leaves out a project's own skills because the person has not trusted the folder in Pi. Pi 1.1.0 loads
/// <c>.pi/skills</c> and each <c>.agents/skills</c> from the folder up only in a trusted folder, and print and RPC mode never
/// ask, so its skill listing leaves them out (probe, 2026-10-11). This reads Pi's own decision without a model turn, as
/// <c>dist/core/project-trust.js</c> and <c>trust-manager.js</c> make it: the nearest folder that <c>trust.json</c> names,
/// then the <c>defaultProjectTrust</c> setting. It never writes either file. An extension can also decide, which only Pi
/// itself can tell.
/// </summary>
internal static class PiTrust
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool HidesProjectSkills(string folder, UserFolders user)
    {
        var project = RealPath(folder);
        return HasProjectSkills(project, RealPath(user.Home)) && !Trusted(project, user.PiAgent);
    }

    /// <summary>The folder with each link along it followed, as Node's <c>realpathSync</c>, whose paths <c>trust.json</c> keeps.</summary>
    internal static string RealPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (new DirectoryInfo(current) is { Exists: true, LinkTarget: not null } link && link.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Path.TrimEndingDirectorySeparator(target.FullName);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return current;
    }

    private static bool HasProjectSkills(string project, string home)
    {
        if (Directory.Exists(Path.Combine(project, ".pi", "skills")))
        {
            return true;
        }

        var personal = Path.Combine(home, ".agents", "skills");
        for (var folder = project; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var skills = Path.Combine(folder, ".agents", "skills");
            if (!string.Equals(skills, personal, PathComparison) && Directory.Exists(skills))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Trusted(string project, string piAgent)
    {
        if (Read(Path.Combine(piAgent, "trust.json")) is not { } trust)
        {
            // Pi stops on a trust store it cannot read, so it trusts nothing.
            return false;
        }

        for (var folder = project; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var decision = trust.EnumerateObject().LastOrDefault(entry => string.Equals(entry.Name, folder, PathComparison)).Value;
            if (decision.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return decision.ValueKind == JsonValueKind.True;
            }
        }

        return Read(Path.Combine(piAgent, "settings.json")) is { } settings
            && settings.TryGetProperty("defaultProjectTrust", out var trustByDefault) && trustByDefault.ValueKind == JsonValueKind.String
            && trustByDefault.GetString() == "always";
    }

    /// <summary>A JSON object file, an empty object when it is missing, or null when it cannot be read as one.</summary>
    private static JsonElement? Read(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return JsonDocument.Parse("{}").RootElement.Clone();
            }

            using var json = JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿'));
            return json.RootElement.ValueKind == JsonValueKind.Object ? json.RootElement.Clone() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
