using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Data.Editing.Lists;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The clips and the skeletons of the animation picker (section 12.1 of the M8b design), with hand-written
/// files.</summary>
public sealed class AnimationCatalogTests
{
    private const string Folder = "DATA/CHARACTERS/ANIMATIONS";

    [Fact]
    public void ReadsClipNamesFromTheActiveDataFiles()
    {
        using var ws = new TestWorkspace();
        Write(ws, "bodygenericfemale.anm", AnimationFiles.Build());
        var files = FileSystem.FromPath(ws.Base);

        var clips = AnimationCatalog.Read(files, [$"{Folder}/bodygenericfemale.anm"]);

        Assert.Equal(["Sc_Run", "Sc_Walk"], clips.Select(c => c.Name));
        Assert.All(clips, c => Assert.Equal($"{Folder}/bodygenericfemale.anm", c.Archive));
        Assert.Empty(AnimationCatalog.Read(files, []));
    }

    /// <summary>The engine loads the files in the order of the [Animations] lines, and a later file replaces a name
    /// without letter case. So the clip has the spelling and the file of the last file. It also keeps each file with
    /// the name, in load order, for the skeleton groups of the list.</summary>
    [Fact]
    public void TheLastFileWinsForADuplicateName()
    {
        using var ws = new TestWorkspace();
        Write(ws, "first.anm", AnimationFiles.Build(true, "Sc_Walk", "Sc_Run"));
        Write(ws, "second.anm", AnimationFiles.Build(true, "Sc_RUN", "Sc_Talk"));
        Write(ws, "third.anm", AnimationFiles.Build(true, "sc_run"));
        var files = FileSystem.FromPath(ws.Base);

        var clips = AnimationCatalog.Read(files,
            [$"{Folder}/first.anm", $"{Folder}/second.anm", $"{Folder}/third.anm", $"{Folder}/third.anm"]);

        Assert.Equal([("sc_run", "third.anm"), ("Sc_Talk", "second.anm"), ("Sc_Walk", "first.anm")],
            clips.Select(c => (c.Name, Path.GetFileName(c.Archive))));
        Assert.Equal(["first.anm", "second.anm", "third.anm"], clips[0].Archives.Select(a => Path.GetFileName(a)));
        Assert.Equal([$"{Folder}/first.anm"], clips[2].Archives);
    }

    /// <summary>
    /// A body part belongs to the last [PetalAnimations] and the last [Skeleton] before it. A [Skeleton] without a new
    /// [PetalAnimations] starts a skeleton with the same files. A sex that is not male or female gives no sex, and a
    /// skeleton without a body part is not in the list.
    /// </summary>
    [Fact]
    public void TheSkeletonsComeFromTheBlocksOfBodyparts()
    {
        const string text = """
            [Animations]
            anim = characters\animations\body_m.anm
            [DetailSwitchTable]
            switch = 100, 10.0
            [PetalAnimations]
            anim = characters\animations\body_m.anm
            anim = characters\animations\shared.anm
            [Skeleton]
            sex = male
            [Body]
            nickname = body_a
            mesh = characters\body_a.dfm
            [Body]
            nickname = BODY_B
            [Skeleton]
            sex = female
            [Head]
            nickname = head_f
            [PetalAnimations]
            anim = characters\animations\hands.anm
            [Skeleton]
            sex = none
            [LeftHand]
            nickname = hand_l
            [RightHand]
            nickname = hand_r
            [Accessory]
            nickname = hat
            [PetalAnimations]
            anim = characters\animations\unused.anm
            [Skeleton]
            sex = male
            """;
        var sections = IniFile.ParseFile("bodyparts.ini", new MemoryStream(Encoding.ASCII.GetBytes(text))).ToList();

        var skeletons = AnimationCatalog.Skeletons(sections, path => $"DATA\\{path}");

        Assert.Equal(3, skeletons.Count);
        var (male, female, hands) = (skeletons[0], skeletons[1], skeletons[2]);
        Assert.Equal([@"DATA\characters\animations\body_m.anm", @"DATA\characters\animations\shared.anm"], male.Archives);
        Assert.Equal((FLGender.male, SkeletonParts.Body), (male.Sex, male.Parts));
        Assert.True(male.Bodyparts.SetEquals(["body_a", "body_b"]));
        Assert.Contains("Body_A", male.Bodyparts);
        Assert.Equal(male.Archives, female.Archives);
        Assert.Equal((FLGender.female, SkeletonParts.Head), (female.Sex, female.Parts));
        Assert.Equal([@"DATA\characters\animations\hands.anm"], hands.Archives);
        Assert.Equal((FLGender.unset, SkeletonParts.Hands), (hands.Sex, hands.Parts));
        Assert.True(hands.Bodyparts.SetEquals(["hand_l", "hand_r"]));
    }

    private static void Write(TestWorkspace ws, string name, (byte[] Bytes, int DataOffset) file)
    {
        var folder = Path.Combine(ws.Base, "DATA", "CHARACTERS", "ANIMATIONS");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), file.Bytes);
    }
}
