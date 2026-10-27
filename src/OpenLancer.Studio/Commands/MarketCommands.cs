using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of the market lines of a good (section 4.0.8 of the M6b design). They set a value, the transaction
/// type or all values of a line, remove a line, and make a base sell a good. The "Markets" section of the Inspector
/// calls them. Each one gives the plan that its caller runs.
/// </summary>
/// <remarks>Each command wraps an edit of <see cref="MarketEdits"/> and does not copy its rules. A command never throws
/// for a rule of the edit. It gives a plan that refuses, with the reason. A plan reads the edit view when it is made, so
/// make it and run it at once.</remarks>
internal static class MarketCommands
{
    /// <summary>Plans one value of a line from the text of its field. See <see cref="MarketEdits.SetValue"/>.</summary>
    public static EditPlan SetValue(EditSession session, MarketLine line, MarketValue field, string text) =>
        CommandPlan.Of($"Set {field.Name}", () => MarketEdits.SetValue(session, line, field, text), [line.Section]);

    /// <summary>Plans the values of a line after the good, or the remove of the line when <paramref name="values"/> is
    /// null. See <see cref="MarketEdits.SetLine"/>.</summary>
    public static EditPlan SetLine(EditSession session, MarketLine line, string? values) =>
        CommandPlan.Of(values == null ? $"Remove {line.Base}" : $"Set {line.Base}",
            () => MarketEdits.SetLine(session, line, values), [line.Section]);

    /// <summary>Plans the transaction type of a line. See <see cref="MarketEdits.SetTransaction"/>.</summary>
    public static EditPlan SetTransaction(EditSession session, MarketLine line, MarketTransaction type) =>
        CommandPlan.Of("Set transaction type", () => MarketEdits.SetTransaction(session, line, type), [line.Section]);

    /// <summary>Plans a line that makes a base sell a good. See <see cref="MarketEdits.AddToBase"/>.</summary>
    /// <param name="values">The values after the good (see <see cref="MarketEdits.DefaultValues"/>).</param>
    public static EditPlan AddToBase(EditSession session, string good, string baseNickname, string values) =>
        CommandPlan.Of($"Sell {good} at {baseNickname}", () => MarketEdits.AddToBase(session, good, baseNickname, values));
}
