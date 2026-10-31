using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The JSON form of the mission commands for MCP1 (section 4.13 of the M6b design).</summary>
public sealed class MissionJsonTests
{
    public static IEnumerable<object[]> Kinds() => Samples().Keys.Select(k => new object[] { k });

    /// <summary>One sample of each command kind: the samples of the inverse tests, the four kinds that need an edit
    /// before them, and the prefab commands.</summary>
    private static Dictionary<string, Func<MissionCommand>> Samples()
    {
        var samples = new Dictionary<string, Func<MissionCommand>>(MissionInverseTests.Commands)
        {
            ["delete_script"] = () => new DeleteScript(TestScript),
            ["revert_section"] = () => new RevertSection(T("ol_end").Item) { Name = "Cnd_Destroyed" },
            ["accept_base"] = () => new AcceptBase(Item("MsnShip", "ol_ship_1")),
            ["remove_act"] = () => new RemoveAct(new ActRef(Script, "act_1")),
        };
        foreach (var (kind, make) in PrefabInstanceTests.Commands.Concat(PrefabModeTests.Commands))
            samples["prefab:" + kind] = make;
        return samples;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachCommandKindRoundTripsInSnakeCase(string kind)
    {
        var command = Samples()[kind]();

        var json = MissionJson.Write(command);
        var back = MissionJson.ReadCommand(json);

        Assert.Equal(command.GetType(), back.GetType());
        Assert.Equal(json, MissionJson.Write(back));
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(MissionJson.KindOf(command.GetType()), (string?)root["kind"]);
        Assert.All(root.Select(p => p.Key), name => Assert.Equal(JsonNamingPolicy.SnakeCaseLower.ConvertName(name), name));
        Assert.DoesNotContain("history_label", root.Select(p => p.Key));
    }

    /// <summary>Each inverse of a sample round trips too, also a group with body guards.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachInverseRoundTrips(string kind)
    {
        // These kinds need an edit before them. The prefab commands have their inverse tests in PrefabInstanceTests and
        // PrefabModeTests, with a JSON round trip of each inverse.
        if (kind is "delete_script" or "revert_section" or "accept_base" or "remove_act" || kind.StartsWith("prefab:"))
            return;
        using var m = Create();
        var inverse = m.Apply(Samples()[kind]()).Inverse!;

        var json = MissionJson.Write(inverse);

        Assert.Equal(json, MissionJson.Write(MissionJson.ReadCommand(json)));
        var result = m.Missions.Apply(MissionJson.ReadCommand(json));
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void TheReferencesOfAKeylessSectionARepeatedKeyAndAPathInAnotherCase()
    {
        var mission = new SetLineValue(new RowRef(ItemRef.Of(Script, "Mission", null), 0), "missions\\x.ini");
        var repeated = new RemoveLine(new RowRef(ItemRef.Of(Script, "MsnShip", "ol_ship", 1), 3)
            { ExpectKey = "label", ExpectValues = ["ol_wing"] });

        Assert.Equal("""{"kind":"set_line_value","row":{"ref":"line:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#Mission/0"},"value":"missions\\x.ini"}""",
            MissionJson.Write(mission));
        Assert.Equal("""{"kind":"remove_line","row":{"ref":"line:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#MsnShip/ol_ship~1/3","expect_key":"label","expect_values":["ol_wing"]}}""",
            MissionJson.Write(repeated));
        var back = (RemoveLine)MissionJson.ReadCommand(MissionJson.Write(repeated));
        Assert.Equal((1, 3, "label"), (back.Row.Item.Section.Ordinal, back.Row.Row, back.Row.ExpectKey));

        using var m = Create();
        var lower = MissionJson.ReadCommand("""{"kind":"set_trigger_start","trigger":"trg:data/missions/ol_test/OL_TEST.INI#OL_END","starts":true}""");
        m.Apply(lower);
        Assert.Contains("InitState = ACTIVE", m.Lines("ol_end"));
    }

    [Fact]
    public void AnUnknownKindGivesUnknownCommand()
    {
        var error = Assert.Throws<EditException>(() => MissionJson.ReadBatch(
            """{"label":"x","commands":[{"kind":"launch_rocket"}]}""")).Error;

        Assert.Equal((EditErrorKind.InvalidArgument, MissionErrors.UnknownCommand), (error.Kind, error.Detail));
        Assert.Equal(EditErrorKind.InvalidArgument,
            Assert.Throws<EditException>(() => MissionJson.ReadCommand("""{"kind":"set_trigger_start","trigger":"sec:x"}""")).Error.Kind);
        Assert.Equal(EditErrorKind.InvalidArgument, Assert.Throws<EditException>(() => MissionJson.ReadBatch("[1, 2")).Error.Kind);
    }

    /// <summary>The batch example of section 4.13 reads and runs.</summary>
    [Fact]
    public void TheBatchExampleOfTheDesignReadsAndRuns()
    {
        using var m = Create();
        var json = """
            {
              "label": "Add a delay before the escort starts",
              "expect_versions": { "DATA\\MISSIONS\\OL_TEST\\ol_test.ini": 1 },
              "options": { "reject_new_errors": true },
              "commands": [
                {
                  "kind": "insert_trigger_on_link",
                  "row": { "ref": "line:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#Trigger/ol_start/4", "expect_key": "Act_ActTrig" },
                  "nickname": "ol_launch_delay",
                  "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } }
                }
              ]
            }
            """;

        var batch = MissionJson.ReadBatch(json);
        var result = m.Missions.Apply(batch);

        Assert.Equal(EditOrigin.User, batch.Origin);
        Assert.True(batch.Options.RejectNewErrors);
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(["nickname = ol_launch_delay", "Cnd_Timer = 3", "Act_ActTrig = ol_end"], m.Lines("ol_launch_delay"));
    }

    /// <summary>The result has the shape of the example of section 4.13.</summary>
    [Fact]
    public void TheResultHasTheShapeOfTheDesign()
    {
        using var m = Create();

        var result = m.Apply(new InsertTriggerOnLink(Row("ol_start", 4) with { ExpectKey = "Act_ActTrig" },
            LineSpec.Of("Cnd_Timer", ("seconds", 3))) { Nickname = "ol_launch_delay" });
        var root = JsonNode.Parse(MissionJson.Write(result))!.AsObject();

        Assert.Equal("applied", (string?)root["status"]);
        Assert.Equal(m.Session.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)root["rev"]);
        Assert.Equal(2, (long?)root["versions"]![Script]);
        Assert.Equal([$"trg:{Script}#ol_launch_delay"], root["adds"]!.AsArray().Select(n => (string?)n));
        Assert.Equal($"trg:{Script}#ol_launch_delay", (string?)root["select"]);
        var inverse = root["inverse"]!.AsObject();
        Assert.Equal("command_group", (string?)inverse["kind"]);
        Assert.Equal(2, inverse["expect_bodies"]!.AsObject().Count);
        Assert.Equal(["retarget_link", "remove_trigger"],
            inverse["commands"]!.AsArray().Select(c => (string?)c!["kind"]));
        Assert.Equal("keep", (string?)inverse["commands"]![1]!["references"]);
    }

    /// <summary>The schema of a batch has a definition for each command kind and each line key of
    /// <c>mission-schema.json</c>. M6b adds no package of a JSON Schema validator.</summary>
    [Fact]
    public void TheSchemaHasEachCommandKindAndEachLineKey()
    {
        var schema = MissionJson.Schema();
        var definitions = schema["$defs"]!.AsObject();

        Assert.Equal("object", (string?)schema["type"]);
        Assert.All(MissionJson.Kinds.Keys, kind => Assert.True(definitions.ContainsKey(kind), kind));
        Assert.Equal("#/$defs/command", (string?)schema["properties"]!["commands"]!["items"]!["$ref"]);
        Assert.Equal(MissionJson.Kinds.Count, definitions["command"]!["oneOf"]!.AsArray().Count);
        var lines = MissionSchema.Retail.Conditions.Concat(MissionSchema.Retail.Actions).Concat(MissionSchema.Retail.Directives)
            .Select(n => n.Name).ToList();
        Assert.All(lines, key => Assert.True(definitions.ContainsKey("line." + key), key));
        Assert.True(definitions.ContainsKey("key.MsnShip.npc"));
        var timer = definitions["line.Cnd_Timer"]!;
        Assert.Equal("Cnd_Timer", (string?)timer["properties"]!["key"]!["const"]);
        Assert.True(timer["properties"]!["values"]!["properties"]!.AsObject().ContainsKey("seconds"));
        var trigger = definitions["add_trigger"]!;
        Assert.Equal("add_trigger", (string?)trigger["properties"]!["kind"]!["const"]);
        Assert.Contains("script", trigger["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("#/$defs/command", (string?)definitions["command_group"]!["properties"]!["commands"]!["items"]!["$ref"]);
        Assert.Equal("#/$defs/line", (string?)trigger["properties"]!["condition"]!["$ref"]);
        Assert.Equal("^(trg):", (string?)definitions["remove_trigger"]!["properties"]!["trigger"]!["pattern"]);
        // An act is a reference text too, not any value (final finding 18 of MCP1-3).
        Assert.Equal("^(act):", (string?)trigger["properties"]!["act"]!["pattern"]);
        Assert.Equal(["x", "y"], trigger["properties"]!["place"]!["required"]!.AsArray().Select(n => (string?)n));
        Assert.DoesNotContain("origin", schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain(typeof(MissionJson).Assembly.GetReferencedAssemblies(),
            a => a.Name!.Contains("Schema", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each <c>$ref</c> of the schema resolves as a JSON Pointer (RFC 6901), because each definition name has
    /// only letters, digits, '_', '.' and '-'. A '/' in a name would split the path. The schema has no problem of the
    /// subset that the tests read, and a sample batch fits it.</summary>
    [Fact]
    public void EachReferenceOfTheSchemaResolvesAndASampleBatchFits()
    {
        var schema = MissionJson.Schema();
        var sample = JsonNode.Parse($$"""
            {
              "label": "Add a delay",
              "commands": [
                { "kind": "add_trigger", "script": "msn:{{Script.Replace(@"\", @"\\")}}", "nickname": "ol_delay",
                  "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } },
                  "actions": [ { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } } ] },
                { "kind": "set_item_key", "item": "sec:{{Script.Replace(@"\", @"\\")}}#MsnShip/ol_ship_1", "key": "npc",
                  "lines": [ { "key": "npc", "texts": [ "ol_npc_shared" ] } ] }
              ]
            }
            """);

        Assert.All(schema["$defs"]!.AsObject(), d => Assert.Matches("^[A-Za-z0-9_.-]+$", d.Key));
        Assert.Empty(SchemaCheck.SchemaProblems(schema));
        Assert.Empty(SchemaCheck.Problems(schema, sample));
        Assert.NotEmpty(SchemaCheck.Problems(schema, JsonNode.Parse("""{ "label": "x", "commands": [ { "kind": "launch_rocket" } ] }""")));
    }

    /// <summary>A field that the batch or its options do not have refuses, so a typo cannot turn off a guard. The host
    /// sets the origin, so a request cannot.</summary>
    [Fact]
    public void AnUnknownFieldOfTheBatchOrOfItsOptionsRefuses()
    {
        var origin = Assert.Throws<EditException>(() =>
            MissionJson.ReadBatch("""{ "label": "x", "origin": "recovery", "commands": [] }""")).Error;
        var typo = Assert.Throws<EditException>(() =>
            MissionJson.ReadBatch("""{ "label": "x", "options": { "reject_new_error": true }, "commands": [] }""")).Error;

        Assert.Equal((EditErrorKind.InvalidArgument, MissionErrors.UnknownField, "origin"), (origin.Kind, origin.Detail, origin.Field));
        Assert.Equal("The batch has no field 'origin'. Remove the field, or correct its name.", origin.Message);
        Assert.Equal((MissionErrors.UnknownField, "reject_new_error"), (typo.Detail, typo.Field));
        Assert.Equal("The options of the batch have no field 'reject_new_error'. Remove the field, or correct its name.",
            typo.Message);
    }

    /// <summary>A JSON error tells the place of the value in plain words, without the text of the exception.</summary>
    [Fact]
    public void AJsonErrorTellsThePlaceInPlainWords()
    {
        var type = Assert.Throws<EditException>(() => MissionJson.ReadBatch("""{ "label": 5, "commands": [] }""")).Error;
        var reference = Assert.Throws<EditException>(() =>
            MissionJson.ReadCommand("""{ "kind": "set_trigger_start", "trigger": 5, "starts": true }""")).Error;

        Assert.Equal(MissionErrors.BadJson, type.Detail);
        Assert.Equal("The request is not a valid mission batch. The value at $.label on line 1 does not have the form of a " +
                     "mission batch. Correct the JSON.", type.Message);
        Assert.DoesNotContain("LineNumber", type.Message);
        Assert.Equal("The request is not a valid mission batch. An item of a mission command is a reference text, for " +
                     @"example trg:DATA\MISSIONS\M02\m02.ini#ol_trigger. Correct the JSON.", reference.Message);
    }
}
