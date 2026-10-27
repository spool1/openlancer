using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenLancer.Tests;

/// <summary>
/// Animation files for the tests. A test builds a small UTF file itself, so no retail bytes go into the repo.
/// </summary>
internal static class AnimationFiles
{
    private const int NodeSize = 44;
    private const int Intermediate = 0x10;
    private const int Leaf = 0x80;

    /// <summary>
    /// A UTF file: the root, "Animation" with the child "Script" and a peer "Other", and the scripts under "Script". A
    /// data block of keys follows the string block.
    /// </summary>
    /// <param name="scripts">False for a file whose "Animation" node has no "Script" child.</param>
    /// <param name="names">The names of the scripts. Without names, the file has Sc_Walk and Sc_Run.</param>
    public static (byte[] Bytes, int DataOffset) Build(bool scripts = true, params string[] names)
    {
        if (names.Length == 0)
        {
            names = ["Sc_Walk", "Sc_Run"];
        }

        var strings = new List<string> { "\\", "animation", "SCRIPT", "Other" };
        strings.AddRange(names);
        var nameOffsets = new List<int>();
        var stringBlock = new MemoryStream();
        foreach (var name in strings)
        {
            nameOffsets.Add((int)stringBlock.Length);
            stringBlock.Write(Encoding.ASCII.GetBytes(name + "\0"));
        }

        // (name, flags, peer, child) by node index. The scripts follow the four fixed nodes, and the last script is a
        // leaf.
        var nodes = new List<(int Name, int Flags, int Peer, int Child)>
        {
            (0, Intermediate, 0, 1),
            (1, Intermediate, 3, scripts ? 2 : 0),
            (2, Intermediate, 0, 4),
            (3, Leaf, 0, 0),
        };
        for (var i = 0; i < names.Length; i++)
        {
            var last = i == names.Length - 1;
            nodes.Add((4 + i, last ? Leaf : Intermediate, last ? 0 : 5 + i, 0));
        }

        var nodeBlock = new MemoryStream();
        foreach (var (name, flags, peer, child) in nodes)
        {
            foreach (var value in new[] { peer * NodeSize, nameOffsets[name], flags, 0, child * NodeSize, 0, 0, 0, 0, 0, 0 })
            {
                nodeBlock.Write(BitConverter.GetBytes(value));
            }
        }

        const int headerSize = 4 + 9 * 4;
        var nodeOffset = headerSize;
        var stringOffset = nodeOffset + (int)nodeBlock.Length;
        var dataOffset = stringOffset + (int)stringBlock.Length;
        var file = new MemoryStream();
        file.Write("UTF "u8);
        foreach (var value in new[] { 257, nodeOffset, (int)nodeBlock.Length, 0, NodeSize, stringOffset,
                     (int)stringBlock.Length, (int)stringBlock.Length, dataOffset })
        {
            file.Write(BitConverter.GetBytes(value));
        }

        file.Write(nodeBlock.ToArray());
        file.Write(stringBlock.ToArray());
        file.Write(new byte[4096]);
        return (file.ToArray(), dataOffset);
    }
}
