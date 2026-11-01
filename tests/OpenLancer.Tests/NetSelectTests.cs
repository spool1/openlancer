using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The select of the net result of a request (<see cref="EditResult.Net"/>, review finding of MCP1-2). The walk reads
/// the parts in order. The renames and the deletes of a part apply to the select first, then the select of the part
/// replaces it. Each test makes the parts by hand, so it shows one rule.
/// </summary>
public sealed class NetSelectTests
{
    /// <summary>A part of a request. A delete of the kind <see cref="DeleteReason.Cascade"/> names a line as its
    /// cause.</summary>
    private static EditResult Part(string? select = null, string[]? adds = null,
        (string From, string To)[]? renames = null, string[]? deletes = null, DeleteReason why = DeleteReason.Target) =>
        new(EditStatus.Applied)
        {
            Select = select,
            Adds = adds ?? [],
            Renames = (renames ?? []).Select(r => new ResultRename(r.From, r.To)).ToList(),
            Deletes = (deletes ?? [])
                .Select(d => new ResultDelete(d, why, why == DeleteReason.Cascade ? "parent = obj_p" : null)).ToList(),
        };

    private static string? SelectOf(params EditResult[] parts) =>
        EditResult.Net(EditResult.Merge(EditStatus.Applied, parts), parts).Select;

    [Fact]
    public void TheSelectIsTheSelectOfTheLastPartThatHasOne()
    {
        Assert.Equal("obj:b", SelectOf(Part("obj:a"), Part(), Part("obj:b")));
        Assert.Equal("obj:a", SelectOf(Part("obj:a"), Part(adds: ["obj:c"])));
        Assert.Null(SelectOf(Part(adds: ["obj:c"]), Part()));
        Assert.Null(SelectOf());
    }

    /// <summary>A rename of the selected item in a later part gives its new reference, also after two renames. A part
    /// whose rename has no select of its own, for example the rename of a data item, needs this rule.</summary>
    [Fact]
    public void ARenameOfTheSelectedItemInALaterPartGivesItsNewReference()
    {
        Assert.Equal("obj:y", SelectOf(Part("obj:x", adds: ["obj:x"]), Part(renames: [("obj:x", "obj:y")])));
        Assert.Equal("obj:z",
            SelectOf(Part("obj:x"), Part(renames: [("obj:x", "obj:y")]), Part(renames: [("obj:y", "obj:z")])));
        Assert.Equal("obj:z", SelectOf(Part("obj:x"), Part(renames: [("obj:x", "obj:y"), ("obj:y", "obj:z")])));
        // The merge of the parts keeps the old reference, which no longer resolves.
        Assert.Equal("obj:x",
            EditResult.Merge(EditStatus.Applied, [Part("obj:x"), Part(renames: [("obj:x", "obj:y")])]).Select);
    }

    [Fact]
    public void ARenameOrADeleteOfAnotherItemKeepsTheSelect()
    {
        Assert.Equal("obj:x", SelectOf(Part("obj:x"), Part(renames: [("obj:w", "obj:v")])));
        Assert.Equal("obj:x", SelectOf(Part("obj:x"), Part(deletes: ["obj:w"])));
        // After the rename, the old reference names another item, for example a new item with the old nickname.
        Assert.Equal("obj:y", SelectOf(Part("obj:x"), Part(renames: [("obj:x", "obj:y")]), Part(deletes: ["obj:x"])));
    }

    /// <summary>A delete of the selected item gives no select. This is also true for an item that the request added and
    /// then deleted. It is also true for an item that the request renamed first, and for a child that goes with its
    /// parent.</summary>
    [Fact]
    public void ADeleteOfTheSelectedItemGivesNoSelect()
    {
        Assert.Null(SelectOf(Part("obj:x", adds: ["obj:x"]), Part(deletes: ["obj:x"])));
        Assert.Null(SelectOf(Part("obj:x"), Part(renames: [("obj:x", "obj:y")]), Part(deletes: ["obj:y"])));
        Assert.Null(SelectOf(Part("obj:x"), Part(deletes: ["obj:p", "obj:x"], why: DeleteReason.Cascade)));
        Assert.Null(SelectOf(Part("obj:x"), Part(deletes: ["obj:x"]), Part(adds: ["obj:c"])));
        Assert.Equal("obj:x",
            EditResult.Merge(EditStatus.Applied, [Part("obj:x"), Part(deletes: ["obj:x"])]).Select);
    }

    /// <summary>A later part with its own select replaces the select. Its renames and its deletes apply first, so its
    /// own select stays, also when it renames or deletes the item that an earlier part selected.</summary>
    [Fact]
    public void ALaterPartWithItsOwnSelectReplacesTheSelect()
    {
        Assert.Equal("obj:z", SelectOf(Part("obj:x"), Part(deletes: ["obj:x"]), Part("obj:z")));
        Assert.Equal("obj:q", SelectOf(Part("obj:x"), Part("obj:q", renames: [("obj:x", "obj:y")])));
        Assert.Equal("obj:y", SelectOf(Part("obj:x"), Part("obj:y", renames: [("obj:x", "obj:y")])));
        Assert.Equal("obj:p", SelectOf(Part("obj:x"), Part("obj:p", deletes: ["obj:x"])));
        // A later rename of the new select gives its new reference.
        Assert.Equal("obj:r", SelectOf(Part("obj:x"), Part("obj:q"), Part(renames: [("obj:q", "obj:r")])));
    }

    /// <summary>In a part, the renames and the deletes name the items before the part, and the select names an item
    /// after it. So the renames and the deletes of a part do not change its own select. An example is a part that
    /// renames x to y, then adds a new x and selects it.</summary>
    [Fact]
    public void TheRenamesAndTheDeletesOfAPartDoNotChangeItsOwnSelect()
    {
        Assert.Equal("obj:x", SelectOf(Part("obj:x"), Part("obj:x", renames: [("obj:x", "obj:y")])));
        Assert.Equal("obj:x", SelectOf(Part("obj:x", renames: [("obj:x", "obj:y")])));
        Assert.Equal("obj:x", SelectOf(Part("obj:x", deletes: ["obj:x"])));
        Assert.Equal("obj:x", SelectOf(Part("obj:w"), Part("obj:x", deletes: ["obj:x"])));
    }

    /// <summary>The walk changes only the select. The other lists of the result and the other fields stay as the rules
    /// of the net result give them.</summary>
    [Fact]
    public void TheSelectRuleKeepsTheOtherFieldsOfTheResult()
    {
        EditResult[] parts = [Part("obj:x", adds: ["obj:x"]), Part(renames: [("obj:x", "obj:y")])];
        var merged = EditResult.Merge(EditStatus.Applied, parts) with { Summary = "done", Revision = 4 };

        var net = EditResult.Net(merged, parts);

        Assert.Equal("obj:y", net.Select);
        Assert.Equal(["obj:y"], net.Adds);
        Assert.Empty(net.Renames);
        Assert.Equal(("done", 4L), (net.Summary, net.Revision));
    }
}
