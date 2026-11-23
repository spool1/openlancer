using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of the bases (section C.5 of the bases design). The agent tools and the editor call them. Each one gives
/// the plan that its caller runs.
/// </summary>
/// <remarks>Each command wraps a plan of <see cref="BasePlans"/> and does not copy its rules. A command never throws for
/// a rule of the plan. It gives a plan that refuses, with the reason. A plan reads the edit view when it is made, so make
/// it and run it at once.</remarks>
internal static class BaseCommands
{
    /// <summary>
    /// Plans a new base from a template base. See <see cref="BasePlans.Create"/>. The texts of the game give the name
    /// texts of the copied characters, for example the name of a bartender of the template.
    /// </summary>
    /// <param name="texts">The texts of the items of the edit view.</param>
    public static BasePlan Create(EditSession session, BaseSpec spec, ItemTexts texts) =>
        BasePlans.Create(session, spec with
        {
            Text = (section, key) => texts.TextOf(section, key) is { Length: > 0 } text ? text : null,
        });
}
