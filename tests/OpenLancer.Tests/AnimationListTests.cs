using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The synthetic profile of <see cref="ThnDataWorkspace"/> with animation files: bodyparts.ini names body.anm, a file
/// that does not exist and more.anm in its <c>[Animations]</c> lines. more.anm has the walk of body.anm in other letters.
/// extra.anm and broken.anm exist, but no line names them. The script has a character and a COMPOUND with a START_MOTION
/// event each.
/// </summary>
internal static class AnimationWorkspace
{
    public const string Folder = @"DATA\CHARACTERS\ANIMATIONS";
    public const string Body = Folder + @"\body.anm";
    public const string More = Folder + @"\more.anm";
    public const string Extra = Folder + @"\extra.anm";
    public const string Broken = Folder + @"\broken.anm";

    public const string Script = """
        duration = 10
        entities = {
            { entity_name = "Man", type = DEFORMABLE, template_name = "trent" },
            { entity_name = "Ship", type = COMPOUND, template_name = "ship1", userprops = { category = "Spaceship" } },
            { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = 10 } }
        }
        events = {
            { 1, START_MOTION, { "Man" }, { animation = "Sc_Walk" } },
            { 2, START_MOTION, { "Ship" }, { animation = "Sc_loop" } }
        }
        """;

    /// <param name="modFiles">The animation files of the active mod: install path to script names.</param>
    public static TestWorkspace Create(params (string Path, string[] Names)[] modFiles)
    {
        var ws = ThnDataWorkspace.Create();
        ws.WriteBase(ThnDataWorkspace.BodypartsPath, """
            [Animations]
            anim = characters\animations\body.anm
            anim = characters\animations\gone.anm
            anim = characters\animations\more.anm
            [PetalAnimations]
            anim = characters\animations\body.anm
            [Skeleton]
            sex = male
            [Body]
            nickname = body1
            mesh = characters\body1.dfm
            """);
        Write(Path.Combine(ws.Base, Body), "Sc_Walk", "Sc_Run");
        Write(Path.Combine(ws.Base, More), "SC_WALK", "Sc_Sit");
        Write(Path.Combine(ws.Base, Extra), "Sc_Extra");
        ws.WriteBase(Broken, "not an animation file");
        ws.WriteBase(ScriptPath, Script);
        foreach (var (path, names) in modFiles)
            Write(Path.Combine(ws.ModFolder("act"), "files", path), names);
        return ws;
    }

    /// <summary>Writes an animation file with script names.</summary>
    public static void Write(string file, params string[] names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, AnimationFiles.Build(true, names).Bytes);
    }

    /// <summary>A command that adds an <c>[Animations]</c> section with one line.</summary>
    public static EditCommand AddLine(string value) => EditCommand.AddSection(ThnDataWorkspace.BodypartsPath, "Animations",
        [ThnDataWorkspace.Line("anim", value)]);
}

/// <summary>
/// The data list <c>animation</c> (#19, section 12.1 of the M8b design): the clips of the animation files that the
/// <c>[Animations]</c> lines of W of bodyparts.ini name, with a cache of the names of each file by its stamp on disk.
/// </summary>
public sealed class AnimationListTests
{
    /// <summary>
    /// The entries are the clips sorted by name. For a name in two files, the last file wins: the entry has its spelling
    /// and its file name, and the clip keeps both files. The list skips a file that does not exist, as the engine does,
    /// and the list is closed. The skeletons of bodyparts.ini come with the clips.
    /// </summary>
    [Fact]
    public void TheListHoldsTheClipsOfTheFilesAndTheLastFileWins()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");

        var list = session.Lists.List("animation")!;
        var data = session.Lists.Animations()!;

        Assert.Equal(("animation", "Animation", "Animation", true), (list.Id, list.Label, list.Kind, list.Closed));
        Assert.Equal([("Sc_Run", "body.anm"), ("Sc_Sit", "more.anm"), ("SC_WALK", "more.anm")],
            list.Entries.Select(e => (e.Value, e.Detail)));
        Assert.All(list.Entries, e => Assert.Equal((ValueOrigin.Base, (string?)null, (SectionRef?)null), (e.Origin, e.ModId, e.Section)));
        Assert.Same(list.Find("sc_walk"), list.Entries[2]);
        var walk = data.Clips.Single(c => c.Name == "SC_WALK");
        Assert.Equal([AnimationWorkspace.Body, AnimationWorkspace.More], walk.Archives, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(data.Warnings);
        var skeleton = Assert.Single(data.Skeletons);
        Assert.Equal([AnimationWorkspace.Body], skeleton.Archives, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("body1", skeleton.Bodyparts);
    }

    /// <summary>A whole animation file of the active mod gives the clips of the mod, with the origin of the active
    /// mod.</summary>
    [Fact]
    public void AnAnimationFileOfTheActiveModGivesItsClipsWithItsOrigin()
    {
        using var ws = AnimationWorkspace.Create((AnimationWorkspace.More, ["Sc_ModWalk"]));
        var session = ws.Session("act");

        var list = session.Lists.List("animation")!;

        Assert.Equal(["Sc_ModWalk", "Sc_Run", "Sc_Walk"], list.Entries.Select(e => e.Value));
        Assert.Equal((ValueOrigin.ActiveMod, "act"), (list.Find("Sc_ModWalk")!.Origin, list.Find("Sc_ModWalk")!.ModId));
        Assert.Equal(ValueOrigin.Base, list.Find("Sc_Run")!.Origin);
    }

    /// <summary>
    /// The list reads a file again only when its stamp on disk changes. A second build reads no file, also after a save
    /// gives a new edit view. A new line reads only its own file. A file with a new stamp is read again at the next new
    /// edit view.
    /// </summary>
    [Fact]
    public void TheCacheReadsAnAnimationFileOnlyWhenItsStampChanges()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal(3, session.Lists.List("animation")!.Entries.Count);
        Assert.Equal(2, session.Lists.AnimationReads);
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        AssertSaved(session.Save());
        Assert.Equal(3, session.Lists.List("animation")!.Entries.Count);
        Assert.Equal(2, session.Lists.AnimationReads);

        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\extra.anm"));
        Assert.Equal(4, session.Lists.List("animation")!.Entries.Count);
        Assert.Equal(3, session.Lists.AnimationReads);
        session.Undo();
        Assert.Equal(3, session.Lists.List("animation")!.Entries.Count);
        Assert.Equal(3, session.Lists.AnimationReads);

        var more = Path.Combine(ws.Base, AnimationWorkspace.More);
        AnimationWorkspace.Write(more, "SC_WALK", "Sc_Sit", "Sc_Nod");
        File.SetLastWriteTimeUtc(more, File.GetLastWriteTimeUtc(more).AddMinutes(1));
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(30)));
        AssertSaved(session.Save());
        Assert.Contains(session.Lists.List("animation")!.Entries, e => e.Value == "Sc_Nod");
        Assert.Equal(4, session.Lists.AnimationReads);
    }

    /// <summary>An unsaved <c>[Animations]</c> line counts at once, and an undo takes its clips away again.</summary>
    [Fact]
    public void AnUnsavedAnimationsLineChangesTheList()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        Assert.Null(session.Lists.List("animation")!.Find("Sc_Extra"));

        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\extra.anm"));
        Assert.Equal("extra.anm", session.Lists.List("animation")!.Find("Sc_Extra")?.Detail);

        session.Undo();
        Assert.Null(session.Lists.List("animation")!.Find("Sc_Extra"));
    }

    /// <summary>
    /// A line that names a file that does not exist gives no warning, as the engine skips such a file. A file that does
    /// not read gives one warning of the list, with the file as its source, and the other files keep their clips.
    /// </summary>
    [Fact]
    public void AFileThatDoesNotReadGivesOneWarningAndTheOtherFilesKeepTheirClips()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        Assert.Empty(session.Lists.Animations()!.Warnings);

        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\broken.anm"));
        var data = session.Lists.Animations()!;

        var warning = Assert.Single(data.Warnings);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(AnimationWorkspace.Broken, warning.Source, ignoreCase: true);
        Assert.StartsWith("OpenLancer cannot read this animation file, so the clip list has no clips from it. ", warning.Message);
        Assert.Equal(["Sc_Run", "Sc_Sit", "SC_WALK"], session.Lists.List("animation")!.Entries.Select(e => e.Value));
    }

    /// <summary>A profile without bodyparts.ini has no clip list, so no check uses it.</summary>
    [Fact]
    public void AProfileWithoutBodypartsHasNoList()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Assert.Null(session.Lists.List("animation"));
        Assert.Null(session.Lists.Animations());
    }
}
