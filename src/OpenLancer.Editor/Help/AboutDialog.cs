using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Editor.Dialogs;

namespace OpenLancer.Editor.Help;

/// <summary>
/// Help &gt; About OpenLancer: the version, the commit, the engine commit, the license notice and the non-affiliation
/// note. Copy copies the same text, so a tester can paste the version and the commits into a report.
/// </summary>
internal sealed class AboutDialog : ModalDialog
{
    /// <summary>The commit line of a build without a commit.</summary>
    public const string NoCommit = "not in this build";

    /// <summary>The license notice, as in the LICENSES part of README-FIRST.txt.</summary>
    public const string License =
        "OpenLancer is free software under the MIT License (LICENSE.txt). It contains third-party software. NOTICE.txt " +
        "and the folder \"licenses\" name each part and its license.";

    /// <summary>The non-affiliation note, as at the top of README-FIRST.txt.</summary>
    public const string Note =
        "OpenLancer is a mod editor for Freelancer (2003). It is an independent fan project. It is not affiliated with, " +
        "endorsed by or sponsored by Microsoft, Digital Anvil or the Librelancer project. OpenLancer uses the name " +
        "\"Freelancer\" only to identify the game. All trademarks belong to their owners.";

    private string text = "";

    protected override string Title => "About OpenLancer";

    /// <summary>The text of the window and of Copy.</summary>
    public static string Text(BuildVersion build) =>
        $"OpenLancer {build.Version}{(build.IsDevelopment ? " (development build)" : "")}\n" +
        $"Commit: {build.Commit ?? NoCommit}\n" +
        $"Engine commit: {build.EngineCommit ?? NoCommit}\n" +
        "\n" +
        $"{License}\n" +
        "\n" +
        Note;

    /// <summary>Shows the window with the facts of this build.</summary>
    public void Show()
    {
        text = Text(BuildInfo.Current);
        Open();
    }

    protected override void Body()
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                ImGui.Spacing();
            }
            else
            {
                ImGui.TextWrapped(line);
            }
        }

        ImGui.Spacing();
        if (Button("Copy", primary: true))
        {
            ImGui.SetClipboardText(text);
        }

        ImGui.SameLine();
        if (Button("Close"))
        {
            Close();
        }
    }
}
