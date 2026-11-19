using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Schema.Missions;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rule data of the mission studio (sections 14.4, 14.6 and 18.1 of the M6a design): each name of the engine has a
/// row, each help text passes the STE lint, each sentence names only values of its row, and the story slot table matches
/// the storyline of the engine.
/// </summary>
public sealed class MissionSchemaTests
{
    private static readonly MissionSchema Schema = MissionSchema.Retail;

    [Fact]
    public void EachNameOfTheForkEnumsHasARowAPlainNameAndAGroup()
    {
        foreach (var action in Enum.GetValues<TriggerActions>())
        {
            var name = Schema.Name(action.ToString());
            Assert.True(name is { Kind: MissionNameKind.Action }, $"{action} has no action row.");
            Assert.False(string.IsNullOrWhiteSpace(name.Plain), $"{action} has no plain name.");
            Assert.True(Schema.Group(name.Group) != null, $"{action} has no group.");
        }
        foreach (var condition in Enum.GetValues<TriggerConditions>())
        {
            var name = Schema.Name(condition.ToString());
            Assert.True(name is { Kind: MissionNameKind.Condition }, $"{condition} has no condition row.");
            Assert.False(string.IsNullOrWhiteSpace(name.Plain), $"{condition} has no plain name.");
            Assert.True(Schema.Event(name.Group) != null, $"{condition} has no event.");
        }
        foreach (var command in Enum.GetValues<ObjListCommands>())
            Assert.True(Schema.Directive(command.ToString()) != null, $"{command} has no order row.");

        Assert.Equal(Enum.GetValues<TriggerActions>().Length, Schema.Actions.Count);
        Assert.Equal(Enum.GetValues<TriggerConditions>().Length, Schema.Conditions.Count);
        Assert.Equal(129, Schema.Actions.Count + Schema.Conditions.Count);
    }

    [Fact]
    public void EachHelpTextPassesTheSteLint()
    {
        var problems = Texts().SelectMany(t => Lint(t.Text).Select(p => $"{t.Where}: {p}: {t.Text}")).ToList();

        Assert.Empty(problems);
    }

    /// <summary>The rule data holds the steps of the completeness panels of section 13.5 of the M6a design, in order.
    /// So the providers of the data layer make each report (section 11.4).</summary>
    [Fact]
    public void TheCompletenessStepsOfTheItemsAreRuleData()
    {
        string[] Steps(string item) => Schema.Completeness[item].Steps.Select(s => s.Name).ToArray();

        Assert.Equal(["Start trigger", "Mission success", "Fail texts", "One condition for each trigger", "Names resolve",
            "Each trigger can start", "Cutscenes end"], Steps("mission"));
        Assert.Equal(["Pilot and voice", "Ship build", "Archetype, loadout and pilot"], Steps("missionShip"));
        Assert.Equal(["Comm lines exist", "Speakers in the system"], Steps("conversation"));
        Assert.Equal(["Cutscene file", "Anchor", "Camera back"], Steps("cutsceneLink"));
        Assert.Equal(["Scene file", "Path length"], Steps("baseSceneLink"));
        Assert.Equal("shipBuildParts", Schema.Completeness["missionShip"].Step("buildParts").Check);
    }

    /// <summary>The classes of a ship build are one row each, from the values of one line (UI rule 3). The rule data
    /// holds the list and its help text (decision D539).</summary>
    [Fact]
    public void TheClassesOfAShipBuildAreAListOfTheValuesOfOneLine()
    {
        var spec = Schema.KeyList("NPCShipArch", "npc_class")!;

        Assert.Equal((ListFieldCodec.ValueList, "NPC classes"), (spec.Codec, spec.Label));
        Assert.False(string.IsNullOrEmpty(spec.Help));
        var model = ListFieldModel.Read(spec, [new ListFieldLine("npc_class",
            [new LibreLancer.Data.Ini.StringValue("lawful"), new LibreLancer.Data.Ini.StringValue("d5")])]);
        Assert.Equal(2, model.Rows.Count);
    }

    [Fact]
    public void TheLintFindsTheBrokenRules()
    {
        Assert.NotEmpty(Lint("Do this; do that."));
        Assert.NotEmpty(Lint("The game doesn't read it."));
        Assert.NotEmpty(Lint("Make sure that it's done."));
        Assert.NotEmpty(Lint("Use the robust value."));
        Assert.NotEmpty(Lint(string.Join(' ', Enumerable.Repeat("word", 26)) + "."));
        Assert.Empty(Lint("The game reads the attitude of the player. It sets it to -0.2."));
    }

    [Fact]
    public void EachSentenceNamesOnlyValuesOfItsRow()
    {
        foreach (var name in Schema.Conditions.Concat(Schema.Actions).Concat(Schema.Directives))
        {
            foreach (var sentence in name.Sentences.Concat(name.Suffixes))
            {
                foreach (var value in sentence.When.Keys.Concat(MissionSchema.Placeholders(sentence.Text)))
                    Assert.True(name.Value(value) != null, $"A sentence of {name.Name} names '{value}'.");
            }
        }
    }

    [Fact]
    public void ASentenceThatNamesAnotherValueDoesNotLoad()
    {
        var root = JsonNode.Parse(ResourceText())!;
        var condition = root["conditions"]!.AsArray().First(c => c!["sentences"] is JsonArray { Count: > 0 })!;
        condition["sentences"]![0]!["text"] = "{no_such_value} happens.";

        var error = Assert.Throws<InvalidDataException>(() => MissionSchema.Parse(root.ToJsonString()));
        Assert.Contains("no_such_value", error.Message);
    }

    /// <summary>The inside test of a distance condition is strict (OL-0078). So a ship exactly at the radius is not
    /// inside. The sentences say "closer than" and "farther than", as the help of the side does.</summary>
    [Theory]
    [InlineData("Cnd_DistShip", "inside", "Starts when ol_ship is closer than 1000 m to ol_other.")]
    [InlineData("Cnd_DistShip", "outside", "Starts when ol_ship is farther than 1000 m from ol_other.")]
    [InlineData("Cnd_DistVec", "inside", "Starts when ol_ship is closer than 1000 m to a point.")]
    [InlineData("Cnd_DistVec", "outside", "Starts when ol_ship is farther than 1000 m from a point.")]
    public void TheSentenceOfADistanceConditionTellsItsStrictSide(string condition, string side, string expected)
    {
        var values = new Dictionary<string, string>
        {
            ["side"] = side, ["ship"] = "ol_ship", ["other"] = "ol_other", ["range"] = "1000", ["radius"] = "1000",
        };

        Assert.Equal(expected, Schema.Name(condition)!.Sentence(values.GetValueOrDefault));
    }

    /// <summary>The Inspector does not show the sentences of the label distance condition yet. They tell the strict side
    /// too, so that they are correct when the Inspector shows them.</summary>
    [Fact]
    public void TheSentencesOfTheLabelDistanceConditionTellTheirSide()
    {
        var name = Schema.Name("Cnd_DistVecLbl")!;

        Assert.False(name.Checked);
        Assert.Collection(name.Sentences,
            inside =>
            {
                Assert.Equal(["inside"], inside.When["side"]);
                Assert.Contains("are closer than {radius} m to a point", inside.Text);
            },
            outside =>
            {
                Assert.Empty(outside.When);
                Assert.Contains("are farther than {radius} m from a point", outside.Text);
            });
    }

    /// <summary>The start role of each action (section 5.3, step 9 of the M6c design): one role of the closed set for each
    /// of the 85 actions, and none for a condition or an order.</summary>
    [Fact]
    public void EachActionHasOneStartRoleOfTheClosedSet()
    {
        Assert.Equal(85, Schema.Actions.Count);
        foreach (var action in Schema.Actions)
            Assert.True(action.Start is { } role && Enum.IsDefined(role), action.Name);
        Assert.All(Schema.Conditions.Concat(Schema.Directives), n => Assert.Null(n.Start));
        Assert.Equal(StartRole.World, Schema.Name("Act_SpawnFormation")!.Start);
        Assert.Equal(StartRole.World, Schema.Name("Act_PlayMusic")!.Start);
        Assert.Equal(StartRole.State, Schema.Name("Act_SetRep")!.Start);
        Assert.Equal(StartRole.State, Schema.Name("Act_LockDock")!.Start);
        Assert.Equal(StartRole.Save, Schema.Name("Act_AddRTC")!.Start);
        Assert.Equal(StartRole.Place, Schema.Name("Act_SetInitialPlayerPos")!.Start);
        Assert.Equal(StartRole.Skip, Schema.Name("Act_ChangeState")!.Start);
        Assert.Equal(StartRole.Skip, Schema.Name("Act_SendComm")!.Start);
    }

    /// <summary>The 11 conditions that write a state line after the trigger id into a save (section 2.4, item 6 of the
    /// M6c design).</summary>
    [Fact]
    public void ElevenConditionsSaveAState()
    {
        string[] expected =
        [
            "Cnd_Timer", "Cnd_Destroyed", "Cnd_DistShip", "Cnd_DistVec", "Cnd_DistVecLbl", "Cnd_LootAcquired", "Cnd_ProjHit",
            "Cnd_ProjHitShipToLbl", "Cnd_TetherBroke", "Cnd_NPCSystemEnter", "Cnd_NPCSystemExit",
        ];

        Assert.Equal(expected.Order(), Schema.Conditions.Where(c => c.SavesState).Select(c => c.Name).Order());
        Assert.DoesNotContain(Schema.Actions, a => a.SavesState);
    }

    [Theory]
    [InlineData(null, "has no start role")]
    [InlineData("teleport", "start role 'teleport'")]
    public void AnActionWithoutAKnownStartRoleDoesNotLoad(string? role, string message)
    {
        var root = JsonNode.Parse(ResourceText())!;
        var action = root["actions"]![0]!.AsObject();
        action.Remove("start");
        if (role != null)
            action["start"] = role;

        var error = Assert.Throws<InvalidDataException>(() => MissionSchema.Parse(root.ToJsonString()));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void AConditionWithAStartRoleDoesNotLoad()
    {
        var root = JsonNode.Parse(ResourceText())!;
        root["conditions"]![0]!["start"] = "world";

        var error = Assert.Throws<InvalidDataException>(() => MissionSchema.Parse(root.ToJsonString()));
        Assert.Contains("Only an action has a start role", error.Message);
    }

    /// <summary>
    /// The slot table against the storyline of the engine (section 14.6 of the M6a design). The path of each slot is the
    /// file of its story mission. The start stage is the stage <c>{mission}_loaded</c>, or the stage after it when the
    /// game skips that stage. The accept stage is the stage <c>{mission}_accepted</c>. "At once" means that no free time
    /// stage without a skip comes before the next mission.
    /// </summary>
    [Fact]
    public void TheSlotTableMatchesTheForkStoryline()
    {
        var storyline = StoryStages.Default;
        var items = storyline.Items;
        // The engine storyline gives these free time stages no skip, although the game code starts the next mission at
        // once. MF1 item 15.9 corrects the storyline (decision D549).
        string[] knownGaps = ["freetime_06_07", "freetime_09_10", "freetime_10_11"];

        Assert.Equal(storyline.Missions.Count, Schema.StorySlots.Count);
        for (var i = 0; i < Schema.StorySlots.Count; i++)
        {
            var slot = Schema.StorySlots[i];
            var mission = storyline.Missions[i];
            Assert.Equal(mission.File, slot.Path, ignoreCase: true);
            var loaded = items.FindIndex(s => s.Nickname.Equals($"{mission.Nickname}_loaded", StringComparison.OrdinalIgnoreCase));
            var accepted = items.FindIndex(s => s.Nickname.Equals($"{mission.Nickname}_accepted", StringComparison.OrdinalIgnoreCase));
            Assert.True(loaded >= 0 && accepted > loaded, slot.Slot);
            Assert.Equal(items[loaded].Skip ? loaded + 1 : loaded, slot.StartStage);
            Assert.Equal(accepted, slot.AcceptStage);
            Assert.Equal(slot.Slot.Equals("M13", StringComparison.Ordinal), slot.Next.Kind == NextMissionKind.End);

            if (slot.Next.Kind == NextMissionKind.End)
                continue;
            var next = items.FindIndex(accepted, s => s.Mission != null && !s.Mission.Equals(mission.Nickname, StringComparison.OrdinalIgnoreCase));
            var freeTime = items.Skip(accepted + 1).Take(next - accepted - 1)
                .Where(s => !s.Skip && s.Mission == null && !knownGaps.Contains(s.Nickname)).ToList();
            Assert.True(slot.Next.Kind == NextMissionKind.AtOnce == (freeTime.Count == 0), slot.Slot);
        }
    }

    /// <summary>The rule data gives the list fields (decision D540, sections 11.1 and 14.4
    /// of the M6a design): the ship lines of a formation, the lines of a conversation, the orders of an order list and
    /// the ships of a condition. Each spec reads lines in the retail form.</summary>
    [Fact]
    public void TheSchemaGivesTheListFields()
    {
        var members = Schema.KeyList("MsnFormation", "ship")!;
        var lines = Schema.KeyList("Dialog", "line")!;
        var orders = Schema.OrderList("ObjList")!;
        var ships = Schema.ValueList("Cnd_NPCSystemEnter", "ships")!;

        Assert.Equal((ListFieldCodec.RepeatedKey, "Members", "ships"), (members.Codec, members.Label, members.Items));
        Assert.Equal((ListColumnKind.Reference, "mission_ship"), (members.Columns[0].Kind, members.Columns[0].List));
        Assert.Equal(["Speaker", "Listener", "Comm line"], lines.Columns.Select(c => c.Name));
        Assert.Equal("comm_line", lines.Columns[2].List);
        Assert.Equal((ListFieldCodec.MixedKeys, "Add order"), (orders.Codec, orders.AddLabel));
        Assert.Equal(Schema.Directives.Select(d => d.Name), orders.Keys);
        Assert.Equal("Fly to a point", orders.KeyLabel("GotoVec"));
        Assert.Equal([ListColumnKind.Token, ListColumnKind.Position, ListColumnKind.WholeNumber, ListColumnKind.Text],
            orders.ColumnsOf("GotoVec").Select(c => c.Kind));
        Assert.Equal((ListFieldCodec.ValueList, 1), (ships.Codec, ships.Prefix));
        Assert.NotNull(Schema.KeyList("MsnShip", "label"));
        Assert.NotNull(Schema.KeyList("MsnShip", "cargo"));
        Assert.Null(Schema.KeyList("MsnShip", "npc"));
        // Only an order list says "Add order".
        Assert.All(Schema.Lists.Where(l => l != orders), l => Assert.Null(l.AddLabel));

        var read = ListFieldModel.Read(orders, [new("GotoVec", Values("goto", "1", "2", "3", "100")),
            new("nickname", Values("ol1")), new("Delay", Values("5"))]);
        Assert.Equal(["GotoVec", "Delay"], read.Rows.Select(r => r.Key));
        var group = ListFieldModel.Read(ships, [new("Cnd_NPCSystemEnter", Values("sys_a", "s1", "s2"))]);
        Assert.Equal(["s1", "s2"], group.Rows.Select(r => r.Cell(0)));
    }

    /// <summary>The list fields of the behavior prefabs: a field of lines gives a mixed-keys spec with the columns of
    /// the schema, and a field of member triggers gives a list of triggers (section 14.5 of the M6a design).</summary>
    [Fact]
    public void ThePrefabListFieldsHaveTheirSpecs()
    {
        var spawn = MissionPrefabs.Retail.Definitions.Single(d => d.Id == "spawnWave");
        var ships = spawn.Fields.Single(f => f.Id == "ships");
        var ends = spawn.Fields.Single(f => f.Id == "endsWhen");
        var sides = spawn.Fields.Single(f => f.Id == "sides");

        var spec = ships.Spec(Schema)!;
        Assert.Equal((ListFieldCodec.MixedKeys, "ships"), (spec.Codec, spec.Items));
        Assert.Equal(["Act_SpawnShip", "Act_SpawnFormation"], spec.Keys);
        Assert.Equal("Spawn ship", spec.KeyLabel("Act_SpawnShip"));
        Assert.Equal(PrefabListCodec.Triggers, ends.List!.Codec);
        Assert.Null(ends.Spec(Schema));
        Assert.Contains("Act_SetVibe", sides.Spec(Schema)!.Keys);
        Assert.Null(spawn.Fields.Single(f => f.Id == "spawnWhen").List);
        Assert.All(MissionPrefabs.Retail.Definitions.SelectMany(d => d.Fields).Where(f => f.List is { Codec: PrefabListCodec.Lines }),
            f => Assert.NotEmpty(f.Spec(Schema)!.Columns));
    }

    /// <summary>The schema marks the fields that are hard to understand (section 11.3 of the
    /// M6a design).</summary>
    [Fact]
    public void TheSchemaMarksTheHardFields()
    {
        var trigger = Schema.Section("Trigger")!;

        Assert.True(trigger.Key("InitState")!.Hard);
        Assert.True(trigger.Key("repeatable")!.Hard);
        Assert.True(trigger.Key("repeatable")!.ValueSpecs[0].Hard);
        Assert.False(trigger.Key("nickname")!.Hard);
        Assert.True(Schema.Section("MsnShip")!.Key("rel_pos")!.Hard);
    }

    private static List<LibreLancer.Data.Ini.ValueBase> Values(params string[] texts) =>
        texts.Select(t => (LibreLancer.Data.Ini.ValueBase)new LibreLancer.Data.Ini.StringValue(t)).ToList();

    [Fact]
    public void EachSlotNoteHasAText()
    {
        foreach (var note in Schema.StorySlots.SelectMany(s => s.Notes))
            Assert.True(Schema.SlotNotes.ContainsKey(note), note);
    }

    private static string ResourceText()
    {
        using var stream = typeof(MissionSchema).Assembly.GetManifestResourceStream("OpenLancer.Data.Missions.mission-schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Each text of the rule data that the editor shows: the help texts, the plain names, the labels, the
    /// sentences, the slot notes and the texts of the prefabs.</summary>
    private static IEnumerable<(string Where, string Text)> Texts()
    {
        IEnumerable<(string, string)> Help(string where, HelpText help) =>
            new[] { help.Title, help.WhatItIs, help.WhatItWrites, help.Example }.OfType<string>().Select(t => (where, t));

        foreach (var (id, help) in Schema.Help)
        {
            foreach (var text in Help($"help {id}", help))
                yield return text;
        }
        foreach (var group in Schema.Groups)
        {
            yield return ($"group {group.Id}", group.Chip);
            foreach (var text in Help($"group {group.Id}", group.Help))
                yield return text;
        }
        foreach (var group in Schema.Events)
            yield return ($"event {group.Id}", group.Label);
        foreach (var (id, note) in Schema.SlotNotes)
            yield return ($"slot note {id}", note);
        foreach (var buffer in Schema.Buffers.Values)
            yield return ($"buffer {buffer.Id}", buffer.What);
        foreach (var name in Schema.Conditions.Concat(Schema.Actions).Concat(Schema.Directives))
        {
            yield return (name.Name, name.Plain);
            foreach (var value in name.Values)
            {
                yield return ($"{name.Name} {value.Name}", value.Label);
                if (value.Help != null)
                    yield return ($"{name.Name} {value.Name}", value.Help);
            }
            foreach (var sentence in name.Sentences.Concat(name.Suffixes))
                yield return ($"{name.Name} sentence", sentence.Text);
        }
        foreach (var (id, note) in Schema.Notes)
            yield return ($"note {id}", note);
        foreach (var (id, item) in Schema.Completeness)
        {
            yield return ($"completeness {id}", item.Title);
            foreach (var step in item.Steps)
            {
                yield return ($"completeness {id} {step.Id}", step.Name);
                yield return ($"completeness {id} {step.Id}", step.Reason);
            }
        }
        foreach (var section in Schema.Sections.Append(Schema.ShipBuild))
        {
            foreach (var key in section.Keys)
            {
                yield return ($"[{section.Type}] {key.Key}", key.Label);
                if (key.Help != null)
                    yield return ($"[{section.Type}] {key.Key}", key.Help);
                foreach (var value in key.Values)
                {
                    yield return ($"[{section.Type}] {key.Key} {value.Name}", value.Label);
                    if (value.Help != null)
                        yield return ($"[{section.Type}] {key.Key} {value.Name}", value.Help);
                }
            }
        }
        foreach (var prefab in MissionPrefabs.Retail.Definitions)
        {
            yield return ($"prefab {prefab.Id}", prefab.Name);
            foreach (var text in Help($"prefab {prefab.Id}", prefab.Help))
                yield return text;
            foreach (var member in prefab.Members)
                yield return ($"prefab {prefab.Id} {member.Role}", member.Label);
            yield return ($"prefab {prefab.Id} core", prefab.CoreLabel);
            foreach (var field in prefab.Fields)
            {
                yield return ($"prefab {prefab.Id} {field.Id}", field.Label);
                yield return ($"prefab {prefab.Id} {field.Id}", field.Help);
            }
            foreach (var step in prefab.Steps)
            {
                yield return ($"prefab {prefab.Id} {step.Id}", step.Name);
                yield return ($"prefab {prefab.Id} {step.Id}", step.Reason);
            }
        }
        foreach (var e in MissionPrefabs.Retail.Events)
            yield return ($"event {e.Id}", e.Label);
    }

    private static List<string> Lint(string text) => SteLint.Problems(text);
}
