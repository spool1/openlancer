using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>One section that a template plan adds: a copy of a section of the edit view.</summary>
/// <param name="Source">The section that the plan copies.</param>
/// <param name="Copy">The new section.</param>
/// <param name="Children">The number of child sections that the plan copies with the section.</param>
public sealed record TemplateCopy(SectionRef Source, SectionRef Copy, int Children);

/// <summary>What <see cref="ItemTemplates.Build"/> adds for a new item, or why it cannot add it.</summary>
public sealed class TemplatePlan
{
    internal TemplatePlan(SectionRef template, string nickname, IReadOnlyList<TemplateCopy> copies,
        IReadOnlyList<Diagnostic> problems, IReadOnlyList<EditCommand> commands, Func<bool> current)
    {
        Template = template;
        Nickname = nickname;
        Copies = copies;
        Problems = problems;
        Commands = commands;
        Current = current;
    }

    /// <summary>The ship or the equipment that the new item copies.</summary>
    public SectionRef Template { get; }

    /// <summary>The nickname of the new item.</summary>
    public string Nickname { get; }

    /// <summary>The sections that the plan adds, in the order of the edit.</summary>
    public IReadOnlyList<TemplateCopy> Copies { get; }

    /// <summary>Why the plan cannot add the item. <see cref="ItemTemplates.Build"/> needs an empty list.</summary>
    public IReadOnlyList<Diagnostic> Problems { get; }

    internal IReadOnlyList<EditCommand> Commands { get; }

    /// <summary>True while the edit view and the text are as the plan read them.</summary>
    internal Func<bool> Current { get; }
}

/// <summary>
/// Makes a new item from a template: a copy of a ship or of equipment, with each section that belongs to it.
/// </summary>
/// <remarks>
/// <para>A ship copies the <c>[Ship]</c>, the hull good that the engine uses (the last one), and the packages of
/// that hull. Equipment copies its section, its good, and the munition that <c>projectile_archetype</c> names, with
/// the good of the munition. Each section gets its child sections too, for example the <c>[CollisionGroup]</c>
/// sections of a ship (decision D1: the patch format has no template directive).</para>
/// <para>Each copy goes into the file of the section that it copies, because a patch cannot change the file list of
/// <c>freelancer.ini</c>. The copies get these nicknames: the ship or the equipment and its good get the new
/// nickname. The hull good gets <c>&lt;new&gt;_hull</c>, a package <c>&lt;new&gt;_package</c> (then
/// <c>_package_2</c> and on), and a munition and its good <c>&lt;new&gt;_ammo</c>.</para>
/// <para>A copy that names another copied section names the new copy (review finding F12): the
/// <c>projectile_archetype</c> of a gun, the <c>equipment</c> of a good, the <c>ship</c> of a hull good and the
/// <c>hull</c> of a package.</para>
/// <para>A copy keeps a text id of the base data. A text of a mod gets a copy in the text of the active mod, with a
/// key from the nickname of the copy (see <see cref="ItemEdits.NewTextKeys"/>), and the line of the copy gets its
/// placeholder. This applies to a text placeholder of the active mod, so that a text edit of the copy does not
/// change the template, and to the id of a text of a lower mod, because that id changes when a mod adds text (rule
/// S8). Copies of sections that share a text share the copy of the text.</para>
/// <para>A binary file stores a new boolean as the text <c>true</c> or <c>false</c>, so a copy writes each bool
/// string of a binary file in lowercase. The engine reads both forms as the same boolean.</para>
/// </remarks>
public static class ItemTemplates
{
    /// <summary>Plans a new item. The plan copies the sections and the text as the edit view has them now, so build
    /// it and run the edit at once. The edit fails when it runs after the edit view or the text changed.</summary>
    public static TemplatePlan Plan(EditSession session, SectionRef template, string newNickname)
    {
        var index = session.Items;
        var problems = new List<Diagnostic>();
        void Problem(string message) =>
            problems.Add(Diagnostic.Error(template.Path, $"{template.Id}: {message}") with { Target = template });
        TemplatePlan Refused() => new(template, newNickname, [], problems, [], session.Snapshot());

        var parts = Parts(index, template, newNickname, Problem);
        var planned = new HashSet<(ItemFileKind, string)>();
        foreach (var part in parts)
        {
            if (PatchFormatter.KeyError(part.Nickname, index.IsBinary(part.Source)) is { } error)
                Problem(error);
            else if (!planned.Add((part.Kind, part.Nickname.ToLowerInvariant())) ||
                     index.Named(part.Kind, part.Nickname).Count > 0)
                Problem($"another section of the {part.Kind.ToString().ToLowerInvariant()} files has the nickname " +
                        $"{part.Nickname}.");
        }
        if (problems.Count > 0)
            return Refused();

        var renamed = new Dictionary<(ItemFileKind, string), string>();
        foreach (var part in parts)
            renamed.TryAdd((part.Kind, part.Source.Id.Key!.ToLowerInvariant()), part.Nickname);
        var texts = new SectionCopies(session, Problem);
        var copies = new List<TemplateCopy>();
        var commands = new List<EditCommand>();
        foreach (var part in parts)
        {
            var binary = index.IsBinary(part.Source);
            var section = index.Section(part.Source)!;
            var copy = new SectionRef(part.Source.Path, new SectionIdentity(section.Name, part.Nickname));
            commands.Add(EditCommand.AddSection(part.Source.Path, section.Name,
                Entries(section, binary, (entry, i) => Rewrite(entry, i, part, renamed, texts))));
            var children = index.Children(part.Source);
            foreach (var child in children)
            {
                // A child has no nickname, so the key of a copied text comes from the nickname of the new parent.
                var keys = new SectionIdentity(child.Name, part.Nickname);
                commands.Add(EditCommand.AddSection(part.Source.Path, child.Name,
                    Entries(child, binary, (entry, i) => texts.Copy(entry, i, keys)), copy));
            }
            copies.Add(new TemplateCopy(part.Source, copy, children.Count));
        }
        if (problems.Count > 0)
            return Refused();
        commands.AddRange(texts.Commands);
        return new TemplatePlan(template, newNickname, copies, [], commands, session.Snapshot());
    }

    /// <summary>The edit that adds the sections of a plan, as one entry of the edit history.</summary>
    /// <exception cref="InvalidOperationException">The plan has problems, or the edit view or the text changed after
    /// the plan.</exception>
    public static EditCommand Build(TemplatePlan plan)
    {
        if (plan.Problems.Count > 0)
            throw new InvalidOperationException(plan.Problems[0].Message);
        if (!plan.Current())
            throw new InvalidOperationException($"{plan.Template.Id}: the edit view changed after the plan of " +
                                                $"{plan.Nickname}. Plan the new item again.");
        return EditCommand.Group($"New {plan.Nickname} from {plan.Template.Id.Key}", plan.Commands, plan.Current);
    }

    /// <summary>The sections that the new item copies, each with its new nickname, in the order of the edit.</summary>
    private static List<Part> Parts(ItemIndex index, SectionRef template, string nickname, Action<string> problem)
    {
        var kind = index.KindOf(template.Path);
        var section = index.Section(template);
        if (section == null || template.Id.Key == null ||
            !(kind == ItemFileKind.Ships && Is(section.Name, "Ship") || kind == ItemFileKind.Equipment))
        {
            problem("a template must be a [Ship] or an equipment section with a nickname.");
            return [];
        }
        var parts = new List<Part>();
        if (kind == ItemFileKind.Ships)
        {
            parts.Add(new Part(template, ItemFileKind.Ships, nickname));
            // The engine takes the price and the icon of a ship from its last hull good.
            if (index.Related(template, ItemRelation.Hull) is [.., var hull])
            {
                parts.Add(new Part(hull, ItemFileKind.Goods, nickname + "_hull"));
                var packages = index.Related(hull, ItemRelation.Package);
                parts.AddRange(packages.Select((p, i) =>
                    new Part(p, ItemFileKind.Goods, nickname + "_package" + (i == 0 ? "" : $"_{i + 1}"))));
            }
            return parts;
        }
        var munitions = index.Related(template, ItemRelation.Munition).Where(m => !m.Equals(template)).ToList();
        if (munitions.Count > 1)
            problem($"{munitions.Count} sections have the nickname of its projectile_archetype.");
        foreach (var munition in munitions.Take(1))
        {
            parts.Add(new Part(munition, ItemFileKind.Equipment, nickname + "_ammo"));
            parts.AddRange(Goods(index, munition, nickname + "_ammo", problem));
        }
        parts.Add(new Part(template, ItemFileKind.Equipment, nickname));
        parts.AddRange(Goods(index, template, nickname, problem));
        return parts;
    }

    /// <summary>The good of equipment, when it has one.</summary>
    private static IEnumerable<Part> Goods(ItemIndex index, SectionRef equipment, string nickname, Action<string> problem)
    {
        var goods = index.Related(equipment, ItemRelation.Good);
        if (goods.Count > 1)
            problem($"{goods.Count} goods have the nickname {equipment.Id.Key}.");
        return goods.Take(1).Select(g => new Part(g, ItemFileKind.Goods, nickname));
    }

    /// <summary>The value of a line of a copy: the new nickname in the key entry, the new nickname of each copied
    /// section that a link names, and the copy of a text of a mod.</summary>
    private static ValueBase Rewrite(Entry entry, int i, Part part, Dictionary<(ItemFileKind, string), string> renamed,
        SectionCopies texts)
    {
        var value = entry[i];
        if (i != 0)
            return value;
        if (Is(entry.Name, SectionKeys.For(entry.Section.Name).KeyEntry ?? ""))
            return new StringValue(part.Nickname);
        foreach (var (name, target) in ItemIndex.Links(part.Kind))
        {
            if (Is(entry.Name, name) && renamed.TryGetValue((target, value.ToString().ToLowerInvariant()), out var copy))
                return new StringValue(copy);
        }
        return texts.Copy(entry, i, new SectionIdentity(entry.Section.Name, part.Nickname));
    }

    /// <summary>The lines of a copy of a section.</summary>
    /// <param name="rewrite">Gives value i of a line.</param>
    private static List<EntryView> Entries(Section section, bool binary, Func<Entry, int, ValueBase> rewrite) =>
        section.Select(entry => new EntryView(entry.Name,
            entry.Select((_, i) => SectionCopies.Stored(rewrite(entry, i), binary)).ToList())).ToList();

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <param name="Source">The section that the part copies.</param>
    /// <param name="Kind">The kind of its file.</param>
    /// <param name="Nickname">The nickname of the copy.</param>
    private sealed record Part(SectionRef Source, ItemFileKind Kind, string Nickname);
}
