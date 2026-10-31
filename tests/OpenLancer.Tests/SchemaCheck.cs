using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The check of a JSON value against a JSON Schema, for the subset that the tool table uses: the keywords of the input
/// schemas of <c>ToolInput</c>, of the output schemas that <c>JsonSchemaExporter</c> writes, and of the batch schema of
/// <c>MissionJson.Schema()</c>. An unknown keyword is a problem, so the check never passes a schema that it does not
/// read. A <c>$ref</c> resolves as a JSON Pointer (RFC 6901) in the root schema, and a <c>$ref</c> that does not resolve
/// is a problem. The repository has no JSON Schema package (MCP1 open alignment item 4).
/// </summary>
internal static class SchemaCheck
{
    private static readonly HashSet<string> Known =
    [
        "type", "properties", "additionalProperties", "required", "items", "enum", "minimum", "maximum", "minLength",
        "maxLength", "minItems", "maxItems", "uniqueItems", "default", "description", "$ref", "anyOf", "pattern", "$schema",
        "minProperties", "$defs", "oneOf", "const", "title", "examples",
    ];

    private static readonly HashSet<string> Types = ["object", "array", "string", "integer", "number", "boolean", "null"];

    /// <summary>The problems of a schema itself: an unknown keyword or type, or a <c>$ref</c> that does not resolve.
    /// Empty for a valid schema of the subset.</summary>
    public static List<string> SchemaProblems(JsonNode? schema) => SchemaProblems(schema, schema, "#");

    private static List<string> SchemaProblems(JsonNode? root, JsonNode? schema, string path)
    {
        var problems = new List<string>();
        if (schema is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return problems;
        if (schema is not JsonObject obj)
            return [$"{path}: a schema must be an object or a boolean."];
        foreach (var (key, value) in obj)
        {
            if (!Known.Contains(key))
                problems.Add($"{path}: the keyword '{key}' is not in the subset.");
            switch (key)
            {
                case "type":
                    var names = value is JsonArray a ? a.Select(t => (string?)t) : [(string?)value];
                    problems.AddRange(names.Where(n => n == null || !Types.Contains(n)).Select(n => $"{path}: the type '{n}' is not valid."));
                    break;
                case "properties" or "$defs":
                    foreach (var (name, property) in value!.AsObject())
                        problems.AddRange(SchemaProblems(root, property, $"{path}/{key}/{name}"));
                    break;
                case "items" or "additionalProperties":
                    problems.AddRange(SchemaProblems(root, value, $"{path}/{key}"));
                    break;
                case "anyOf" or "oneOf":
                    foreach (var option in value!.AsArray())
                        problems.AddRange(SchemaProblems(root, option, $"{path}/{key}"));
                    break;
                case "$ref":
                    if (value?.GetValueKind() != JsonValueKind.String || Resolve(root!, (string)value!) == null)
                        problems.Add($"{path}: the reference '{value}' does not resolve.");
                    break;
            }
        }
        return problems;
    }

    /// <summary>The problems of a value against a schema. Empty when the value fits.</summary>
    public static List<string> Problems(JsonNode schema, JsonNode? value)
    {
        var problems = SchemaProblems(schema);
        if (problems.Count == 0)
            Check(schema, schema, value, "$", problems);
        return problems;
    }

    private static void Check(JsonNode root, JsonNode schema, JsonNode? value, string path, List<string> problems)
    {
        if (schema is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (b.GetValueKind() == JsonValueKind.False)
                problems.Add($"{path}: the schema allows no value.");
            return;
        }
        var obj = schema.AsObject();
        if (obj["$ref"] is { } reference)
        {
            Check(root, Resolve(root, (string)reference!)!, value, path, problems);
            return;
        }
        // anyOf and oneOf apply together with the other keywords of the schema.
        if (obj["anyOf"] is JsonArray anyOf && !anyOf.Any(option => Fits(root, option!, value)))
            problems.Add($"{path}: the value fits no schema of anyOf.");
        if (obj["oneOf"] is JsonArray oneOf && oneOf.Count(option => Fits(root, option!, value)) is var fits and not 1)
            problems.Add($"{path}: the value fits {fits} schemas of oneOf, not exactly one.");
        if (obj.TryGetPropertyValue("const", out var constant) && !JsonNode.DeepEquals(constant, value))
            problems.Add($"{path}: the value is not the constant {constant?.ToJsonString()}.");
        if (obj["type"] is { } type)
        {
            var types = type is JsonArray a ? a.Select(t => (string)t!).ToList() : [(string)type!];
            if (!types.Any(t => IsType(t, value)))
            {
                problems.Add($"{path}: the value is not of the type {string.Join(" or ", types)}.");
                return;
            }
        }
        if (obj["enum"] is JsonArray values && !values.Any(e => JsonNode.DeepEquals(e, value)))
            problems.Add($"{path}: the value is not in the enum.");
        if (value?.GetValueKind() == JsonValueKind.String)
        {
            var length = value.GetValue<string>().Length;
            if (obj["minLength"] is { } min && length < (int)min)
                problems.Add($"{path}: the text is shorter than {(int)min} characters.");
            if (obj["maxLength"] is { } max && length > (int)max)
                problems.Add($"{path}: the text is longer than {(int)max} characters.");
        }
        if (value is JsonObject o)
        {
            if (obj["minProperties"] is { } fewest && o.Count < (int)fewest)
                problems.Add($"{path}: the object has fewer than {(int)fewest} properties.");
            var properties = obj["properties"]?.AsObject();
            foreach (var name in obj["required"]?.AsArray().Select(r => (string)r!) ?? [])
            {
                if (!o.ContainsKey(name))
                    problems.Add($"{path}: the property '{name}' is necessary.");
            }
            foreach (var (name, item) in o)
            {
                if (properties?[name] is { } property)
                    Check(root, property, item, $"{path}.{name}", problems);
                else if (obj["additionalProperties"] is { } more)
                    Check(root, more, item, $"{path}.{name}", problems);
            }
        }
        if (value is JsonArray array && obj["items"] is { } items)
        {
            for (var i = 0; i < array.Count; i++)
                Check(root, items, array[i], $"{path}[{i}]", problems);
        }
    }

    private static bool Fits(JsonNode root, JsonNode schema, JsonNode? value)
    {
        var problems = new List<string>();
        Check(root, schema, value, "$", problems);
        return problems.Count == 0;
    }

    /// <summary>The schema of a <c>$ref</c> in the root schema, or null when it does not resolve. The reference is a
    /// JSON Pointer in a URI fragment (RFC 6901): <c>/</c> splits the path, <c>~1</c> is a <c>/</c> and <c>~0</c> is a
    /// <c>~</c> in a name.</summary>
    private static JsonNode? Resolve(JsonNode root, string reference)
    {
        Assert.StartsWith("#", reference);
        var node = root;
        foreach (var part in reference[1..].Split('/').Skip(1))
        {
            var name = part.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonArray a when int.TryParse(name, out var index) && index >= 0 && index < a.Count => a[index],
                JsonObject o when o.TryGetPropertyValue(name, out var child) => child,
                _ => null,
            };
            if (node == null)
                return null;
        }
        return node;
    }

    private static bool IsType(string type, JsonNode? value) => type switch
    {
        "null" => value == null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value?.GetValueKind() == JsonValueKind.String,
        "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "integer" => value?.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() % 1 == 0,
        "number" => value?.GetValueKind() == JsonValueKind.Number,
        _ => false,
    };
}
