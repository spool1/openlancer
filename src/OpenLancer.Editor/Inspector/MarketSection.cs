using System;
using System.Linq;
using System.Text;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The "Markets" section of a good: each <c>marketgood</c> line of all market files that names it, by base. Each line
/// has a named field for each of its values (see <see cref="MarketEdits.Fields"/>) and a "Buys or sells" dropdown for
/// its transaction type. A line of a <c>[BaseGood]</c> that the active mod adds can change or go. A patch cannot change
/// a line of a lower layer, so such a line is read-only. "Add to base" makes one more base sell the good (see
/// <see cref="MarketEdits"/>).
/// </summary>
internal sealed class MarketSection(EditorWindow window, PropertyGrid grid)
{
    private const string LowerLineReason =
        "A market line of a lower layer. A patch cannot change or remove it. Use a whole-file override for that.";

    /// <summary>The hint of a field of a value that the line does not have.</summary>
    private const string NotSet = "Not set";

    /// <summary>The label of the dropdown of the transaction type.</summary>
    private const string TransactionLabel = "Buys or sells";

    private const string TransactionTip =
        "What the base does with the good. This is the transaction type, the sixth value of the marketgood line.\n\n" +
        "Buys and sells: the player can buy and sell the good at the base.\n" +
        "Only buys: the player can sell the good at the base, but cannot buy it there.\n" +
        "Only sells: the player can buy the good at the base, but cannot sell it there.";

    /// <summary>The transaction types in the order of the dropdown.</summary>
    private static readonly MarketTransaction[] Transactions = Enum.GetValues<MarketTransaction>();

    /// <summary>The labels of the value fields of a line, in the order of <see cref="MarketEdits.Fields"/>.</summary>
    private static readonly PropertyGrid.ComponentLabel[] Labels =
        MarketEdits.Fields.Select(f => new PropertyGrid.ComponentLabel(f.Name, EditorTheme.Label)).ToArray();

    private readonly FilterList<string> bases = new();
    private ListView? basesList;

    /// <param name="good">The nickname of the good.</param>
    /// <param name="key">The key of the open state of the section in the settings, "Type.Section".</param>
    public void Draw(string good, string key, string title = "Markets")
    {
        if (window.Session is not { } session || !grid.BeginSection(key, EditorIcons.Store, title))
        {
            return;
        }

        var lines = session.Edits.MarketLines(good);
        if (lines.Count == 0)
        {
            grid.Row("Bases", () => PropertyGrid.Field(null, hint: "No base sells it"));
        }

        foreach (var line in lines)
        {
            var reason = session.CanEdit && MarketEdits.CanChange(session.Edits, line)
                ? null
                : session.NoEditReason ?? LowerLineReason;
            grid.Row(line.Base, () => Values(session, line, reason));
            grid.SetNextNote(TransactionTip);
            grid.Row(TransactionLabel, () => Transaction(session, line, reason));
        }

        grid.Row("Add to base", () => AddToBase(session, good));
        grid.EndSection();
    }

    /// <summary>
    /// The values of a line that have a field: a named field for each value and a remove button, or read-only fields
    /// with a lock. Each field edits only its own value, so each value keeps its place in the line.
    /// </summary>
    /// <param name="reason">Why the line cannot change, or null when it can.</param>
    private void Values(EditorSession session, MarketLine line, string? reason)
    {
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var texts = MarketEdits.Fields.Select(f => MarketEdits.FieldText(line, f)).ToArray();
        if (reason != null)
        {
            PropertyGrid.ComponentFields(texts, Labels, (index, text, width) =>
            {
                PropertyGrid.Field(text, width, NotSet);
                ImGui.SetItemTooltip(MarketEdits.Fields[index].Tip + "\n\n" + reason);
            }, ToolbarItems.IconWidth + spacing);
            Lock(spacing);
            return;
        }

        PropertyGrid.ComponentFields(texts, Labels, (index, text, width) =>
        {
            var field = MarketEdits.Fields[index];
            var edited = text;
            ImGui.SetNextItemWidth(width);
            var changed = ImGui.InputTextWithHint("##value", NotSet, ref edited,
                (uint)Encoding.UTF8.GetByteCount(edited) + 256);
            // The edit runs when the field ends, also when the window closes, as the other text fields do.
            grid.CommitField(MarketEdits.LineKey(line), changed, text, edited, (value, _) =>
            {
                // The command runner writes a problem of the edit to the Console, so the commit returns no error.
                window.Commands.Run(edits => MarketCommands.SetValue(edits, line, field, value));
                return null;
            });
            ImGui.SetItemTooltip(field.Tip);
        }, ToolbarItems.IconWidth + spacing);

        ImGui.SameLine(0, spacing);
        if (ToolbarItems.Button("remove", EditorIcons.TrashAlt,
                $"Remove the line. Then {line.Base} does not buy or sell the good."))
        {
            window.Commands.Run(edits => MarketCommands.SetLine(edits, line, null));
        }
    }

    /// <summary>The transaction type of a line: a dropdown with the three types, or a read-only field.</summary>
    /// <param name="reason">Why the line cannot change, or null when it can.</param>
    private void Transaction(EditorSession session, MarketLine line, string? reason)
    {
        var text = TransactionText(line);
        if (reason != null)
        {
            ReadOnly(text, TransactionTip, reason);
            return;
        }

        ImGui.SetNextItemWidth(-1);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##transaction", text))
        {
            foreach (var type in Transactions)
            {
                var selected = type == line.Transaction;
                if (ImGui.Selectable(Name(type), selected) && !selected)
                {
                    window.Commands.Run(edits => MarketCommands.SetTransaction(edits, line, type));
                }
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.SetItemTooltip(TransactionTip);
    }

    /// <summary>A read-only field with a lock. The tooltip tells what the field holds and why it cannot change.</summary>
    private static void ReadOnly(string text, string tip, string reason)
    {
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        PropertyGrid.Field(text, Math.Max(1, ImGui.GetContentRegionAvail().X - ToolbarItems.IconWidth - spacing));
        ImGui.SetItemTooltip(tip + "\n\n" + reason);
        Lock(spacing);
    }

    /// <summary>The lock after a read-only field. Its tooltip tells why the field cannot change.</summary>
    private static void Lock(float spacing)
    {
        ImGui.SameLine(0, spacing);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(EditorIcons.Lock.ToString());
    }

    /// <summary>The name of a transaction type in the dropdown.</summary>
    internal static string Name(MarketTransaction type) => type switch
    {
        MarketTransaction.BuyAndSell => "Buys and sells",
        MarketTransaction.BuyOnly => "Only buys",
        MarketTransaction.SellOnly => "Only sells",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// The text of the transaction type of a line: the name of its type, the value when the game does not know it, or
    /// "Not set" for a line without the value.
    /// </summary>
    internal static string TransactionText(MarketLine line) => line.Transaction is { } type
        ? Name(type)
        : line.Values.Count > MarketLine.TransactionIndex
            ? $"Unknown type {FieldEdit.FormatValue(line.Values[MarketLine.TransactionIndex])}"
            : NotSet;

    /// <summary>A list of the bases of the data list (milestone M21), so a base that the active mod adds and did not save
    /// is in it. The chosen base gets a line that sells the good.</summary>
    private void AddToBase(EditorSession session, string good)
    {
        var list = session.Edits.Lists.List("base");
        if (!ReferenceEquals(list, basesList))
        {
            basesList = list;
            bases.Entries = list == null
                ? []
                : FilterList<string>.Sorted(list.Entries.Select(b => new FilterEntry<string>(b.Value,
                    b.Section != null && session.SectionName(b.Section) is { Length: > 0 } name ? name : b.Value, b.Value,
                    EditorIcons.Building)));
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(!session.CanEdit || list == null);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##add-base", "Choose a base...", ImGuiComboFlags.HeightLargest))
        {
            if (bases.Draw(_ => false) is { } chosen)
            {
                ImGui.CloseCurrentPopup();
                var values = MarketEdits.DefaultValues(MarketEdits.Category(session.Edits, good));
                window.Commands.Run(edits => MarketCommands.AddToBase(edits, good, chosen.Item, values));
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(session.NoEditReason ??
                             "Makes the base sell the good: a line in a [BaseGood] of the base that the active mod adds.");
    }
}
