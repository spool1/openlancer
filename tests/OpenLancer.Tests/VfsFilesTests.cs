using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The order of the files of a view. The data is invented.</summary>
public sealed class VfsFilesTests
{
    /// <summary>The comparer gives the order of the find: in each folder the files first, then the subfolders, each by
    /// name without case. So a files list can put a file that is not on disk yet at its place after the save.</summary>
    [Fact]
    public void TheFindOrderComparerGivesTheOrderOfTheFind()
    {
        using var ws = new TestWorkspace();
        foreach (var path in new[]
                 {
                     @"DATA\b.ini", @"DATA\A\z.ini", @"DATA\A\B\a.ini", @"DATA\a.ini", @"DATA\Z.ini", @"DATA\A\c.ini",
                     @"DATA\b\a.ini",
                 })
            ws.WriteBase(path, "[Fog]\n");

        var found = VfsFiles.Find(FileSystem.FromPath(ws.Base), ".ini", "DATA").ToList();

        Assert.Equal([@"DATA\a.ini", @"DATA\b.ini", @"DATA\Z.ini", @"DATA\A\c.ini", @"DATA\A\z.ini", @"DATA\A\B\a.ini",
            @"DATA\b\a.ini"], found);
        var sorted = found.AsEnumerable().Reverse().ToList();
        sorted.Sort(VfsFiles.CompareInFindOrder);
        Assert.Equal(found, sorted);
        Assert.True(VfsFiles.CompareInFindOrder(@"DATA\A\c.ini", @"data\a\C.INI") == 0);
    }
}
