using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing.Assets;

namespace OpenLancer.Data.Editing.Scene;

public static partial class ScenePlans
{
    /// <summary>
    /// Plans a new static prop archetype and an object that uses it (decisions D94, D278 and D279 of the M23 design), as
    /// one entry of the edit history "Create &lt;object&gt; from &lt;source&gt;". The archetype is a new <c>[Solar]</c>
    /// at the end of the first file of the solar archetypes, with the model lines of the source. The object comes from the
    /// object builder for the template "Other solar". One Undo removes both.
    /// </summary>
    /// <remarks>The loaded game data does not have the new archetype, so the Scene shows the object after the save and
    /// the reload of the game data (decisions D248 and D282). <see cref="ScenePlan.NewArchetype"/> names it.</remarks>
    /// <param name="source">The model of a ship, an asteroid or a model file.</param>
    /// <param name="stem">The nickname of the ship or the asteroid, or the file name of the model.</param>
    /// <param name="parent">The object of a Hierarchy row that gets the new object as a child, or null.</param>
    public static ScenePlan CreateProp(EditSession session, string system, ModelSource source, string stem, Vector3 position,
        SectionRef? parent)
    {
        var plan = new ScenePlan(SceneAction.Create, "Create");
        if (SystemFileProblem(session, system, out var path) is { } fileProblem)
            return Refused(plan, fileProblem);
        // The new archetype goes into the first file of the solar archetypes (decision D279).
        if (session.DataFiles.Paths("solar").FirstOrDefault() is not { } solarPath ||
            session.OpenDocument(solarPath) is not { } solarFile)
            return Refused(plan, Diagnostic.Error(path, "The edit view has no file of the solar archetypes."));
        if ((session.ReadOnlyReason ?? solarFile.ReadOnlyReason) is { } reason)
            return Refused(plan, Diagnostic.Error(solarPath, $"{SceneSections.FileName(solarPath)}: {reason}"));

        var archetype = PropArchetypes.Nickname(session, stem);
        if (PropArchetypes.Lines(source, archetype, session.IsBinaryFile(solarPath), out var error) is not { } solar)
            return Refused(plan, Diagnostic.Error(solarPath, error!));
        var stemOfObject = $"{system}_{TemplateId(SceneTemplate.Solar)}".ToLowerInvariant();
        if (NewObject(session, path, stemOfObject, position, archetype, parent, null, null, null, out error) is not { } made)
            return Refused(plan, Diagnostic.Error(path, error!));

        var added = new SectionRef(path, new SectionIdentity(SceneSections.ObjectType, made.Nickname));
        var newArchetype = new SectionRef(solarPath, new SectionIdentity("Solar", archetype));
        plan.Label = $"Create {made.Nickname} from {stem}";
        plan.Adds = [newArchetype, added];
        plan.SelectAfter = added;
        plan.NewArchetype = newArchetype;
        plan.Warnings = DanglingWarnings(session, made.Nickname, path);
        plan.Command = EditCommand.Group(plan.Label,
            [EditCommand.AddSection(solarPath, "Solar", solar), EditCommand.AddSection(path, SceneSections.ObjectType, made.Entries)],
            session.Snapshot());
        return plan;
    }
}
