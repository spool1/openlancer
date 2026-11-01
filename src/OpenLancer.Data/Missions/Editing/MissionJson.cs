using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The JSON form of the mission commands for MCP1 (section 4.13 of the M6b design): snake case property names and
/// <c>kind</c> names, the reference strings of section 4.0.7, and named values of lines.
/// </summary>
/// <remarks>A line also takes <c>texts</c>: its value texts by position. The inverses use them to give back a line
/// exactly as it was, so an inverse in a result can go into the next request.</remarks>
public static class MissionJson
{
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> KindTable = new(() =>
        typeof(MissionCommand).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsPublic: true } && typeof(MissionCommand).IsAssignableFrom(t))
            .ToDictionary(KindOf, t => t, StringComparer.Ordinal));

    /// <summary>The JSON settings of the command layer (see <see cref="EditJson.Options"/>).</summary>
    public static JsonSerializerOptions Options => EditJson.Options;

    /// <summary>The <c>kind</c> name of each command type, for example <c>add_trigger</c>.</summary>
    public static IReadOnlyDictionary<string, Type> Kinds => KindTable.Value;

    /// <summary>The <c>kind</c> name of a command type: its name in snake case.</summary>
    public static string KindOf(Type type) => JsonNamingPolicy.SnakeCaseLower.ConvertName(type.Name);

    /// <summary>Reads a batch.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the JSON is not a batch, the batch,
    /// its options or a command has an unknown field (<c>UnknownField</c>), a command has an unknown kind
    /// (<c>UnknownCommand</c>), or a reference is not valid.</exception>
    public static MissionBatch ReadBatch(string json)
    {
        CheckBatchFields(json);
        return Read<MissionBatch>(json);
    }

    /// <summary>Refuses a field that the batch or its options do not have, so that a typo, for example in
    /// <c>reject_new_errors</c>, does not turn off a guard without an error.</summary>
    private static void CheckBatchFields(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new EditException(MissionErrors.BadJsonError(Where(e)));
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            Check(root, typeof(MissionBatch), options: false);
            if (root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
                Check(options, typeof(MissionEditOptions), options: true);
        }

        static void Check(JsonElement element, Type type, bool options)
        {
            var fields = FieldsOf(type, SchemaOptions.Value);
            foreach (var property in element.EnumerateObject())
            {
                if (!fields.Contains(property.Name))
                    throw new EditException(MissionErrors.UnknownBatchFieldError(property.Name, options));
            }
        }
    }

    /// <summary>Reads one command.</summary>
    /// <exception cref="EditException">See <see cref="ReadBatch"/>.</exception>
    public static MissionCommand ReadCommand(string json) => Read<MissionCommand>(json);

    /// <summary>Writes a value of the command layer: a batch, a command, a result or a query result. A command gets its
    /// <c>kind</c> also when its static type is the type of the command.</summary>
    public static string Write<T>(T value) => value is MissionCommand command
        ? JsonSerializer.Serialize(command, Options)
        : JsonSerializer.Serialize(value, Options);

    private static T Read<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ??
                   throw new EditException(MissionErrors.BadJsonError("The request is empty."));
        }
        catch (MissionJsonException e)
        {
            throw new EditException(e.Error);
        }
        catch (JsonException e) when (e.InnerException is MissionJsonException inner)
        {
            throw new EditException(inner.Error);
        }
        catch (JsonException e)
        {
            throw new EditException(MissionErrors.BadJsonError(Where(e)));
        }
    }

    /// <summary>The place of a JSON error as a sentence. The text of the exception is not for users.</summary>
    private static string Where(JsonException e)
    {
        var place = e.Path is { Length: > 0 } path && path != "$" ? $"The value at {path}" : "The JSON";
        var line = e.LineNumber is { } number ? $" on line {(number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        return $"{place}{line} does not have the form of a mission batch.";
    }

    /// <summary>
    /// The JSON Schema of a batch (section 4.13 of the M6b design): the batch fields, a definition for each command
    /// kind, and a definition for each line key with its named values from <c>mission-schema.json</c>. MCP1 gives it as
    /// the input schema of its tool <c>mission_edit</c>. The schema comes from the command types, so it follows them.
    /// </summary>
    /// <param name="help">Gives the help of a type of the command layer without a field, or of one of its JSON fields,
    /// or null. A command type without a field gives the description of its definition. Each field of a command, also
    /// of an object in a command, gets its help as its description. Null gives no help. MCP1 gives the texts of its
    /// data file (section 9.1 of the MCP1 design).</param>
    public static JsonObject Schema(Func<Type, string?, string?>? help = null)
    {
        var schema = MissionSchema.Retail;
        var definitions = new JsonObject();
        foreach (var (kind, type) in Kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
            definitions[kind] = CommandSchema(kind, type, help);
        definitions["command"] = OneOf(Kinds.Keys.Order(StringComparer.Ordinal));

        var lines = new List<string>();
        foreach (var name in schema.Conditions.Concat(schema.Actions).Concat(schema.Directives)
                     .DistinctBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(LineDefinition(name.Name));
            definitions[LineDefinition(name.Name)] = LineSchema(name.Name, name.Plain, name.Values, schema);
        }
        foreach (var section in schema.Sections.Append(schema.ShipBuild))
        {
            foreach (var key in section.Keys)
            {
                lines.Add(KeyDefinition(section.Type, key.Key));
                definitions[KeyDefinition(section.Type, key.Key)] = LineSchema(key.Key, key.Label, key.AllValueSpecs, schema);
            }
        }
        definitions["line"] = new JsonObject
        {
            ["description"] = "A line: its key, and its values by name. A key can be in more than one section type, so " +
                              "each definition of a key fits.",
            ["anyOf"] = new JsonArray([.. lines.Select(Ref)]),
        };

        var batch = Export(typeof(MissionBatch), null);
        batch["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        batch["title"] = "Mission batch";
        batch["description"] = "Mission commands that give one history entry and one validation.";
        batch["properties"]!["commands"] = new JsonObject { ["type"] = "array", ["items"] = Ref("command") };
        batch["required"] = new JsonArray("label", "commands");
        batch["additionalProperties"] = false;
        batch["$defs"] = definitions;
        return batch;
    }

    /// <summary>The schema of a command: its <c>kind</c>, its fields, and the fields of its constructor as the
    /// required fields.</summary>
    private static JsonObject CommandSchema(string kind, Type type, Func<Type, string?, string?>? help)
    {
        var exported = Export(type, help);
        var properties = new JsonObject
        {
            ["kind"] = Described(new JsonObject { ["const"] = kind }, help?.Invoke(type, "kind")),
        };
        foreach (var (name, value) in exported["properties"]?.AsObject().ToList() ?? [])
            properties[name] = value?.DeepClone();
        // The exporter gives no items for a list of commands, because a command has its own converter.
        foreach (var property in SchemaOptions.Value.GetTypeInfo(type).Properties
                     .Where(p => typeof(IEnumerable<MissionCommand>).IsAssignableFrom(p.PropertyType)))
        {
            properties[property.Name] = Described(new JsonObject { ["type"] = "array", ["items"] = Ref("command") },
                help?.Invoke(type, property.Name));
        }
        var required = new JsonArray("kind");
        foreach (var parameter in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First()
                     .GetParameters().Where(p => !p.HasDefaultValue))
            required.Add(JsonNamingPolicy.SnakeCaseLower.ConvertName(parameter.Name!));
        var schema = Described(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        }, help?.Invoke(type, null));
        Relocate(schema, "#/$defs/" + kind);
        return schema;
    }

    /// <summary>A schema with a description, or the schema as it is without one. The schema <c>true</c> takes any
    /// value, so it becomes an object with only the description.</summary>
    private static JsonNode Described(JsonNode node, string? description)
    {
        if (description == null)
            return node;
        var schema = node as JsonObject ?? new JsonObject();
        schema["description"] = description;
        return schema;
    }

    private static JsonObject Described(JsonObject node, string? description) =>
        (JsonObject)Described((JsonNode)node, description);

    /// <summary>
    /// Moves each <c>$ref</c> that the exporter wrote to the place of a definition. The exporter names a type that its
    /// schema has two times with a <c>$ref</c> from the root of that schema, for example <c>#/properties/sections/items</c>.
    /// The schema of a command is a definition of the batch, so such a <c>$ref</c> must start at the definition. A
    /// <c>$ref</c> to a definition of the batch stays.
    /// </summary>
    /// <param name="root">The JSON Pointer of the definition, for example <c>#/$defs/create_script</c>.</param>
    private static void Relocate(JsonNode? node, string root)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue value && value.GetValueKind() == JsonValueKind.String &&
                    value.GetValue<string>() is { } reference && reference.StartsWith('#') &&
                    !reference.StartsWith("#/$defs/", StringComparison.Ordinal))
                    obj["$ref"] = root + reference[1..];
                foreach (var (_, child) in obj.ToList())
                    Relocate(child, root);
                break;
            case JsonArray array:
                foreach (var child in array)
                    Relocate(child, root);
                break;
        }
    }

    /// <summary>The schema of a line key with its named values.</summary>
    private static JsonObject LineSchema(string key, string title, IEnumerable<MissionValueSpec> specs, MissionSchema schema)
    {
        var values = new JsonObject();
        foreach (var spec in specs)
            values[spec.Name] = ValueSchema(spec, schema);
        return new JsonObject
        {
            ["type"] = "object",
            ["title"] = title,
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["const"] = key },
                ["values"] = new JsonObject { ["type"] = "object", ["properties"] = values, ["additionalProperties"] = false },
                ["texts"] = new JsonObject
                {
                    ["description"] = "The value texts by position, in place of the values by name. An inverse writes them.",
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
            ["required"] = new JsonArray("key"),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>The schema of a named value by its kind (section 4.3 of the M6b design).</summary>
    private static JsonObject ValueSchema(MissionValueSpec spec, MissionSchema schema)
    {
        JsonObject one = spec.Kind switch
        {
            MissionValueKind.Position or MissionValueKind.Rotation => new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "number" },
                ["minItems"] = spec.Width,
                ["maxItems"] = spec.Width,
            },
            // A string that holds a number is also valid for a number kind. A text id is also $str:<key>.
            MissionValueKind.Number or MissionValueKind.WholeNumber or MissionValueKind.TextId =>
                new JsonObject { ["type"] = new JsonArray("number", "string") },
            MissionValueKind.Bool => new JsonObject { ["type"] = "boolean" },
            _ => new JsonObject { ["type"] = "string" },
        };
        var words = new[] { spec.Tokens, spec.Also }.OfType<string>()
            .SelectMany(id => schema.Tokens.TryGetValue(id, out var set) ? set.Values : []).ToList();
        if (words.Count > 0)
            one["examples"] = new JsonArray([.. words.Select(w => (JsonNode)w)]);
        var value = spec.Rest ? new JsonObject { ["type"] = "array", ["items"] = one } : one;
        value["title"] = spec.Label;
        if (spec.Help != null)
            value["description"] = spec.Help;
        return value;
    }

    /// <summary>The settings of the command layer with the resolver that the schema exporter needs.</summary>
    private static readonly Lazy<JsonSerializerOptions> SchemaOptions = new(() =>
        new JsonSerializerOptions(Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

    /// <summary>The schema of a type of the command layer, with the reference strings in place of the records. Each
    /// property gets its help as its description (see <see cref="Schema"/>).</summary>
    private static JsonObject Export(Type type, Func<Type, string?, string?>? help)
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions.Value, type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var schema = ReferenceSchema(context.TypeInfo.Type, help) ??
                             (context.PropertyInfo?.CustomConverter is NullableLongAsText
                                 ? new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9]+$" }
                                 : node);
                return context.PropertyInfo is { } property
                    ? Described(schema, help?.Invoke(property.DeclaringType, property.Name))
                    : schema;
            },
        });
        return node.AsObject();
    }

    /// <summary>The schema of a type that JSON writes in its own form, or null.</summary>
    private static JsonNode? ReferenceSchema(Type type, Func<Type, string?, string?>? help)
    {
        static JsonObject Text(string prefix, string example) => new()
        {
            ["type"] = "string",
            ["pattern"] = $"^({prefix}):",
            ["examples"] = new JsonArray(example),
        };
        if (type == typeof(ScriptRef))
            return Text("msn", @"msn:DATA\MISSIONS\M02\m02.ini");
        if (type == typeof(TriggerRef))
            return Text("trg", @"trg:DATA\MISSIONS\M02\m02.ini#ol_trigger");
        if (type == typeof(ItemRef))
            return Text("sec|trg", @"sec:DATA\MISSIONS\M02\m02.ini#MsnShip/ol_ship");
        if (type == typeof(LabelRef))
            return Text("label", @"label:DATA\MISSIONS\M02\m02.ini#ol_wing");
        if (type == typeof(ActRef))
            return Text("act", @"act:DATA\MISSIONS\M02\m02.ini#act_1");
        if (type == typeof(InstanceRef))
            return Text("inst", @"inst:DATA\MISSIONS\M02\m02.ini#ol_wave_1");
        if (type == typeof(PrefabRef))
            return Text("prefab", "prefab:builtin:spawnWave");
        if (type == typeof(RowRef))
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["ref"] = Described(Text("line", @"line:DATA\MISSIONS\M02\m02.ini#Trigger/ol_trigger/2"),
                        help?.Invoke(type, "ref")),
                    ["expect_key"] = Described(new JsonObject { ["type"] = "string" }, help?.Invoke(type, "expect_key")),
                    ["expect_values"] = Described(
                        new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        help?.Invoke(type, "expect_values")),
                },
                ["required"] = new JsonArray("ref"),
                ["additionalProperties"] = false,
            };
        if (type == typeof(LineSpec))
            return Ref("line");
        if (type == typeof(MissionCommand))
            return Ref("command");
        if (type == typeof(MissionValue))
            return new JsonObject
            {
                ["type"] = new JsonArray("number", "string", "boolean", "array"),
                ["items"] = new JsonObject { ["type"] = new JsonArray("number", "string", "boolean") },
            };
        return null;
    }

    /// <summary>The name of the definition of a condition, an action or an order, for example
    /// <c>line.Cnd_Timer</c>.</summary>
    /// <remarks>A definition name has only the characters A to Z, a to z, 0 to 9, <c>_</c>, <c>.</c> and <c>-</c>. In a
    /// JSON Pointer, <c>/</c> splits the path, and <c>~</c> starts an escape (RFC 6901). So each <c>$ref</c> resolves as
    /// it is.</remarks>
    internal static string LineDefinition(string key) => "line." + key;

    /// <summary>The name of the definition of a key of a section type, for example <c>key.MsnShip.npc</c>. See
    /// <see cref="LineDefinition"/> for the characters.</summary>
    internal static string KeyDefinition(string type, string key) => $"key.{type}.{key}";

    private static JsonObject Ref(string definition) => new() { ["$ref"] = "#/$defs/" + definition };

    private static JsonObject OneOf(IEnumerable<string> definitions) =>
        new() { ["oneOf"] = new JsonArray([.. definitions.Select(Ref)]) };

    /// <summary>The JSON names of the fields of a type of the command layer. A field that JSON ignores, for example the
    /// origin of a batch, is not a field.</summary>
    internal static IReadOnlySet<string> FieldsOf(Type type, JsonSerializerOptions options) =>
        options.GetTypeInfo(type).Properties
            .Where(p => p.AttributeProvider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) != true)
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Reads and writes a <see cref="MissionCommand"/> with its <c>kind</c> (section 4.13 of the M6b design). An
/// unknown kind gives <c>UnknownCommand</c>, and an unknown field gives <c>UnknownField</c>.</summary>
public sealed class MissionCommandConverter : JsonConverter<MissionCommand>
{
    public override MissionCommand? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
            throw new MissionJsonException(MissionErrors.BadJsonError("A mission command is an object with a 'kind'."));
        var kind = kindElement.GetString()!;
        if (!MissionJson.Kinds.TryGetValue(kind, out var type))
            throw new MissionJsonException(MissionErrors.UnknownCommandError(kind));
        var fields = MissionJson.FieldsOf(type, options);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "kind" && !fields.Contains(property.Name))
                throw new MissionJsonException(MissionErrors.UnknownFieldError(kind, property.Name));
        }
        return (MissionCommand?)root.Deserialize(type, options);
    }

    public override void Write(Utf8JsonWriter writer, MissionCommand value, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(value, value.GetType(), options);
        writer.WriteStartObject();
        writer.WriteString("kind", MissionJson.KindOf(value.GetType()));
        foreach (var property in element.EnumerateObject())
            property.WriteTo(writer);
        writer.WriteEndObject();
    }
}
