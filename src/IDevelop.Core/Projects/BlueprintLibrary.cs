using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IDevelop.Workflows;

namespace IDevelop.Projects;

/// <summary>Where a library lives: in the project, which Git tracks, or in the user's own folder.</summary>
public enum LibraryKind { Project, Personal }

/// <summary>The blueprints a library holds, by name, and a message for each file it could not read.</summary>
public sealed record LibraryContents(ImmutableArray<Blueprint> Blueprints, ImmutableArray<string> Problems);

/// <summary>
/// A folder of blueprint files, one file per blueprint, named by its id. A file holds the blueprint's latest version.
/// The project library is <c>.idp/blueprints/</c>, and the personal library sits beside the user's settings.
/// </summary>
public sealed partial class BlueprintLibrary
{
    public const string Format = "idevelop.blueprint/1";

    private readonly string? _projectFolder;

    private BlueprintLibrary(LibraryKind kind, string folder, string? projectFolder)
    {
        Kind = kind;
        Folder = folder;
        _projectFolder = projectFolder;
    }

    public LibraryKind Kind { get; }

    public string Folder { get; }

    public static BlueprintLibrary Project(string projectFolder) =>
        new(LibraryKind.Project, DataFolder.Blueprints(projectFolder), projectFolder);

    public static BlueprintLibrary Personal(string folder) => new(LibraryKind.Personal, folder, null);

    /// <summary>A new id made from the name, at version 1. The random end keeps two libraries from sharing an id.</summary>
    public static BlueprintKey NewKey(string name)
    {
        var slug = NotIdText().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return new BlueprintKey($"{(slug.Length == 0 ? "blueprint" : slug)}-{Guid.NewGuid().ToString("N")[..8]}", 1);
    }

    /// <summary>A missing folder is an empty library. A folder or file that does not read is a problem, and a bad file leaves the others listed.</summary>
    public LibraryContents Read()
    {
        if (!Directory.Exists(Folder))
        {
            return new LibraryContents([], []);
        }

        string[] paths;
        try
        {
            paths = [.. Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LibraryContents([], [$"Couldn't read {Folder}: {e.Message}"]);
        }

        var blueprints = ImmutableArray.CreateBuilder<Blueprint>();
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var path in paths)
        {
            try
            {
                blueprints.Add(ReadFile(path));
            }
            catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException)
            {
                problems.Add(e is ProjectException ? e.Message : $"Couldn't read {path}: {e.Message}");
            }
        }

        return new LibraryContents(
            [.. blueprints.OrderBy(blueprint => blueprint.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(blueprint => blueprint.Key)],
            problems.ToImmutable());
    }

    /// <summary>
    /// Writes version 1 of a new blueprint, or the next version of one this library holds. A next version is written
    /// only while the file still holds the version before it, so of two editors that opened one version, the second
    /// save is refused.
    /// </summary>
    /// <exception cref="ProjectException">The blueprint is built in, or the file no longer holds the version before it.</exception>
    public void Save(Blueprint blueprint)
    {
        if (blueprint.IsBuiltIn)
        {
            throw new ProjectException($"{blueprint.Name} is built in, and a built-in blueprint is read-only. Derive a blueprint to change it.");
        }

        var path = PathOf(blueprint.Key.Id);
        var opened = blueprint.Key.Version - 1;
        if (opened == 0)
        {
            if (File.Exists(path))
            {
                throw new ProjectException($"Not saved. {path} already holds a blueprint.");
            }
        }
        else
        {
            var current = File.Exists(path)
                ? ReadFile(path)
                : throw new ProjectException($"Not saved. {blueprint.Name} is no longer in the {LibraryName} library at {path}.");
            if (current.Key.Version != opened)
            {
                throw new ProjectException(
                    $"Not saved. {path} changed since you opened version {opened} of {blueprint.Name}, and it holds version {current.Key.Version} now.");
            }
        }

        Directory.CreateDirectory(Folder);
        if (_projectFolder is { } projectFolder)
        {
            DataFolder.EnsureGitIgnore(projectFolder);
        }

        AtomicFile.Replace(path, Serialize(blueprint));
    }

    public static byte[] Serialize(Blueprint blueprint)
    {
        var dto = BlueprintJson.ToDto(blueprint);
        var file = new FileDto
        {
            Format = Format,
            Id = dto.Id,
            Version = dto.Version,
            Name = dto.Name,
            Description = dto.Description,
            DerivedFrom = dto.DerivedFrom,
            Icon = dto.Icon,
            Color = dto.Color,
            Work = dto.Work,
            Fields = dto.Fields,
            Defaults = dto.Defaults,
        };
        return [.. JsonSerializer.SerializeToUtf8Bytes(file, WorkflowFile.Options), (byte)'\n'];
    }

    /// <exception cref="ProjectException">The file is not a blueprint this version reads.</exception>
    public static Blueprint Parse(ReadOnlySpan<byte> utf8, string path)
    {
        if (utf8.StartsWith("﻿"u8))
        {
            utf8 = utf8["﻿"u8.Length..];
        }

        FileDto file;
        try
        {
            var format = JsonSerializer.Deserialize<WorkflowFile.HeaderDto>(utf8, WorkflowFile.HeaderOptions)?.Format;
            file = format == Format
                ? JsonSerializer.Deserialize<FileDto>(utf8, WorkflowFile.Options)!
                : throw new ProjectException($"{path} has format \"{format}\". This version of iDevelop reads {Format}.");
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{path} is not a valid blueprint file. {e.Message}", e);
        }

        return BlueprintJson.FromDto(file, path);
    }

    private string LibraryName => Kind switch
    {
        LibraryKind.Project => "project",
        LibraryKind.Personal => "personal",
    };

    private string PathOf(string id) => Path.Combine(Folder, $"{id}.json");

    private Blueprint ReadFile(string path)
    {
        var blueprint = Parse(File.ReadAllBytes(path), path);
        if (blueprint.IsBuiltIn)
        {
            throw new ProjectException($"{path} holds {blueprint.Key}, a built-in id, which only iDevelop ships.");
        }

        return Path.GetFileName(path) == $"{blueprint.Key.Id}.json"
            ? blueprint
            : throw new ProjectException($"{path} holds blueprint {blueprint.Key.Id}, so its name must be {blueprint.Key.Id}.json.");
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotIdText();

    private sealed class FileDto : BlueprintDto
    {
        [JsonPropertyOrder(-1)]
        public required string Format { get; init; }
    }
}
