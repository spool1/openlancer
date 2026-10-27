using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Editing.Lists;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The script names of an animation file (section 8.6 of the M21 design). The tests build small UTF files with
/// <see cref="AnimationFiles"/>, so no retail bytes go into the repo.
/// </summary>
public sealed class AnimationNamesTests
{
    [Fact]
    public void TheReaderGivesTheNamesUnderAnimationScript()
    {
        var (bytes, dataOffset) = AnimationFiles.Build();
        using var stream = new GuardStream(bytes, dataOffset);

        Assert.Equal(["Sc_Walk", "Sc_Run"], AnimationNames.Scripts(stream, "test.anm"));
        var (three, threeData) = AnimationFiles.Build(true, "Sc_A", "Sc_B", "Sc_C");
        using var threeStream = new GuardStream(three, threeData);
        Assert.Equal(["Sc_A", "Sc_B", "Sc_C"], AnimationNames.Scripts(threeStream, "test.anm"));
    }

    [Fact]
    public void AFileWithoutScriptsGivesNoName()
    {
        var (bytes, dataOffset) = AnimationFiles.Build(scripts: false);
        using var stream = new GuardStream(bytes, dataOffset);

        Assert.Empty(AnimationNames.Scripts(stream, "test.anm"));
    }

    [Fact]
    public void AFileThatIsNotUtfIsAnError()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("[Animations]\nanim = test.anm\n"));
        Assert.Throws<InvalidDataException>(() => AnimationNames.Scripts(stream, "test.anm"));
        var (bytes, _) = AnimationFiles.Build();
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 8);
        Assert.Throws<InvalidDataException>(() => AnimationNames.Scripts(new MemoryStream(bytes), "test.anm"));
    }

    /// <summary>A stream that fails the test when a read reaches the data block.</summary>
    private sealed class GuardStream(byte[] bytes, int limit) : MemoryStream(bytes, false)
    {
        public override int Read(Span<byte> buffer)
        {
            Assert.True(Position + buffer.Length <= limit, "The reader read the data block.");
            return base.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Assert.True(Position + count <= limit, "The reader read the data block.");
            return base.Read(buffer, offset, count);
        }
    }
}
