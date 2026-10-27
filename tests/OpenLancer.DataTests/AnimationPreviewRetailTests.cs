using System;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Schema;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.Thn;
using LibreLancer.World;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The clip preview of the animation picker on the retail data (section 12.1 of the M8b design). A clip moves the bones
/// and the root of a real character. The preview copies the models of a scene character. A head clip plays over a
/// standing idle of the sex of the body of each costume.
/// </summary>
public sealed class AnimationPreviewRetailTests
{
    [Fact]
    public void PreviewAdvancesTheBonesOfARealCharacter()
    {
        GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var costume = game.Data.Items.Costumes.First(c => c.Body != null);
        var skeleton = new DfmSkeletonManager(costume.Body.LoadModel(game.Resources)!,
            costume.Head?.LoadModel(game.Resources), costume.LeftHand?.LoadModel(game.Resources),
            costume.RightHand?.LoadModel(game.Resources));
        var candidate = game.Data.GetCharacterAnimations().Scripts.Values.SelectMany(script =>
            script.JointMaps.Select(joint => (script, joint))).First(x =>
        {
            if (!x.joint.Channel.HasOrientation || x.joint.Channel.FrameCount < 2 ||
                x.joint.Channel.Duration < 0.1f || !skeleton.BodySkinning.Bones.ContainsKey(x.joint.ChildName))
            {
                return false;
            }

            var channel = x.joint.Channel;
            var a = 0;
            var b = 0;
            var start = channel.QuaternionAtTime(0, ref a);
            var middle = channel.QuaternionAtTime(channel.Duration * 0.5f, ref b);
            return MathF.Abs(Quaternion.Dot(start, middle)) < 0.9999f;
        });
        var bone = skeleton.BodySkinning.Bones[candidate.joint.ChildName];
        var actor = new GameObject { RenderComponent = new CharacterRenderer(skeleton) };
        var character = new ThnSceneObject { Name = "preview", Object = actor, Rotate = Quaternion.Identity };
        skeleton.StartScript(candidate.script, 0, 1, ThnAnimationPreview.ClipDuration(candidate.script));
        ThnAnimationPreview.Advance(skeleton, character, 0);
        var before = bone.Rotation;
        ThnAnimationPreview.Advance(skeleton, character, candidate.joint.Channel.Duration * 0.5);
        Assert.True(MathF.Abs(Quaternion.Dot(before, bone.Rotation)) < 0.9999f);
    }

    [Fact]
    public void PreviewAppliesRootMovement()
    {
        GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var costume = game.Data.Items.Costumes.First(c => c.Body != null);
        var skeleton = new DfmSkeletonManager(costume.Body.LoadModel(game.Resources)!,
            costume.Head?.LoadModel(game.Resources), costume.LeftHand?.LoadModel(game.Resources),
            costume.RightHand?.LoadModel(game.Resources));
        var candidate = game.Data.GetCharacterAnimations().Scripts.Values.SelectMany(script =>
            script.ObjectMaps.Select(map => (script, map))).First(x =>
        {
            if (!string.Equals(x.map.ParentName, "Root", StringComparison.OrdinalIgnoreCase) ||
                !x.map.Channel.HasPosition || x.map.Channel.FrameCount < 2 || x.map.Channel.Duration < 0.1f)
            {
                return false;
            }

            var channel = x.map.Channel;
            var a = 0;
            var b = 0;
            var start = channel.PositionAtTime(0, ref a);
            var middle = channel.PositionAtTime(channel.Duration * 0.5f, ref b);
            return Vector3.Distance(start, middle) > 0.01f;
        });
        var actor = new GameObject { RenderComponent = new CharacterRenderer(skeleton) };
        var character = new ThnSceneObject { Name = "preview", Object = actor, Rotate = Quaternion.Identity };
        skeleton.StartScript(candidate.script, 0, 1, ThnAnimationPreview.ClipDuration(candidate.script));
        ThnAnimationPreview.Advance(skeleton, character, 0);
        var before = character.Translate;
        ThnAnimationPreview.Advance(skeleton, character, candidate.map.Channel.Duration * 0.5);
        Assert.True(Vector3.Distance(before, character.Translate) > 0.01f);
    }

    /// <summary>The preview copies the models of a character of the scene, and the renderer of another object gives no
    /// models.</summary>
    [Fact]
    public void ThePreviewTakesTheModelsOfASceneCharacter()
    {
        GameDataPaths.RequireBase();
        var game = RetailGame.Instance;
        var costume = game.Data.Items.Costumes.First(c => c.Body != null && c.Head != null && c.LeftHand != null);
        var skeleton = new DfmSkeletonManager(costume.Body.LoadModel(game.Resources)!,
            costume.Head!.LoadModel(game.Resources), costume.LeftHand!.LoadModel(game.Resources),
            costume.RightHand?.LoadModel(game.Resources));
        var actor = new GameObject { RenderComponent = new CharacterRenderer(skeleton) };

        var models = CharacterModels.Of(actor)!;

        Assert.Same(skeleton.Body, models.Body);
        Assert.Same(skeleton.Head, models.Head);
        Assert.Same(skeleton.LeftHand, models.LeftHand);
        Assert.Same(skeleton.RightHand, models.RightHand);
        Assert.Equal(models, CharacterModels.Of(actor));
        Assert.Null(CharacterModels.Of(new GameObject()));
        Assert.Null(CharacterModels.Of(null));
    }

    /// <summary>
    /// A head clip plays over the standing idle of the sex of the body. The sex comes from the [Skeleton] of the body part
    /// (Bodypart.Sex), so a female body without a sex word in its path, for example the body of a story character, also
    /// gets the female idle.
    /// </summary>
    [Fact]
    public void EachCostumeGetsTheIdleOfTheSexOfItsBody()
    {
        GameDataPaths.RequireBase();
        var data = RetailGame.Instance.Data;
        var clips = AnimationClips.Read(data);
        var head = clips.Clips.First(c => clips.PartsOf(c) == SkeletonParts.Head);
        var costumes = data.Items.Costumes.Where(c => c.Body != null).ToList();
        var noWord = 0;
        foreach (var costume in costumes)
        {
            var idle = clips.IdleFor(head, costume.Body.Sex);
            Assert.NotNull(idle);
            var skeletons = clips.SkeletonText(clips.Find(idle)!)["Skeletons: ".Length..].Split(", ");
            Assert.Contains($"{costume.Body.Sex} body", skeletons);
            var path = costume.Body.Path ?? "";
            if (costume.Body.Sex == FLGender.female && !path.Contains("female", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("juni", StringComparison.OrdinalIgnoreCase))
            {
                noWord++;
            }
        }

        Assert.True(noWord > 0, "No female body without a sex word in its path.");
        Assert.Contains(costumes, c => c.Body.Sex == FLGender.male);
    }
}
