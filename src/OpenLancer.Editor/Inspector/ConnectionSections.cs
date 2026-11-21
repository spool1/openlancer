using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The sections of the Jump connection and Trade lane ring components on the page of an object, and the completeness
/// panel of a jump gate, a jump hole or a trade lane ring at the top of the page (AGENTS.md UI rules 10 and 12). The
/// sections show the models of <see cref="ConnectionComponents"/>, which read W. So a new pair and a new lane show at
/// once. Each control calls the plan of an agent tool through <see cref="Editing.SceneEdits"/>: the lock dropdown calls
/// <see cref="ConnectionPlans.SetLock"/>, and the ring buttons call <see cref="LanePlans"/>. Each click is one entry of
/// the edit history.
/// </summary>
/// <remarks>The <c>goto</c>, <c>prev_ring</c> and <c>next_ring</c> rows stay bound and locked, so they show the lines and
/// the reason of the lock. The end name is the bound "Lane name" row, which edits the text as one entry of the edit
/// history. It shows only on an end ring, because the HUD reads only the names of the end rings.</remarks>
internal sealed class ConnectionSections(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The settings key of the Jump connection section.</summary>
    public const string JumpKey = "SystemObject.JumpConnection";

    /// <summary>The settings key of the Trade lane ring section.</summary>
    public const string RingKey = "SystemObject.TradeLaneRing";

    /// <summary>The choices of the lock dropdown, in order, with their labels.</summary>
    internal static readonly IReadOnlyList<(JumpLock Lock, string Label)> LockChoices =
    [
        (JumpLock.None, "No lock"),
        (JumpLock.Player, "Locked for the player"),
        (JumpLock.Npc, "Locked for NPCs"),
        (JumpLock.Both, "Locked for the player and NPCs"),
    ];

    /// <summary>The help of the lock dropdown.</summary>
    internal const string LockHelp =
        "The lock at the start of a new game. A mission can unlock the gate with Act_LockDock. The route tables leave out " +
        "a gate that the player cannot use, also after the unlock. A change gives the same lock to the partner when the " +
        "partner names this gate in its goto line.";

    /// <summary>Why a control waits while a field of the Inspector takes an edit.</summary>
    internal const string FieldEditReason = "Wait until the field edit ends.";

    /// <summary>The help of the "Lane name" row of an end ring.</summary>
    internal const string LaneNameHelp =
        "The name of this end of the lane. The HUD shows the lane as the start name, an arrow and the end name.";

    /// <summary>The tooltip of "Insert ring after".</summary>
    internal const string InsertTip = "Adds a ring after this ring. The links, the end name and the lane zone follow.";

    /// <summary>The tooltip of "Repair lane".</summary>
    internal const string RepairTip =
        "Writes the links of the lane again in the order of the rings on the line, and moves the end names to the end " +
        "rings. The positions stay.";

    /// <summary>The tooltip of "Delete ring".</summary>
    internal const string DeleteTip = "Deletes this ring, as the Delete key does. Its neighbors link to each other.";

    /// <summary>The tooltip of "Go to partner".</summary>
    internal const string PartnerTip = "Opens the system of the partner in the Scene and selects the partner.";

    private Page? page;

    /// <summary>Why the controls of the sections take no click now, or null: the profile takes no edit, or a field of
    /// the Inspector takes an edit.</summary>
    private string? EditorReason => window.Session?.NoEditReason ?? (window.IsEditing ? FieldEditReason : null);

    /// <summary>The models of the page of a section, with the edit view that they came from.</summary>
    /// <param name="Revision">The revision of the edit view (see <see cref="EditSession.Revision"/>).</param>
    internal sealed record Page(EditSession Session, SectionRef Section, long Revision, JumpConnectionInfo? Jump,
        TradeLaneRingInfo? Ring, CompletenessReport? Completeness)
    {
        /// <summary>The models of a section in the edit view now. Each model runs its plans once, and the completeness
        /// report reads the same models.</summary>
        public static Page Of(EditSession session, SectionRef section)
        {
            var jump = ConnectionComponents.Jump(session, section);
            var ring = ConnectionComponents.Ring(session, section);
            return new Page(session, section, session.Revision, jump, ring,
                ComponentCompleteness.Of(session, section, jump, ring));
        }
    }

    /// <summary>True when the page shows the "Lane name" row of a ring: on an end ring, or without a ring model. An
    /// object of another type with a link line has no ring model, and its bound rows follow its lines.</summary>
    internal static bool ShowsLaneName(TradeLaneRingInfo? ring) => ring?.IsEnd ?? true;

    /// <summary>The label of a lock in the dropdown.</summary>
    internal static string LockLabel(JumpLock locked) => LockChoices.First(c => c.Lock == locked).Label;

    /// <summary>The completeness panel of a jump gate, a jump hole or a trade lane ring at the top of its page. A missing
    /// step has "Go to", and a broken link of a lane has the fix "Repair lane".</summary>
    public void Completeness(SectionRef section)
    {
        if (PageOf(section)?.Completeness is not { } report || window.Session is not { } session)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Indent();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.Text(report.Title);
        ImGui.PopStyleColor();
        CompletenessPanel.Draw("connection-completeness", report, false, GoTo,
            session.NoEditReason == null
                ? step => step.Fix == ComponentCompleteness.RepairFix
                    ? [("Repair lane", () => window.SceneEdits.RepairLane(section))]
                    : []
                : null);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    /// <summary>
    /// The Jump connection section: the kind, the locked <c>goto</c> row, the target system and the exit object as
    /// links, "Go to partner", the traffic zones and the lock dropdown. Without a section of W, it shows the dock of the
    /// game data.
    /// </summary>
    /// <param name="show">True when the page shows the section (see <see cref="SystemObjectPage.DockingRows"/>).</param>
    public void Jump(SystemObject obj, SectionRef? section, bool show)
    {
        if (!show || !grid.BeginSection(JumpKey, EditorIcons.LifeRing, ObjectComponents.Name(ObjectComponent.JumpConnection),
                true, ConnectionComponents.JumpHelp))
        {
            return;
        }

        var dock = obj.Dock?.Kind == DockKinds.Jump ? obj.Dock : null;
        var system = dock?.Target is { } target ? window.Session?.GameData?.Items.Systems.Get(target) : null;
        var jump = section == null ? null : PageOf(section)?.Jump;
        if (!grid.IsBound)
        {
            grid.Reference("Destination system", ObjectKinds.StarSystem, dock?.Target, Name(system), system, "goto");
            grid.Text("Exit object", dock?.Exit, "goto");
            grid.Text("Tunnel effect", dock?.Tunnel, "goto");
            grid.EndSection();
            return;
        }

        if (jump != null)
        {
            grid.Text("Kind", Capitalized(jump.Kind));
        }

        // One line has the three values. The row is locked, and its lock names the tools that change the pair.
        grid.SetNextNote("The destination system, the object where the ship comes out, and the tunnel effect.");
        grid.Reference("Destination", ObjectKinds.StarSystem, dock?.Target, Name(system), system, "goto");
        if (jump == null || section == null)
        {
            grid.EndSection();
            return;
        }

        using (grid.Bind(null))
        {
            if (jump.System is { } nickname)
            {
                var loaded = window.Session?.GameData?.Items.Systems.Get(nickname);
                grid.Reference("Target system", ObjectKinds.StarSystem, nickname,
                    loaded != null ? Name(loaded) : jump.SystemSection is { } entry ? Name(entry) : null,
                    (object?)loaded ?? jump.SystemSection);
            }

            if (jump.Exit is { } exit)
            {
                var kind = jump.PartnerKind == "jump hole" ? ObjectKinds.JumpHole : ObjectKinds.JumpGate;
                grid.ReferenceTo("Exit object", kind, exit, jump.Partner is { } partner ? Name(partner) : null,
                    jump.Partner == null ? null : GoToPartner, missing: jump.Partner == null);
            }

            grid.Help(PartnerTip);
            grid.Row("Partner", () =>
            {
                ImGui.BeginDisabled(jump.Partner == null);
                if (ImGui.SmallButton("Go to partner"))
                {
                    GoToPartner();
                }

                ImGui.EndDisabled();
                if (jump.Partner == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("The goto line names no object that the edit view has.");
                }

                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(jump.Partner == null ? "Missing" : jump.GoesBack ? "Goes back" : "Does not go back");
            });
            foreach (var zone in jump.TrafficZones)
            {
                Link("Traffic zone", zone);
            }

            if (jump.Lock is { } locked)
            {
                LockRow(section, jump, locked);
            }
        }

        grid.EndSection();

        void GoToPartner()
        {
            if (jump.Partner is { } partner)
            {
                GoTo(partner);
            }
        }
    }

    /// <summary>
    /// The Trade lane ring section: the place of the ring in its lane, the locked rows of the links, the lane zone, the
    /// end name on an end ring, and the buttons "Insert ring after", "Repair lane" and "Delete ring". Without a section
    /// of W, it shows the lane of the game data.
    /// </summary>
    /// <param name="show">True when the page shows the section (see <see cref="SystemObjectPage.DockingRows"/>).</param>
    public void Lane(SystemObject obj, SectionRef? section, bool show)
    {
        if (!show || !grid.BeginSection(RingKey, EditorIcons.Stream, ObjectComponents.Name(ObjectComponent.TradeLaneRing),
                true, ConnectionComponents.RingHelp))
        {
            return;
        }

        var lane = obj.Dock?.Kind == DockKinds.Tradelane ? obj.Dock : null;
        var ring = section == null ? null : PageOf(section)?.Ring;
        if (ring != null)
        {
            grid.Help("The place of the ring in its lane, from the start of the lane.");
            grid.Text("Place in lane", ring.Place);
        }

        // The rows of the links are locked. Their lock names the buttons and the tools that change the lane.
        grid.Reference("Previous ring", ObjectKinds.TradeLaneRing, lane?.TargetLeft, RingName(ring?.Previous, lane?.TargetLeft),
            RingTarget(ring?.Previous, lane?.TargetLeft), "prev_ring");
        grid.Reference("Next ring", ObjectKinds.TradeLaneRing, lane?.Target, RingName(ring?.Next, lane?.Target),
            RingTarget(ring?.Next, lane?.Target), "next_ring");
        if (ShowsLaneName(ring))
        {
            grid.Help(LaneNameHelp);
            grid.Ids("Lane name", obj.TradelaneSpaceName, "tradelane_space_name");
        }

        using (grid.Bind(null))
        {
            if (ring != null)
            {
                if (ring.Zone is { } zone)
                {
                    Link("Lane zone", zone);
                }
                else
                {
                    grid.Text("Lane zone", null);
                }
            }

            if (lane != null)
            {
                // The engine gives these names in a walk over the lanes of the system.
                grid.Ids("Left name", obj.IdsLeft, "ids_left");
                grid.Ids("Right name", obj.IdsRight, "ids_right");
            }

            if (ring != null && section != null)
            {
                RingButtons(section, ring);
            }
        }

        grid.EndSection();
    }

    /// <summary>The row of the lock dropdown. A pick runs the lock plan as one entry of the edit history. The dropdown
    /// is disabled when the editor takes no edit or the plan refuses each other lock. A lock that the plan refuses is
    /// disabled. Each disabled part tells why in its tooltip.</summary>
    private void LockRow(SectionRef gate, JumpConnectionInfo jump, JumpLock locked)
    {
        var unavailable = EditorReason ?? jump.LockUnavailable;
        grid.Help(LockHelp);
        grid.Row("Lock at start", () =>
        {
            ImGui.BeginDisabled(unavailable != null);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##lock", LockLabel(locked)))
            {
                foreach (var (choice, label) in LockChoices)
                {
                    var refusal = jump.LockRefusals.GetValueOrDefault(choice);
                    ImGui.BeginDisabled(refusal != null);
                    if (ImGui.Selectable(label, choice == locked) && choice != locked)
                    {
                        window.SceneEdits.SetLock(gate, choice);
                    }

                    ImGui.EndDisabled();
                    if (refusal != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(refusal);
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.EndDisabled();
            if (unavailable != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(unavailable);
            }
        });
    }

    /// <summary>The row of the ring buttons. A disabled button tells why in its tooltip.</summary>
    private void RingButtons(SectionRef section, TradeLaneRingInfo ring)
    {
        var editor = EditorReason;
        grid.FullRow(() =>
        {
            if (Button("Insert ring after", editor ?? ring.InsertUnavailable, InsertTip))
            {
                window.SceneEdits.InsertRingAfter(section);
            }

            ImGui.SameLine();
            if (Button("Repair lane", editor ?? ring.RepairUnavailable, RepairTip))
            {
                window.SceneEdits.RepairLane(section);
            }

            ImGui.SameLine();
            if (Button("Delete ring", editor ?? ring.DeleteUnavailable, DeleteTip))
            {
                window.SceneEdits.Delete(section);
            }
        });
    }

    /// <summary>A small button, disabled with its reason in the tooltip. Returns true on a click.</summary>
    private static bool Button(string label, string? unavailable, string tooltip)
    {
        ImGui.BeginDisabled(unavailable != null);
        var clicked = ImGui.SmallButton(label);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(unavailable ?? tooltip);
        }

        return clicked;
    }

    /// <summary>A link to a zone of the open system, or to its section.</summary>
    private void Link(string label, SectionRef zone)
    {
        var item = window.Scene.ItemOf(zone);
        grid.Reference(label, window.Scene.KindOf(item) ?? ObjectKinds.Of(ZoneRole.Other), zone.Id.Key, Name(zone),
            item ?? zone);
    }

    /// <summary>
    /// Goes to the section of a step or a link: an object or a zone of a system opens its system and selects it, as
    /// "Go to partner" does. Another section, for example a <c>[system]</c> of <c>universe.ini</c>, shows its page.
    /// </summary>
    private void GoTo(object target)
    {
        if (target is not SectionRef section || window.Session is not { } session)
        {
            return;
        }

        if (session.Edits.SystemNicknameOf(section.Path) is { } system)
        {
            window.OpenSystem(system, section);
        }
        else
        {
            window.Selection = section;
        }
    }

    /// <summary>The models of a section, made again after each change of the edit view.</summary>
    private Page? PageOf(SectionRef section)
    {
        if (window.Session is not { } session)
        {
            return null;
        }

        var edits = session.Edits;
        if (page is not { } known || !ReferenceEquals(known.Session, edits) || !known.Section.Equals(section) ||
            known.Revision != edits.Revision)
        {
            page = Page.Of(edits, section);
        }

        return page;
    }

    /// <summary>The item that the link of a neighbor ring selects: the ring of the lane, else the object that the line
    /// names in the open system.</summary>
    private object? RingTarget(SectionRef? ring, string? nickname) =>
        ring != null ? window.Scene.ItemOf(ring) ?? ring
        : nickname == null ? null
        : window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(nickname);

    private string? RingName(SectionRef? ring, string? nickname) =>
        ring != null ? Name(ring) : Name(RingTarget(null, nickname));

    /// <summary>The name of an item from W, or null for no item.</summary>
    private string? Name(object? item) => item == null ? null : window.Session?.ItemName(item);

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
