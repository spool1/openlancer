using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>A clip and the animation files that contain it.</summary>
/// <param name="Archive">The last file with the name. The game plays the clip of this file.</param>
public sealed record AnimationClip(string Name, string Archive)
{
    /// <summary>Each file with the name, in load order. The last file is <see cref="Archive"/>.</summary>
    public IReadOnlyList<string> Archives { get; init; } = [Archive];
}

/// <summary>The body parts of a skeleton of bodyparts.ini.</summary>
[Flags]
public enum SkeletonParts
{
    None = 0,
    Body = 1,
    Head = 2,
    Hands = 4,
}

/// <summary>
/// A skeleton of bodyparts.ini: the animation files of its <c>[PetalAnimations]</c>, the sex of its <c>[Skeleton]</c>,
/// and the body parts that follow them.
/// </summary>
/// <param name="Archives">The animation files, as data paths.</param>
/// <param name="Sex">The sex that the <c>[Skeleton]</c> names, or unset for another value, for example "none".</param>
/// <param name="Parts">The kinds of the body parts.</param>
/// <param name="Bodyparts">The nicknames of the body parts, without letter case.</param>
public sealed record AnimationSkeleton(IReadOnlyList<string> Archives, FLGender Sex, SkeletonParts Parts,
    IReadOnlySet<string> Bodyparts);

/// <summary>The animation clips and the skeletons of the active data view.</summary>
public static class AnimationCatalog
{
    /// <summary>
    /// Reads the animations that the game loads. The files are in load order, and a later file replaces a name without
    /// letter case. So a clip has the spelling and the file of the last file, and it also keeps the earlier files with
    /// the name. The result is sorted by name.
    /// </summary>
    public static IReadOnlyList<AnimationClip> Read(FileSystem files, IEnumerable<string> archives)
    {
        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in archives)
        {
            using var stream = files.Open(path);
            foreach (var name in AnimationNames.Scripts(stream, path))
            {
                IReadOnlyList<string> earlier = clips.TryGetValue(name, out var clip) ? clip.Archives : [];
                clips[name] = new AnimationClip(name, path)
                {
                    Archives = earlier.Contains(path) ? earlier : [.. earlier, path],
                };
            }
        }

        return clips.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The skeletons of the sections of bodyparts.ini, in the order of the file. A body part belongs to the last
    /// <c>[PetalAnimations]</c> and the last <c>[Skeleton]</c> before it. The engine gives a body part the sex of the
    /// last <c>[Skeleton]</c> in the same way (BodypartsIni.cs:43-60), but it ignores <c>[PetalAnimations]</c>
    /// (BodypartsIni.cs:40-42). So the skeletons only order and filter the clip list of the editor. For a sex such as
    /// "none", the engine keeps the sex of the earlier <c>[Skeleton]</c>, but here the skeleton has no sex. A skeleton
    /// without a body part is not in the result.
    /// </summary>
    /// <param name="dataPath">Gives the data path of a file name of <c>[PetalAnimations]</c>, or null.</param>
    public static IReadOnlyList<AnimationSkeleton> Skeletons(IEnumerable<Section> bodyparts,
        Func<string, string?> dataPath)
    {
        var skeletons = new List<AnimationSkeleton>();
        IReadOnlyList<string> archives = [];
        var sex = FLGender.unset;
        var parts = SkeletonParts.None;
        HashSet<string>? names = null;
        foreach (var section in bodyparts)
        {
            var part = section.Name.ToLowerInvariant() switch
            {
                "body" => SkeletonParts.Body,
                "head" => SkeletonParts.Head,
                "lefthand" or "righthand" => SkeletonParts.Hands,
                _ => SkeletonParts.None,
            };
            if (part != SkeletonParts.None)
            {
                if (section["nickname"] is { Count: > 0 } nickname)
                {
                    names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names.Add(nickname[0].ToString()!);
                    parts |= part;
                }

                continue;
            }

            if (section.Name.Equals("PetalAnimations", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                archives = section.Where(e => e.Count > 0).Select(e => dataPath(e[0].ToString()!)).OfType<string>()
                    .ToList();
            }
            else if (section.Name.Equals("Skeleton", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                sex = section["sex"] is { Count: > 0 } value &&
                      Enum.TryParse<FLGender>(value[0].ToString(), true, out var named)
                    ? named
                    : FLGender.unset;
            }
        }

        Close();
        return skeletons;

        void Close()
        {
            if (names != null)
            {
                skeletons.Add(new AnimationSkeleton(archives, sex, parts, names));
            }

            names = null;
            parts = SkeletonParts.None;
        }
    }
}
