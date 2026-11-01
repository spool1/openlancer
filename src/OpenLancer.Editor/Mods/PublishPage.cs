using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The Settings tab of the Mods window (section 10.4 of the M11c design): the publish settings of the selected mod on
/// its <see cref="PublishDraft"/>. A changed field shows the blue bar and the blue label of an unsaved edit until Apply
/// (AGENTS.md UI rule 7). Each list is a <see cref="ReorderableList"/> (UI rule 3). Each field with a rule shows its
/// error below it with the reason (UI rule 2).
/// </summary>
internal sealed class PublishPage(EditorWindow window, ModsWindow mods)
{
    /// <summary>The texts of the dropdown "Fill with" of the permissions.</summary>
    internal static readonly (string Name, string Text)[] PermissionTexts =
    [
        ("Ask me first", "Ask the authors before you use the files of this mod in another mod."),
        ("Free to use with credit", "You can use the files of this mod in other free mods. Give credit to the authors."),
        ("Free to use without credit",
            "You can use the files of this mod in other free mods. You do not have to give credit."),
    ];

    private const string FormatHelp =
        "The description has a small format:\n" +
        "# Heading: a large heading\n" +
        "## Heading: a small heading\n" +
        "- item or * item: a bullet\n" +
        "1. item: a number\n" +
        "**bold**: bold text\n" +
        "[text](https://link): a link\n" +
        "An empty line: a new paragraph\n\n" +
        "The export writes BBCode for Nexus, plain text for the README and ASCII for FLMM.";

    private const string TierInfo =
        "Tier 1: a data mod for the retail game and OpenLancer. Tier 2: a mod that only OpenLancer runs.";

    // Sizes in unscaled points.
    private const float ThumbnailHeight = 54;
    private const float DescriptionLines = 10;

    private PublishDraft draft = null!;
    private IReadOnlyList<FieldProblem> problems = [];
    private PublishField? focus;

    // True after a Go to of the changelog: the first change of the entry of the current version gets the keyboard
    // (section 8.3 of the M11c design).
    private bool focusChange;
    private float labelWidth;

    /// <summary>Goes to a field at the next draw: its section opens, the page scrolls to it, and it gets the
    /// keyboard.</summary>
    public void Focus(PublishField field) => focus = field;

    public void Draw(ModRow row, PublishDraft current)
    {
        draft = current;
        problems = draft.Problems();
        if (!ImGui.BeginChild("##settings", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y)))
        {
            ImGui.EndChild();
            return;
        }

        // The ids of the fields and of the changelog entries include the mod folder, so that another mod does not get
        // their state, for example an open entry.
        ImGui.PushID(draft.ModFolder);
        Notes();
        NameAndVersion(row);
        Targets();
        PageTexts(row);
        Screenshots();
        CreditsAndPermissions();
        NexusPage();
        ImGui.PopID();
        ImGui.EndChild();
        Buttons(row);
    }

    /// <summary>The read-only reasons and the problems of the read of publish.json.</summary>
    private void Notes()
    {
        if (draft.ManifestReadOnly is { } manifest)
        {
            CardLayout.Note(EditorIcons.Lock, EditorTheme.Warning, manifest);
        }

        if (draft.SettingsReadOnly is { } settings)
        {
            CardLayout.Note(EditorIcons.Lock, EditorTheme.Warning, settings);
        }

        foreach (var problem in draft.LoadProblems.Where(p => p != draft.SettingsReadOnly))
        {
            CardLayout.Note(EditorIcons.ExclamationTriangle, EditorTheme.Warning, problem);
        }
    }

    private void NameAndVersion(ModRow row)
    {
        if (!Section("name", "Name and version",
                "What players see. OpenLancer writes these fields to mod.json. The README, the FLMM script and the Nexus " +
                "page use them.", PublishField.DisplayName, PublishField.Version, PublishField.Authors))
        {
            return;
        }

        var manifestLock = draft.ManifestReadOnly;
        Row("Display name", "The name that players see on Nexus, in FLMM and in the README. FLMM shows this name in " +
                            "its list. The file names keep the mod id.", PublishField.DisplayName, () =>
        {
            var name = draft.Values.Name;
            ImGui.BeginDisabled(manifestLock != null);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##name", ModWorkspace.StarterModName, ref name, PublishLimits.NameLength))
            {
                draft.Values = draft.Values with { Name = name };
            }

            ImGui.EndDisabled();
        });
        Row("Mod id", "The internal name of the mod and the name of its folder. The package files and the text DLL of " +
                      "the mod use it.", null, () =>
        {
            var renameWidth = ImGui.CalcTextSize("Rename...").X + 2 * ImGui.GetStyle().FramePadding.X;
            PropertyGrid.Field(draft.ModId,
                MathF.Max(1, ImGui.GetContentRegionAvail().X - renameWidth - ImGui.GetStyle().ItemSpacing.X));
            ImGui.SameLine();
            var block = mods.RenameBlock(row);
            ImGui.BeginDisabled(block != null);
            if (ImGui.Button("Rename..."))
            {
                mods.Rename(row);
            }

            ImGui.EndDisabled();
            ItemTooltip.Show(block ??
                             "Give the mod a new id and a new folder name. Do this only before the first release.");
        });
        Row("Version", PublishMessages.VersionRule, PublishField.Version, () => Version(row, manifestLock));
        Row("Authors", "The names of the authors. The README, the FLMM script and the Nexus page show them.",
            PublishField.Authors, () =>
            {
                if (TextList("authors", draft.Values.Settings.Authors, PublishField.Authors, "Add author") is { } authors)
                {
                    Set(s => s with { Authors = authors });
                }
            });
        EndSection();
    }

    /// <summary>The version field and the dropdown Increase version (section 8.3 of the M11c design).</summary>
    private void Version(ModRow row, string? manifestLock)
    {
        var version = draft.Values.Version;
        const string increase = "Increase version";
        var buttonWidth = ImGui.CalcTextSize(increase).X + ImGui.GetFrameHeight() + 2 * ImGui.GetStyle().FramePadding.X;
        ImGui.BeginDisabled(manifestLock != null);
        ImGui.SetNextItemWidth(MathF.Max(1, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        if (ImGui.InputTextWithHint("##version", "1.0.0", ref version, ModVersion.MaxSafeLength + 1))
        {
            draft.Values = draft.Values with { Version = version.Trim() };
        }

        ImGui.SameLine();
        if (ImGui.Button($"{increase} {EditorIcons.CaretDown}"))
        {
            ImGui.OpenPopup("##increase");
        }

        ImGui.EndDisabled();
        if (!ImGui.BeginPopup("##increase"))
        {
            return;
        }

        foreach (var (part, tooltip) in new[]
                 {
                     (VersionPart.Patch, "Fixes only. Players can install it over the version before."),
                     (VersionPart.Minor, "New content. Old saves still load."),
                     (VersionPart.Major,
                         "Changes that break old saves or other mods. Players start a new game or remove old items."),
                 })
        {
            var next = ModVersion.Next(draft.Values.Version, part, row.History.Versions);
            if (MenuItems.Item(next == null ? part.ToString() : $"{part}: {next}",
                    next == null ? PublishMessages.VersionRule : null, tooltip))
            {
                draft.IncreaseVersion(part, row.History.Versions);
                focus = PublishField.Changelog;
            }
        }

        ImGui.EndPopup();
    }

    private void Targets()
    {
        if (!Section("targets", "Target games",
                "The games that get a package. A target needs a linked game of that kind for the checks and the export.",
                PublishField.Targets, PublishField.FormatVersion))
        {
            return;
        }

        var manifestLock = draft.ManifestReadOnly;
        var formatOne = draft.Values.FormatVersion < ModManifest.CurrentFormatVersion;
        Row("Target games", "The game versions that the export makes packages for. Clear a target that you do not " +
                            "publish for.", PublishField.Targets, () =>
        {
            foreach (var target in ExportTarget.All)
            {
                var on = draft.Values.Targets.Contains(target.Id);
                // A tier-2 mod can clear a retail target, but not check one.
                var tierBlock = draft.Tier == ModManifest.RuntimeTier && target.IsRetail
                    ? $"A tier-{ModManifest.RuntimeTier} mod runs only on OpenLancer."
                    : null;
                var block = manifestLock ?? (formatOne ? PublishMessages.FormatVersion2Fix : null) ??
                            (on ? null : tierBlock);
                ImGui.BeginDisabled(block != null);
                if (ImGui.Checkbox(target.Name, ref on))
                {
                    var targets = ExportTarget.All.Where(t => t == target ? on : draft.Values.Targets.Contains(t.Id))
                        .Select(t => t.Id).ToList();
                    draft.Values = draft.Values with { Targets = targets };
                }

                ImGui.EndDisabled();
                ItemTooltip.Show(block ?? tierBlock ?? target.Game);
            }

            if (formatOne)
            {
                ImGui.BeginDisabled(manifestLock != null);
                if (ImGui.Button("Use format version 2"))
                {
                    draft.UseFormatVersion2();
                }

                ImGui.EndDisabled();
                ItemTooltip.Show(manifestLock ?? PublishMessages.FormatVersion2Fix);
            }
        });
        // The tier is read-only, with an info icon (section 8.2 of the M11c design).
        Row("Tier", TierInfo, null, () =>
        {
            var icon = EditorIcons.InfoCircle.ToString();
            var iconWidth = ImGui.CalcTextSize(icon).X + ImGui.GetStyle().ItemSpacing.X;
            PropertyGrid.Field($"Tier {draft.Tier}", MathF.Max(1, ImGui.GetContentRegionAvail().X - iconWidth));
            ImGui.SameLine();
            ImGui.TextDisabled(icon);
            ImGui.SetItemTooltip(TierInfo);
        });
        EndSection();
    }

    private void PageTexts(ModRow row)
    {
        if (!Section("texts", "Page texts", "The texts of the Nexus page and of the README of each package.",
                PublishField.Summary, PublishField.Description, PublishField.Changelog))
        {
            return;
        }

        var locked = draft.SettingsReadOnly != null;
        Row("Summary", "One or two sentences. Nexus shows the summary below the name of the mod.", PublishField.Summary, () =>
        {
            var summary = draft.Values.Settings.Summary;
            ImGui.BeginDisabled(locked);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##summary", ref summary, 4096))
            {
                Set(s => s with { Summary = PageText.RemoveControl(summary, keepLineEnds: false) });
            }

            ImGui.EndDisabled();
            var count = draft.Values.Settings.Summary.Length;
            ImGui.PushFont(null, EditorTheme.CaptionSize);
            ImGui.TextColored(count > PublishLimits.SummaryLength ? EditorTheme.Warning : EditorTheme.TextDim,
                $"{count} of {PublishLimits.SummaryLength} characters");
            ImGui.PopFont();
        });
        Row("Description", "The text of the Nexus page and of the About section of the README.", PublishField.Description, () =>
        {
            var description = draft.Values.Settings.Description;
            ImGui.BeginDisabled(locked);
            var height = DescriptionLines * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
            if (ImGui.InputTextMultiline("##description", ref description, PublishLimits.DescriptionBytes + 1,
                    new Vector2(-ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X, height)))
            {
                Set(s => s with { Description = description });
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(EditorIcons.QuestionCircle.ToString());
            ImGui.SetItemTooltip(FormatHelp);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Field);
            if (ImGui.BeginChild("##preview", new Vector2(-1, 0), ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.Borders |
                                                                 ImGuiChildFlags.AlwaysUseWindowPadding))
            {
                PageTextView.Draw(window, draft.Values.Settings.Description);
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
        });
        Row("Changelog", "The changes of each version, newest first. The texts of an export hold the entries of the " +
                         "exported version and the lower versions.", PublishField.Changelog, () => Changelog(row, locked));
        EndSection();
    }

    /// <summary>The changelog: one entry for each version, newest first, with a list of changes (decision D576). The
    /// entry of the current version is open. The entry of a version with the mark "published" is read-only: its fields,
    /// its drag handle, its move buttons and Remove are disabled. Add entry adds the new entry at the top.</summary>
    private void Changelog(ModRow row, bool locked)
    {
        var current = draft.Values.Version;
        var focusCurrent = focusChange;
        focusChange = false;
        var changed = ReorderableList.Draw("changelog", draft.Values.Settings.Changelog, (i, entry, width) =>
        {
            var published = PublishedText(row, entry);
            var isCurrent = ModVersion.TextComparer.Equals(entry.Version, current);
            var goTo = focusCurrent && isCurrent;
            ImGui.SetNextItemOpen(isCurrent, goTo ? ImGuiCond.Always : ImGuiCond.Once);
            var title = entry.Version.Length > 0 ? $"Version {entry.Version}" : "New entry";
            var open = ImGui.TreeNodeEx(published != null ? $"{EditorIcons.Lock}  {title}###entry" : $"{title}###entry",
                ImGuiTreeNodeFlags.SpanAvailWidth);
            if (published != null)
            {
                ImGui.SetItemTooltip(published);
            }

            if (!open)
            {
                RowProblems(PublishField.Changelog, i);
                return entry;
            }

            ImGui.BeginDisabled(published != null);
            var version = entry.Version;
            ImGui.SetNextItemWidth(width - ImGui.GetTreeNodeToLabelSpacing());
            if (ImGui.InputTextWithHint("##version", "Version", ref version, ModVersion.MaxSafeLength + 1))
            {
                entry = entry with { Version = version.Trim() };
            }

            ImGui.SetItemTooltip("The version of these changes, in the form 1.2.3.");
            if (TextList("changes", entry.Changes, null, "Add change", multiLine: true, focusFirst: goTo) is
                { } changes)
            {
                entry = entry with { Changes = changes };
            }

            ImGui.EndDisabled();
            RowProblems(PublishField.Changelog, i);
            ImGui.TreePop();
            return entry;
        }, null, null, locked ? draft.SettingsReadOnly : null, entry => PublishedText(row, entry));
        if (changed != null)
        {
            Set(s => s with { Changelog = changed });
        }

        ImGui.BeginDisabled(locked);
        if (ImGui.Button($"{EditorIcons.Plus}  Add entry"))
        {
            draft.AddChangelogEntry();
        }

        ImGui.EndDisabled();
        ItemTooltip.Show(draft.SettingsReadOnly ??
                         "Add an entry at the top. The changelog lists the newest version first.");
    }

    /// <summary>The reason why a changelog entry is read-only, or null: its version has the mark "published" (section
    /// 10.4 of the M11c design).</summary>
    private static string? PublishedText(ModRow row, ChangelogEntry entry) =>
        entry.Version.Length > 0 && row.History.IsPublished(entry.Version)
            ? $"Version {entry.Version} is on Nexus. Change the text on Nexus, or add the change to the next version."
            : null;

    /// <summary>The screenshots: a thumbnail, the file name and a caption for each image (decisions D586 and
    /// D592).</summary>
    private void Screenshots()
    {
        if (!Section("screenshots", "Screenshots",
                "Images for the Nexus page. OpenLancer copies them into the export folder. The first image is the main " +
                "image of the list.", PublishField.Screenshots))
        {
            return;
        }

        var locked = draft.SettingsReadOnly;
        Row("Screenshots", "PNG or JPEG images that you made, with 8,192 pixels or fewer on each side. Apply copies new " +
                           "images into publish\\screenshots of the mod.", PublishField.Screenshots, () =>
        {
            var imports = draft.Imports;
            var changed = ReorderableList.Draw("screenshots", draft.Values.Settings.Screenshots, (i, shot, width) =>
            {
                // An image that Apply copies comes from the file dialog. An image of the list must pass the path rule,
                // so that a hand-written publish.json cannot show a file outside the mod (decision D586).
                var source = imports.GetValueOrDefault(shot.File);
                Thumbnail(source != null
                    ? mods.Thumbnails.Get(source, null)
                    : mods.Thumbnails.Of(draft.ModFolder, shot.File));
                ImGui.SameLine();
                ImGui.BeginGroup();
                var fieldWidth = MathF.Max(1, ImGui.GetContentRegionAvail().X);
                ImGui.Text(TextFit.CutRight(Path.GetFileName(shot.File), fieldWidth));
                ImGui.SetItemTooltip(source != null ? $"{source}\nApply copies the image to {shot.File}." : shot.File);
                var caption = shot.Caption;
                ImGui.SetNextItemWidth(fieldWidth);
                if (ImGui.InputTextWithHint("##caption", "Caption", ref caption, 512))
                {
                    shot = shot with { Caption = PageText.RemoveControl(caption, keepLineEnds: false) };
                }

                ImGui.SetItemTooltip("The text of the image on the Nexus page.");
                RowProblems(PublishField.Screenshots, i);
                ImGui.EndGroup();
                return shot;
            }, null, null, locked);
            if (changed != null)
            {
                Set(s => s with { Screenshots = changed });
            }

            var repo = mods.IsRepoMod(draft.ModFolder);
            ImGui.BeginDisabled(locked != null || repo);
            if (ImGui.Button($"{EditorIcons.Images}  Add images..."))
            {
                mods.AddImages();
            }

            ImGui.EndDisabled();
            ItemTooltip.Show(locked ?? (repo
                ? PublishMessages.ImageInRepoMod
                : "Pick PNG or JPEG images. OpenLancer checks each image."));
        });
        EndSection();
    }

    /// <summary>A thumbnail of an image in the height of a row, or the image glyph while it decodes or when it does
    /// not pass the rule.</summary>
    private static void Thumbnail((ImTextureRef Ref, Vector2 Size)? thumbnail)
    {
        var height = MathF.Truncate(ThumbnailHeight * ImGuiHelper.Scale);
        if (thumbnail is { } image)
        {
            var width = MathF.Round(height * image.Size.X / MathF.Max(1, image.Size.Y));
            var (uv0, uv1) = ScreenshotThumbnails.Uv;
            ImGui.Image(image.Ref, new Vector2(MathF.Min(width, 2 * height), height), uv0, uv1);
            return;
        }

        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(height, height));
        Glyphs.Draw(ImGui.GetWindowDrawList(), EditorIcons.Image, height / 2, start + new Vector2(height / 2),
            ImGui.GetColorU32(EditorTheme.TextDim));
    }

    private void CreditsAndPermissions()
    {
        if (!Section("credits", "Credits and permissions", "Who helped, and what other modders can do with your files.",
                PublishField.Credits, PublishField.Permissions))
        {
            return;
        }

        var locked = draft.SettingsReadOnly;
        Row("Credits", "The people who helped, and what they did. The README and the Nexus page show them.",
            PublishField.Credits, () =>
            {
                var changed = ReorderableList.Draw("credits", draft.Values.Settings.Credits, (i, credit, width) =>
                {
                    var half = MathF.Max(1, (width - ImGui.GetStyle().ItemSpacing.X) / 2);
                    var name = credit.Name;
                    ImGui.SetNextItemWidth(half);
                    if (ImGui.InputTextWithHint("##name", "Name", ref name, 128))
                    {
                        credit = credit with { Name = PageText.RemoveControl(name, keepLineEnds: false) };
                    }

                    ImGui.SetItemTooltip("Name: the name of the person or the group.");
                    ImGui.SameLine();
                    var what = credit.For;
                    ImGui.SetNextItemWidth(half);
                    if (ImGui.InputTextWithHint("##for", "What they did", ref what, 256))
                    {
                        credit = credit with { For = PageText.RemoveControl(what, keepLineEnds: false) };
                    }

                    ImGui.SetItemTooltip("What they did: for example \"Test flights\" or \"The ship model\".");
                    RowProblems(PublishField.Credits, i);
                    return credit;
                }, () => new PublishCredit("", ""), "Add credit", locked);
                if (changed != null)
                {
                    Set(s => s with { Credits = changed });
                }
            });
        Row("Permissions", "What other modders can do with the files of this mod.", PublishField.Permissions, () =>
        {
            var permissions = draft.Values.Settings.Permissions;
            ImGui.BeginDisabled(locked != null);
            var height = 3 * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
            if (ImGui.InputTextMultiline("##permissions", ref permissions, 4096, new Vector2(-1, height)))
            {
                Set(s => s with { Permissions = permissions });
            }

            if (ImGui.Button($"Fill with {EditorIcons.CaretDown}"))
            {
                ImGui.OpenPopup("##fill");
            }

            ImGui.EndDisabled();
            ItemTooltip.Show(locked ?? "Replace the text with a common text.");
            if (ImGui.BeginPopup("##fill"))
            {
                foreach (var (name, text) in PermissionTexts)
                {
                    if (ImGui.MenuItem(name))
                    {
                        Set(s => s with { Permissions = text });
                    }

                    ImGui.SetItemTooltip(text);
                }

                ImGui.EndPopup();
            }
        });
        EndSection();
    }

    private void NexusPage()
    {
        if (!Section("nexus", "Nexus page", "Fields of the Nexus page that only the page uses.", PublishField.PageUrl,
                PublishField.Category, PublishField.Tags, PublishField.Requirements))
        {
            return;
        }

        var locked = draft.SettingsReadOnly;
        Row("Page link", "The link of the page of the mod, after the upload. FLMM and the README show it.",
            PublishField.PageUrl, () =>
            {
                var url = draft.Values.Settings.PageUrl;
                ImGui.BeginDisabled(locked != null);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##page", "https://www.nexusmods.com/...", ref url, 512))
                {
                    Set(s => s with { PageUrl = url.Trim() });
                }

                ImGui.EndDisabled();
            });
        Row("Nexus category", $"The category of the mod on Nexus. The Freelancer mods on Nexus use " +
                              $"\"{PublishSettings.DefaultCategory}\".", PublishField.Category, () =>
        {
            var category = draft.Values.Settings.Category;
            ImGui.BeginDisabled(locked != null);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##category", PublishSettings.DefaultCategory, ref category, 128))
            {
                Set(s => s with { Category = PageText.RemoveControl(category, keepLineEnds: false) });
            }

            ImGui.EndDisabled();
        });
        Row("Tags", "The tags of the Nexus page, one tag in each row. Pick the matching tags on the site too.",
            PublishField.Tags, () =>
            {
                if (TextList("tags", draft.Values.Settings.Tags, PublishField.Tags, "Add tag") is { } tags)
                {
                    Set(s => s with { Tags = tags });
                }
            });
        Row("Other requirements", "Other mods or tools that players need. The export adds the game of each target " +
                                  "itself.", PublishField.Requirements, () =>
        {
            var changed = ReorderableList.Draw("requirements", draft.Values.Settings.Requirements, (i, requirement, width) =>
            {
                var third = MathF.Max(1, (width - 2 * ImGui.GetStyle().ItemSpacing.X) / 3);
                var name = requirement.Name;
                ImGui.SetNextItemWidth(third);
                if (ImGui.InputTextWithHint("##name", "Name", ref name, 128))
                {
                    requirement = requirement with { Name = PageText.RemoveControl(name, keepLineEnds: false) };
                }

                ImGui.SetItemTooltip("Name: the name of the mod or the tool.");
                ImGui.SameLine();
                var url = requirement.Url;
                ImGui.SetNextItemWidth(third);
                if (ImGui.InputTextWithHint("##url", "Link", ref url, 512))
                {
                    requirement = requirement with { Url = url.Trim() };
                }

                ImGui.SetItemTooltip("Link: the page of the mod or the tool, with https://. It can be empty.");
                ImGui.SameLine();
                var note = requirement.Note;
                ImGui.SetNextItemWidth(third);
                if (ImGui.InputTextWithHint("##note", "Note", ref note, 256))
                {
                    requirement = requirement with { Note = PageText.RemoveControl(note, keepLineEnds: false) };
                }

                ImGui.SetItemTooltip("Note: for example \"Optional, for Freelancer 1.1\".");
                RowProblems(PublishField.Requirements, i);
                return requirement;
            }, () => new PublishRequirement("", "", ""), "Add requirement", locked);
            if (changed != null)
            {
                Set(s => s with { Requirements = changed });
            }
        });
        EndSection();
    }

    /// <summary>Revert and Apply at the bottom right. A repo mod without the allowance has "Allow edits of this repo
    /// mod" in place of Apply (decision D584).</summary>
    private void Buttons(ModRow row)
    {
        var errors = problems.Where(p => p.Severity == DiagnosticSeverity.Error).ToList();
        var repoBlock = mods.IsRepoMod(draft.ModFolder) && !mods.RepoAllowed(draft.ModFolder);
        var block = draft.HasChanges ? mods.WriteBlock(row.Folder) : null;
        var allow = "Allow edits of this repo mod";
        var applyLabel = repoBlock ? allow : "Apply";
        var width = CardLayout.ButtonWidth("Revert") + CardLayout.ButtonWidth(applyLabel) + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - width));
        ImGui.BeginDisabled(!draft.HasChanges);
        if (ImGui.Button("Revert", new Vector2(CardLayout.ButtonWidth("Revert"), 0)))
        {
            mods.RevertDraft();
        }

        ImGui.EndDisabled();
        ItemTooltip.Show("Drop the unapplied changes.");
        ImGui.SameLine();
        if (repoBlock)
        {
            if (ImGui.Button(allow, new Vector2(CardLayout.ButtonWidth(allow), 0)))
            {
                mods.AllowRepo(draft.ModFolder);
            }

            ImGui.SetItemTooltip(PublishMessages.RepoMod);
            return;
        }

        var enabled = draft.HasChanges && errors.Count == 0 && block == null;
        ImGui.BeginDisabled(!enabled);
        EditorTheme.PushPrimaryButtonColors();
        if (ImGui.Button("Apply", new Vector2(CardLayout.ButtonWidth("Apply"), 0)))
        {
            mods.ApplyDraft();
        }

        EditorTheme.PopPrimaryButtonColors();
        ImGui.EndDisabled();
        ItemTooltip.Show(block ?? (errors.Count > 0
            ? $"Correct the errors first. {errors[0].Message}"
            : "Write the publish settings into mod.json and publish.json."));
    }

    /// <summary>A list of texts, one row for each text. Returns the new list after a change, else null.</summary>
    /// <param name="field">The field of the problems of the rows, or null.</param>
    /// <param name="multiLine">True for texts that keep their line ends, for example the changes of a version.</param>
    /// <param name="focusFirst">True to give the keyboard to the first text, for example the first change of a new
    /// version.</param>
    private IReadOnlyList<string>? TextList(string id, IReadOnlyList<string> items, PublishField? field, string add,
        bool multiLine = false, bool focusFirst = false) =>
        ReorderableList.Draw(id, items, (i, text, width) =>
        {
            if (focusFirst && i == 0)
            {
                ImGui.SetKeyboardFocusHere();
            }

            ImGui.SetNextItemWidth(width);
            if (multiLine)
            {
                var lines = Math.Max(1, text.Count(c => c == '\n') + 1);
                var height = lines * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
                if (ImGui.InputTextMultiline("##text", ref text, 2048, new Vector2(width, height)))
                {
                    text = PageText.RemoveControl(text, keepLineEnds: true);
                }
            }
            else if (ImGui.InputText("##text", ref text, 256))
            {
                text = PageText.RemoveControl(text, keepLineEnds: false);
            }

            if (field is { } problemsOf)
            {
                RowProblems(problemsOf, i);
            }

            return text;
        }, () => "", add, draft.SettingsReadOnly);

    /// <summary>Changes the publish settings of the draft.</summary>
    private void Set(Func<PublishSettings, PublishSettings> change) =>
        draft.Values = draft.Values with { Settings = change(draft.Values.Settings) };

    /// <summary>
    /// A section header with its info icon (AGENTS.md UI rule 10), and the table of its rows. Returns true when the
    /// section is open. Then draw its rows and call <see cref="EndSection"/>.
    /// </summary>
    /// <param name="fields">The fields of the section. A Go to of one of them opens it.</param>
    private bool Section(string id, string title, string info, params PublishField[] fields)
    {
        if (focus is { } field && fields.Contains(field))
        {
            ImGui.SetNextItemOpen(true);
        }

        var changed = fields.Any(draft.IsChanged);
        var open = ImGui.CollapsingHeader($"{title}{(changed ? " *" : "")}###{id}",
            ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
        // The info icon is at the right end of the header, and it shows in each state of the section.
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var icon = EditorIcons.InfoCircle.ToString();
        var padding = ImGui.GetStyle().FramePadding;
        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(max.X - ImGui.CalcTextSize(icon).X - padding.X, min.Y + padding.Y));
        ImGui.TextDisabled(icon);
        ImGui.SetItemTooltip(info);
        if (!open)
        {
            return false;
        }

        labelWidth = PropertyGrid.LabelWidth(ImGui.GetContentRegionAvail().X, ImGuiHelper.Scale);
        if (!ImGui.BeginTable($"##{id}", 2, ImGuiTableFlags.NoSavedSettings))
        {
            return false;
        }

        ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, labelWidth);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    private static void EndSection()
    {
        ImGui.EndTable();
        ImGui.Spacing();
    }

    /// <summary>
    /// A row: the label with its tooltip, and the value. A changed field has the blue bar and the blue label of an
    /// unsaved edit. The errors and the warnings of the field show below the value, with the reason.
    /// </summary>
    private void Row(string label, string tooltip, PublishField? field, Action value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(label);
        ImGui.AlignTextToFramePadding();
        var min = ImGui.GetCursorScreenPos();
        var changed = field is { } f && draft.IsChanged(f);
        if (changed)
        {
            PropertyGrid.ChangedBar(min);
        }

        ImGui.PushStyleColor(ImGuiCol.Text, changed ? EditorTheme.AccentText : EditorTheme.Label);
        ImGui.Text(label);
        ImGui.PopStyleColor();
        ImGui.SetItemTooltip(changed ? $"{tooltip}\n\nYou did not apply this change. Click Apply." : tooltip);
        ImGui.TableNextColumn();
        if (field != null && focus == field)
        {
            focus = null;
            ImGui.SetScrollHereY(0.2f);
            // The changelog gives the keyboard to the first change of the current version, not to the first drag
            // handle of the list.
            if (field == PublishField.Changelog)
            {
                focusChange = true;
            }
            else
            {
                ImGui.SetKeyboardFocusHere();
            }
        }

        value();
        if (field is { } of)
        {
            foreach (var problem in problems.Where(p => p.Field == of && p.Index < 0))
            {
                Problem(problem);
            }
        }

        ImGui.PopID();
    }

    /// <summary>The problems of one row of a list field.</summary>
    private void RowProblems(PublishField field, int index)
    {
        foreach (var problem in problems.Where(p => p.Field == field && p.Index == index))
        {
            Problem(problem);
        }
    }

    private static void Problem(FieldProblem problem)
    {
        var error = problem.Severity == DiagnosticSeverity.Error;
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(error ? EditorTheme.Error : EditorTheme.Warning,
            $"{(error ? EditorIcons.TimesCircle : EditorIcons.ExclamationTriangle)}  {problem.Message}");
        ImGui.PopTextWrapPos();
    }
}
