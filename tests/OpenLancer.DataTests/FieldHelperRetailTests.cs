using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data.IO;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;
using OpenLancer.Tests;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the Inspector field helpers (section 13.2 of the M20 design): a label drag and a pick on Bw05 write
/// the numbers of a gizmo drag, each retail vector value keeps its exact start, and each retail color line that the
/// picker takes comes back from its start color. The tests count the retail colors that have no picker.
/// </summary>
public sealed class FieldHelperRetailTests(ITestOutputHelper output)
{
    private const string Zone = "Zone_Bw05_to_Ku04";
    private const string Light = "Bw05_system_light";

    static FieldHelperRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>A label drag of X by 100 points of the Position step moves the zone by 1,000. The patch line has three
    /// integers, and the next session reads the moved zone.</summary>
    [Fact]
    public void ALabelDragOfTheXOfAZoneSavesIntegers()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m20proof");
        var session = workspace.Open();
        var zone = Section(session, "zone", Zone);
        Assert.Equal("-12184, 0, -36217", Line(session, zone, "pos"));

        var field = FieldBinding.Create(session, zone, session.Get(zone)!, "pos");
        Assert.Equal(DragKind.Position, FieldDrag.KindOf(field.Key, zone.Id.Type));
        using (var drag = TestGestures.Number(session, field, 3))
        {
            foreach (var units in new[] { 10m, 55m, 100m })
                drag.Change(FieldDrag.DragText(DragKind.Position, drag.StartText, 3, 0, units));
            Assert.Null(drag.End(keep: true, interactive: true));
        }

        var patch = Save(workspace, session, zone, "pos = -11184, 0, -36217");
        session.Dispose();
        var reloaded = workspace.Open();
        Assert.Equal("-11184, 0, -36217", Line(reloaded, zone, "pos"));
        Assert.All(reloaded.Get(zone)!.Lines("pos")[0].Values, v => Assert.IsType<Int32Value>(v));
        Assert.True(File.Exists(patch));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>A pick of orange on the system light writes three integers. The picker gives the numbers from its
    /// color, so the test picks with the color of 255, 128, 0.</summary>
    [Fact]
    public void APickOnTheSystemLightSavesItsColor()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m20proof");
        var session = workspace.Open();
        var light = Section(session, "LightSource", Light);
        Assert.Equal("120, 250, 255", Line(session, light, "color"));

        var field = FieldBinding.Create(session, light, session.Get(light)!, "color");
        var form = ColorForms.Of(field.Key)!.Value;
        Assert.Equal(ColorForm.Byte, form);
        using (var pick = TestGestures.Number(session, field, 3))
        {
            Assert.True(FieldDrag.TryParse(pick.StartText, 3, out var start));
            var startColor = ColorForms.ColorOf(start, form);
            var orange = ColorForms.ColorOf([255, 128, 0], form);
            pick.Change(FieldDrag.Text(ColorForms.FromPicker(orange, startColor, start, form)));
            Assert.Null(pick.End(keep: true, interactive: true));
        }

        Save(workspace, session, light, "color = 255, 128, 0");
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// Each retail value of <c>pos</c>, <c>rotate</c>, <c>size</c>, <c>spin</c> and <c>attenuation</c> has an exact
    /// start: the decimal of its text gives the float back bit for bit. So a change of 0 and a change back give the
    /// start, and a step of 10 on a position gives exactly the value plus 10.
    /// </summary>
    [Fact]
    public void EachRetailVectorValueHasAnExactStart()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m20proof");
        var session = workspace.Open();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var notNumbers = 0;
        foreach (var path in RetailChecks.SystemFiles(session).Skip(1))
        {
            foreach (var section in session.Sections(path))
            {
                var view = session.Get(section)!;
                foreach (var key in (string[])["pos", "rotate", "size", "spin", "attenuation"])
                {
                    foreach (var line in view.Lines(key))
                    {
                        foreach (var value in line.Values)
                        {
                            if (!TransformValues.TryNumber(value, out var number))
                            {
                                notNumbers++;
                                continue;
                            }

                            counts[key] = counts.GetValueOrDefault(key) + 1;
                            var exact = FieldDrag.Exact(number);
                            Assert.True(exact != null, $"{section}: {key} {number:R}");
                            var back = float.Parse(exact.Value.ToString(CultureInfo.InvariantCulture),
                                CultureInfo.InvariantCulture);
                            Assert.Equal(Bits(number), Bits(back));
                            if (key == "pos")
                                Assert.Equal(Bits((float)((double)number + 10)), Bits(FieldDrag.Value(DragKind.Position, number, 10)));
                        }
                    }
                }
            }
        }

        output.WriteLine($"{string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}, not numbers {notNumbers}");
        Assert.Equal("attenuation 135, pos 26982, rotate 17487, size 9587, spin 189",
            string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        Assert.Equal(0, notNumbers);
    }

    /// <summary>
    /// Each retail color line of the system files that the picker takes comes back from its start color: the numbers
    /// are the start numbers, so the line keeps its start objects. The test counts the lines without a picker and
    /// their reasons.
    /// </summary>
    [Fact]
    public void EachRetailColorLineComesBackFromItsStartColor()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m20proof");
        var session = workspace.Open();
        var keys = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lines = 0;
        foreach (var path in RetailChecks.SystemFiles(session))
        {
            foreach (var section in session.Sections(path))
            {
                var view = session.Get(section)!;
                foreach (var name in view.Entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (ColorForms.Of(session.Schema(section, name)) is not { } form)
                        continue;
                    foreach (var line in view.Lines(name))
                    {
                        lines++;
                        keys[name] = keys.GetValueOrDefault(name) + 1;
                        var (color, reason) = ColorForms.ToPicker(line.Values, form);
                        if (reason != null)
                        {
                            var kind = reason[(reason.IndexOf(' ', reason.IndexOf(' ') + 1) + 1)..];
                            reasons[kind] = reasons.GetValueOrDefault(kind) + 1;
                            output.WriteLine($"No picker: {section} {name} = {FieldEdit.FormatLine(line.Values)}: {reason}");
                            continue;
                        }

                        Assert.Null(FieldDrag.NumbersOf(line.Values, 3, out var start));
                        var numbers = ColorForms.FromPicker(color!.Value, color.Value, start, form);
                        Assert.Equal(start, numbers);
                        var values = TransformValues.Line(line.Values, numbers);
                        Assert.True(values.Zip(line.Values).All(p => ReferenceEquals(p.First, p.Second)),
                            $"{section} {name}");
                    }
                }
            }
        }

        output.WriteLine($"{lines} color lines: {string.Join(", ", keys.Select(k => $"{k.Key} {k.Value}"))}");
        output.WriteLine($"Without a picker: {string.Join(", ", reasons.Select(r => $"{r.Key} ({r.Value})"))}");
        // Each retail color line of a system file is in the 0 to 255 range, so each one has a picker.
        Assert.Equal(538, lines);
        Assert.Empty(reasons);
    }

    /// <summary>
    /// The seven cutscene color rows over the retail corpus: the test counts the values that the picker takes and the
    /// values with a reason, for example a light color above 1 (decision D103). Every value is a list of three
    /// finite numbers, and every value is inside the range of its form.
    /// </summary>
    [Fact]
    public void TheRetailCutsceneColorsThatThePickerTakes()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var rows = ThnSchema.Rows.Where(r => r.Color != null).ToList();
        Assert.Equal(7, rows.Count);
        var taken = rows.ToDictionary(r => r, _ => 0);
        var refused = rows.ToDictionary(r => r, _ => 0);
        var notNumbers = 0;
        var largest = rows.ToDictionary(r => r, _ => 0f);

        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            var model = ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var (kind, table) in model.Entities.Select(e => (ThnElementKind.Entity, e.Table))
                         .Concat(model.Events.Select(e => (ThnElementKind.Event, e.Table))))
            {
                var type = ThnSchema.TypeOf(kind, table, evaluator.Symbols);
                foreach (var row in rows.Where(r => r.Element == kind && r.Types!.Contains(type)))
                {
                    if (ThnElementPaths.Get(kind, table, row.Path) is not { } value)
                        continue;
                    if (!FieldDrag.TryParse(ThnFieldEdit.Format(ThnKeyKind.Vector, value), 3, out var numbers))
                    {
                        notNumbers++;
                        output.WriteLine($"Not 3 numbers: {file} {row.Path} = {ThnWriter.Value(value)}");
                        continue;
                    }

                    largest[row] = Math.Max(largest[row], numbers.Max());
                    if (ColorForms.ToPicker(numbers, row.Color!.Value).Reason == null)
                        taken[row]++;
                    else
                        refused[row]++;
                }
            }
        }

        var report = new StringBuilder();
        foreach (var row in rows)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{row.Types![0]} {row.Path} ({row.Color}): {taken[row]} with a picker, {refused[row]} outside the " +
                $"range, largest value {largest[row]}");
        }

        output.WriteLine(report.ToString());
        Assert.Equal(0, notNumbers);
        // No retail cutscene color is outside the range of its form, so decision D103 refuses no retail value.
        Assert.Equal([3671, 3671, 1505, 7, 562, 288, 262], rows.Select(r => taken[r]));
        Assert.All(refused.Values, count => Assert.Equal(0, count));
    }

    private static SectionRef Section(EditSession session, string type, string nickname) =>
        new(session.SystemPath("Bw05")!, new SectionIdentity(type, nickname));

    private static string Line(EditSession session, SectionRef section, string key) =>
        FieldEdit.FormatLine(Assert.Single(session.Get(section)!.Lines(key)).Values);

    /// <summary>Saves the one edit, and checks that the patch holds the section with its nickname and the new line,
    /// with CRLF. Returns the patch file.</summary>
    private string Save(RetailWorkspace workspace, EditSession session, SectionRef section, string line)
    {
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Empty(saved.Problems);
        var patch = Assert.Single(saved.Written);
        Assert.Equal(@"patches\DATA\Universe\Systems\Bw05\Bw05.ini", Path.GetRelativePath(workspace.ModFolder, patch));
        var text = File.ReadAllText(patch);
        output.WriteLine(text);
        Assert.Equal($"[{section.Id.Type}]\r\nnickname = {section.Id.Key}\r\n{line}\r\n", text, ignoreCase: true);
        return patch;
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
