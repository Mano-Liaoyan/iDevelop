using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IDevelop.Execution;
using IDevelop.Workflows;

namespace IDevelop.Projects;

/// <summary>
/// A blueprint as JSON, in the shape the workflow file embeds. Multi-line text is an array of lines, so it stays
/// readable in a text editor. A blueprint file reuses the same shape.
/// </summary>
internal static class BlueprintJson
{
    public static BlueprintDto ToDto(Blueprint blueprint) => new()
    {
        Id = blueprint.Key.Id,
        Version = blueprint.Key.Version,
        Name = blueprint.Name,
        Description = Lines(blueprint.Description),
        DerivedFrom = blueprint.DerivedFrom?.ToString(),
        Icon = blueprint.Icon is { } icon ? IconName(icon) : null,
        Color = blueprint.Color is { } color ? ColorName(color) : null,
        Work = Work(blueprint.Work),
        Fields = [.. blueprint.Fields.Select(field => new FieldDto
        {
            Key = field.Key,
            Label = field.Label,
            Shape = field.Shape == FieldShape.Line ? "line" : "text",
            Required = field.Required,
            Default = Lines(field.Default),
        })],
        Defaults = new SettingsDto { Execution = Execution(blueprint.Defaults.Execution), Conversation = ConversationName(blueprint.Defaults.Conversation) },
    };

    /// <exception cref="ProjectException">The blueprint is not valid. <paramref name="where"/> starts the message.</exception>
    public static Blueprint FromDto(BlueprintDto dto, string where)
    {
        var key = new BlueprintKey(dto.Id, dto.Version);
        var entry = $"{where}: blueprint {key}";
        var kind = Enum.GetValues<WorkKind>().Where(kind => KindName(kind) == dto.Work.Kind).Select(kind => (WorkKind?)kind).FirstOrDefault()
            ?? throw new ProjectException($"{entry} has unknown work \"{dto.Work.Kind}\".");

        var fields = ImmutableArray.CreateBuilder<FieldSpec>();
        foreach (var (index, field) in dto.Fields.Index())
        {
            if (field is null)
            {
                throw new ProjectException($"{entry}: fields[{index}] is null.");
            }

            var shape = field.Shape switch
            {
                "line" => FieldShape.Line,
                "text" => FieldShape.Text,
                _ => throw new ProjectException($"{entry}: field \"{field.Key}\" has unknown shape \"{field.Shape}\"."),
            };
            fields.Add(new FieldSpec(field.Key, field.Label, shape, field.Required, Text(field.Default, entry, $"the default of {field.Key}")));
        }

        var defaults = new NodeSettings(
            dto.Defaults.Execution is { } execution ? Execution(execution, entry) : null,
            Conversation(dto.Defaults.Conversation, entry));
        BlueprintKey? derivedFrom = dto.DerivedFrom is null
            ? null
            : BlueprintKey.Parse(dto.DerivedFrom) ?? throw new ProjectException($"{entry} is derived from \"{dto.DerivedFrom}\", which is not a blueprint id and version.");
        try
        {
            var work = Work(kind, dto.Work, entry);
            return new Blueprint(key, dto.Name, work, fields.ToImmutable(), defaults)
            {
                Description = Text(dto.Description, entry, "description"),
                DerivedFrom = derivedFrom,
                Icon = dto.Icon is null ? null : Enum.GetValues<BlueprintIcon>()
                    .Where(icon => IconName(icon) == dto.Icon).Select(icon => (BlueprintIcon?)icon).FirstOrDefault()
                    ?? throw new ProjectException($"{entry} has unknown icon \"{dto.Icon}\"."),
                Color = dto.Color is null ? null : Enum.GetValues<BlueprintColor>()
                    .Where(color => ColorName(color) == dto.Color).Select(color => (BlueprintColor?)color).FirstOrDefault()
                    ?? throw new ProjectException($"{entry} has unknown color \"{dto.Color}\"."),
            };
        }
        catch (BlueprintException e)
        {
            throw new ProjectException($"{where}: {e.Message}", e);
        }
    }

    public static string[] Lines(string text) => text.Length == 0 ? [] : text.Split('\n');

    /// <exception cref="ProjectException">A line is null.</exception>
    public static string Text(string?[] lines, string entry, string property) => lines.Contains(null)
        ? throw new ProjectException($"{entry} has a null line in {property}.")
        : string.Join('\n', lines);

    public static ExecutionDto? Execution(ExecutionSettings? settings) => settings is null
        ? null
        : new ExecutionDto { Client = Clients.WireName(settings.Client), Model = settings.Model, Reasoning = settings.Reasoning };

    /// <exception cref="ProjectException">The client is unknown.</exception>
    public static ExecutionSettings Execution(ExecutionDto execution, string entry) =>
        new(Clients.ParseWireName(execution.Client) ?? throw new ProjectException($"{entry} names unknown agent \"{execution.Client}\"."))
        {
            Model = execution.Model,
            Reasoning = execution.Reasoning,
        };

    public static string ConversationName(ConversationMode mode) => mode switch
    {
        ConversationMode.Autonomous => "autonomous",
        ConversationMode.MayAsk => "mayAsk",
        ConversationMode.Chat => "chat",
    };

    /// <exception cref="ProjectException">The mode is unknown.</exception>
    public static ConversationMode Conversation(string name, string entry) =>
        Enum.GetValues<ConversationMode>().Where(mode => ConversationName(mode) == name).Select(mode => (ConversationMode?)mode).FirstOrDefault()
        ?? throw new ProjectException($"{entry} has unknown conversation mode \"{name}\".");

    private static WorkDto Work(WorkSpec work) => work.Kind switch
    {
        WorkKind.Agent => new WorkDto
        {
            Kind = KindName(WorkKind.Agent),
            Access = AccessName(((WorkSpec.Agent)work).Access),
            Proposes = ((WorkSpec.Agent)work).Proposes,
            Template = Lines(((WorkSpec.Agent)work).Template.Text),
        },
        WorkKind.Review => new WorkDto
        {
            Kind = KindName(WorkKind.Review),
            Reviewer = Lines(((WorkSpec.Review)work).Reviewer.Text),
            Fix = Lines(((WorkSpec.Review)work).Fix.Text),
        },
        WorkKind.Person => new WorkDto { Kind = KindName(WorkKind.Person) },
    };

    /// <summary>Each work reads only its own properties, and a property of another work is an error.</summary>
    /// <exception cref="ProjectException">The work's properties do not fit its kind.</exception>
    private static WorkSpec Work(WorkKind kind, WorkDto dto, string entry)
    {
        string[] present =
        [
            .. dto.Access is null ? [] : new[] { "access" },
            .. dto.Proposes is null ? [] : new[] { "proposes" },
            .. dto.Template is null ? [] : new[] { "template" },
            .. dto.Reviewer is null ? [] : new[] { "reviewer" },
            .. dto.Fix is null ? [] : new[] { "fix" },
        ];
        string[] takes = kind switch
        {
            WorkKind.Agent => ["access", "proposes", "template"],
            WorkKind.Review => ["reviewer", "fix"],
            WorkKind.Person => [],
        };
        if (takes.FirstOrDefault(name => !present.Contains(name)) is { } missing)
        {
            throw new ProjectException($"{entry}'s {KindName(kind)} work has no {missing}.");
        }

        if (present.FirstOrDefault(name => !takes.Contains(name)) is { } extra)
        {
            throw new ProjectException($"{entry}'s {KindName(kind)} work has a {extra}, which only another work takes.");
        }

        return kind switch
        {
            WorkKind.Agent => new WorkSpec.Agent(
                ParseAccess(dto.Access!) ?? throw new ProjectException($"{entry} has unknown access \"{dto.Access}\"."),
                dto.Proposes!.Value,
                Template(dto.Template!, entry, "template")),
            WorkKind.Review => new WorkSpec.Review(Template(dto.Reviewer!, entry, "reviewer template"), Template(dto.Fix!, entry, "fix template")),
            WorkKind.Person => new WorkSpec.Person(),
        };
    }

    private static PromptTemplate Template(string?[] lines, string entry, string property)
    {
        try
        {
            return PromptTemplate.Parse(Text(lines, entry, property));
        }
        catch (FormatException e)
        {
            throw new ProjectException($"{entry} has a {property} iDevelop cannot read. {e.Message}", e);
        }
    }

    private static string IconName(BlueprintIcon icon) => icon switch
    {
        BlueprintIcon.Code => "code",
        BlueprintIcon.TaskList => "taskList",
        BlueprintIcon.Ruler => "ruler",
        BlueprintIcon.Glasses => "glasses",
        BlueprintIcon.PersonAvailable => "personAvailable",
        BlueprintIcon.DocumentSearch => "documentSearch",
    };

    private static string ColorName(BlueprintColor color) => color switch
    {
        BlueprintColor.Indigo => "indigo",
        BlueprintColor.Cyan => "cyan",
        BlueprintColor.Purple => "purple",
        BlueprintColor.Mint => "mint",
        BlueprintColor.Brown => "brown",
        BlueprintColor.Gray => "gray",
    };

    private static string KindName(WorkKind kind) => kind switch
    {
        WorkKind.Agent => "agent",
        WorkKind.Review => "review",
        WorkKind.Person => "person",
    };

    private static string AccessName(AgentAccess access) => access switch
    {
        AgentAccess.ReadOnly => "readOnly",
        AgentAccess.Edit => "edit",
    };

    private static AgentAccess? ParseAccess(string name) =>
        Enum.GetValues<AgentAccess>().Where(access => AccessName(access) == name).Select(access => (AgentAccess?)access).FirstOrDefault();
}

// RespectNullableAnnotations does not reach collection elements, so those are nullable here and the reader reports each
// null with its entry.
internal class BlueprintDto
{
    public required string Id { get; init; }
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required string?[] Description { get; init; }
    public required string? DerivedFrom { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Icon { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; init; }
    public required WorkDto Work { get; init; }
    public required List<FieldDto?> Fields { get; init; }
    public required SettingsDto Defaults { get; init; }
}

internal sealed class WorkDto
{
    public required string Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Access { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Proposes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?[]? Template { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?[]? Reviewer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?[]? Fix { get; init; }
}

internal sealed class FieldDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Shape { get; init; }
    public required bool Required { get; init; }
    public required string?[] Default { get; init; }
}

internal sealed class SettingsDto
{
    public required ExecutionDto? Execution { get; init; }
    public required string Conversation { get; init; }
}

internal sealed class ExecutionDto
{
    public required string Client { get; init; }
    public required string? Model { get; init; }
    public required string? Reasoning { get; init; }
}
