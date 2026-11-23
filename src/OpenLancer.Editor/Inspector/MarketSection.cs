using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The "Markets" section of a good: for each base that has a <c>marketgood</c> line of the good, the last line, which the
/// games keep. The line has a named field for each of its values (see <see cref="MarketPlans.Fields"/>) and a "Buys or
/// sells" dropdown for its transaction type. A value without an effect in the games is read-only, for example the price
/// multiplier of a ship package, or each value of a hull good except the price multiplier (see
/// <see cref="MarketPlans.NoEffectReason(EditSession, MarketLine, int)"/>). A line of a <c>[BaseGood]</c> that the
/// active mod adds changes or goes. An edit of a line of a lower layer writes a later line of the active mod, and a row
/// under the line shows that result in plain words (see <see cref="MarketPlans.Override"/>). "Add to base" makes one
/// more base sell the good (see <see cref="MarketPlans"/>).
/// </summary>
internal sealed class MarketSection(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The tooltip end of a field of a line of a lower layer.</summary>
    internal const string LowerLineTip =
        "This line comes from a lower layer. An edit writes a later line of the active mod in the same market file. " +
        "The games keep the last line of a good at a base.";

    /// <summary>The label of the row of a line that replaces a lower line.</summary>
    private const string OverrideLabel = "Change";

    internal const string OverrideNote =
        "The active mod replaces the market line of a lower layer with a later line. The games keep the last line of " +
        "a good at a base. Revert removes the line of the active mod, and the line of the lower layer counts again.";

    internal const string RevertTip =
        "Revert: remove the line of the active mod. Then the line of the lower layer counts again.";

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

    /// <summary>The labels of the value fields of a line, in the order of <see cref="MarketPlans.Fields"/>.</summary>
    private static readonly PropertyGrid.ComponentLabel[] Labels =
        MarketPlans.Fields.Select(f => new PropertyGrid.ComponentLabel(f.Name, EditorTheme.Label)).ToArray();

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

        foreach (var line in LastLines(lines))
        {
            var reason = session.NoEditReason ?? MarketPlans.NoChangeReason(session.Edits, line);
            var lower = session.Edits.Get(line.Section) is not { IsNew: true };
            var change = MarketPlans.Override(session.Edits, line);
            grid.Row(line.Base, () => Values(line, reason, lower, change != null));
            grid.SetNextNote(TransactionTip);
            grid.Row(TransactionLabel, () => Transaction(line, reason));
            if (change is { } replaced)
            {
                grid.SetNextNote(OverrideNote);
                grid.Row(OverrideLabel, () => Override(replaced, reason));
            }
        }

        grid.Row("Add to base", () => AddToBase(session, good));
        grid.EndSection();
    }

    /// <summary>
    /// The last line of each base, in the order of the lines. The games keep the last line of a good at a base, so the
    /// section shows only that line. A line that the games skip does not count (see <see cref="BaseMarkets.IsSkipped"/>).
    /// </summary>
    internal static IReadOnlyList<MarketLine> LastLines(IReadOnlyList<MarketLine> lines) =>
        BaseMarkets.KeptLines(lines, l => l.Base.Trim(), l => l.Values);

    /// <summary>The tooltip of the remove button of a line. It tells only the result that the remove gives (see
    /// <see cref="MarketPlans.SetLine"/>).</summary>
    /// <param name="lower">True for a line of a lower layer.</param>
    /// <param name="replaces">True for a line of the active mod that replaces a line of a lower layer (see
    /// <see cref="MarketPlans.Override"/>).</param>
    internal static string RemoveTip(MarketLine line, bool lower, bool replaces) =>
        lower ? $"Remove the line. The save writes a later line of the active mod. {BuyOnly(line)}"
        : replaces ? $"Remove the line. The line replaces a line of a lower layer, so it changes. {BuyOnly(line)}"
        : $"Remove the line. Then {line.Base} does not buy or sell the good.";

    /// <summary>The result of a remove that writes the buy-only form.</summary>
    private static string BuyOnly(MarketLine line) =>
        $"{MarketPlans.BuyOnlyText} stops the sale of the good. Then {line.Base} only buys the good.";

    /// <summary>
    /// The values of a line that have a field: a named field for each value and a remove button, or read-only fields
    /// with a lock. Each field edits only its own value, so each value keeps its place in the line.
    /// </summary>
    /// <param name="reason">Why the line cannot change, or null when it can.</param>
    /// <param name="lower">True for a line of a lower layer. An edit then writes a later line of the active mod.</param>
    /// <param name="replaces">True for a line of the active mod that replaces a line of a lower layer.</param>
    private void Values(MarketLine line, string? reason, bool lower, bool replaces)
    {
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var texts = MarketPlans.Fields.Select(f => MarketPlans.FieldText(line, f)).ToArray();
        if (reason != null)
        {
            PropertyGrid.ComponentFields(texts, Labels, (index, text, width) =>
            {
                PropertyGrid.Field(text, width, NotSet);
                ImGui.SetItemTooltip(MarketPlans.Fields[index].Tip + "\n\n" + reason);
            }, ToolbarItems.IconWidth + spacing);
            Lock(spacing);
            return;
        }

        PropertyGrid.ComponentFields(texts, Labels, (index, text, width) =>
        {
            var field = MarketPlans.Fields[index];
            // A value without an effect in the games is read-only, and its tooltip tells why (UI rule 4).
            if (MarketPlans.NoEffectReason(window.Session!.Edits, line, field) is { } noEffect)
            {
                PropertyGrid.Field(text, width, NotSet);
                ImGui.SetItemTooltip(field.Tip + "\n\n" + noEffect);
                return;
            }

            var edited = text;
            ImGui.SetNextItemWidth(width);
            var changed = ImGui.InputTextWithHint("##value", NotSet, ref edited,
                (uint)Encoding.UTF8.GetByteCount(edited) + 256);
            // The edit runs when the field ends, also when the window closes, as the other text fields do.
            grid.CommitField(MarketCommands.LineKey(line), changed, text, edited, (value, _) =>
            {
                // The command runner writes a problem of the edit to the Console, so the commit returns no error.
                window.Commands.Run(edits => MarketCommands.SetValue(edits, line, field, value));
                return null;
            });
            ImGui.SetItemTooltip(lower ? field.Tip + "\n\n" + LowerLineTip : field.Tip);
        }, ToolbarItems.IconWidth + spacing);

        ImGui.SameLine(0, spacing);
        // A buy-only line with a lower line already has the result of a remove.
        var noRemove = MarketPlans.NoRemoveReason(window.Session!.Edits, line);
        if (ToolbarItems.Button("remove", EditorIcons.TrashAlt, noRemove ?? RemoveTip(line, lower, replaces), noRemove == null))
        {
            window.Commands.Run(edits => MarketCommands.SetLine(edits, line, null));
        }
    }

    /// <summary>
    /// The result of a line that replaces a lower line, in plain words, with a revert button. The tooltip names the line
    /// that the save writes (AGENTS.md UI rules 9 and 10).
    /// </summary>
    /// <param name="reason">Why the line cannot change, or null when it can.</param>
    private void Override(MarketOverride change, string? reason)
    {
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        PropertyGrid.Field(change.Text, Math.Max(1, ImGui.GetContentRegionAvail().X - ToolbarItems.IconWidth - spacing));
        ImGui.SetItemTooltip(change.Tip);
        ImGui.SameLine(0, spacing);
        if (ToolbarItems.Button("revert", EditorIcons.Undo, reason ?? RevertTip, reason == null))
        {
            window.Commands.Run(edits => MarketCommands.Revert(edits, change.Line));
        }
    }

    /// <summary>The transaction type of a line: a dropdown with the three types, or a read-only field.</summary>
    /// <param name="reason">Why the line cannot change, or null when it can.</param>
    private void Transaction(MarketLine line, string? reason)
    {
        var text = TransactionText(line);
        if (reason != null)
        {
            ReadOnly(text, TransactionTip, reason);
            return;
        }

        // A type without an effect in the games is read-only, and its tooltip tells why (UI rule 4).
        if (MarketPlans.NoEffectReason(window.Session!.Edits, line, MarketLine.TransactionIndex) is { } noEffect)
        {
            PropertyGrid.Field(text);
            ImGui.SetItemTooltip(TransactionTip + "\n\n" + noEffect);
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
                var values = MarketPlans.DefaultValues(MarketPlans.Category(session.Edits, good));
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
