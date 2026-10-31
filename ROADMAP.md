# OpenLancer roadmap

OpenLancer is a mod editor for Freelancer (2003). The goal: people can easily make mods for Freelancer HD Edition 0.7.1 and vanilla 1.1, and publish them, for example on Nexus.

Each milestone goes through four steps: design, adversarial review, build and verify. Each pull request names its milestone (see [CONTRIBUTING.md](CONTRIBUTING.md)). The IDs are names, not an order. The table shows the order.

## Mod tiers

- **Tier 1:** data mods (INI files, strings, cutscenes, models). They run in the retail game and in OpenLancer.
- **Tier 2:** runtime mods (scripts, new mechanics, modern rendering). They run only in OpenLancer.
- **Editor** and **engine** milestones change the editor or the engine fork, not the mods.

## Milestones

| ID | Milestone | Tier | Status | What it gives |
|---|---|---|---|---|
| Base | Mod overlay, validator and deploy | 1 | done | A mod is a folder of patches over the base data, with a load order. `oltool validate` checks a profile, and `oltool deploy` prepares it for the game. |
| M1 | Edits save as mod patches | 1 | done | Each edit in the editor saves as a small patch in the active mod. |
| M2 | Scene editing | editor | done | Move, rotate and scale objects and zones in the 3D Scene. The Inspector shows and edits each value. |
| M4 | Play from the editor | editor | done | F5 starts the game at the Scene camera, with the edits of the active mod. |
| M5 | Hot reload | editor | done | A save reloads the running game. |
| M8a | Cutscene editor, part 1 | 1 | done | Browse the cutscenes and preview them on a timeline. A save writes the changes as a small THN patch. |
| H1 | Housekeeping | none | done | The repo docs and comments match the engine fork. |
| GA1 | Gap audit, static part | engine | done | A list of what the engine does, does differently or does not do, compared with the retail game. Each row tells how to repeat it. |
| M20 | Inspector field helpers | editor | done | Drag the X, Y or Z label to change a value. Color pickers keep the value form of each key. |
| M21 | Data lists and pickers | 1 | done | A field that names other data gets a list from the data, so mods can extend it. Each Inspector page has a raw INI block. The save gate stops a save that makes the game crash. |
| M8b | Cutscene editor, part 2 | 1 | done | Sound in the preview, new cutscenes, and room lines that start a new cutscene. A cutscene error blocks Save and Play. |
| M25 | Mod format version 2 | 1, 2 | done | `mod.json` gives the tier and the target games. Key rules for the nebula and asteroid field files. |
| M11a | Game link and first-run setup | editor | done | At the first start, the editor asks for your game folder. It finds the game and its version, makes the profile and shows the link status. |
| M22 | Nebula and asteroid field picking | editor | done | Select nebulae and asteroid fields in the Scene, also from inside them. |
| M3 | Create, delete and rename | 1 | done | Create, duplicate, rename and delete objects, zones and lights in the Scene. A delete fixes or marks the references, as in Unity. A rename changes each reference. Objects show their components. |
| M23 | Asset browser and prop groups | 1 | done | Browse the assets of the game data and the mods with thumbnails, and drag them into the Scene. Ships, asteroids and models become static props. Save a group of props and place it again in any system. |
| M11 | Export and play in the game | 1 | done | Export a mod for HD Edition 0.7.1 and vanilla 1.1, as a Nexus drop-in zip and an FLMM package. Play the mods in a play copy of the linked game, or host a test server. Checks stop data that crashes the retail game. |
| M11c | Mods window and publish flow | editor | done | A list of all mods with their export state. Give a mod a display name and the texts of its Nexus page before the export. A publish checklist, a history of the exports and test plays, and a guard that keeps one version for one set of game files. |
| M6a | Mission studio, read-only | 1 | done | Browse the mission scripts with their checks. See each mission as a graph of triggers beside the Scene, with acts, behavior prefabs and links to the world. The Hierarchy and the Inspector show each mission item. |
| M6b | Mission editing | 1 | done | Edit missions as a graph of triggers, conditions and actions, with behavior prefabs, overrides and prefab mode. See docs/MISSIONS.md. |
| MCP1-1 | Agent server, read-only | editor | done | An MCP server in the editor and in `oltool mcp serve`. An agent reads the systems, data lists, assets, missions, cutscenes, raw INI and problems. In the editor, it also opens views, selects items and gets images. See docs/MCP.md. |
| R1 | First release, v0.1.0 (preview) | none | done | One zip with the editor, Play and oltool. It holds no game files. Published on 2026-10-22. |
| MCP1-2 | Agent scene and data edits | editor | done | Agents edit the Scene and the data through the edit session, checks, save gate and undo of the editor. They also save, export and start Play. The editor shows each agent edit. See docs/MCP.md. |
| M8c | Cutscene editor, part 3 | 1 | in progress | Pick the animation of a character event from a list of the game clips, with a preview of each clip on the character. Warnings for cutscene problems, and Convert to patch. |
| M6c | Play from a trigger | editor | in progress | A dry run runs the triggers of a mission in the editor without the game (M6c-1, done). "Play from here" starts the game at a trigger, and a live view in the graph shows which triggers ran and which wait (M6c-2, done). Next: a start save that HD Edition and vanilla 1.1 load (M6c-3). |
| MCP1-3 | Agent mission edits | editor | next | Agents edit missions with the commands of the mission studio. They also run the dry run and the mission play. |
| MCP1-4 | Agent cutscene edits | editor | next | Agents edit the entities, events and timing of cutscenes, with guards. |
| R2 | Second release, v0.2.0 (preview) | none | next | A new zip with Play from a trigger and the newer agent tools. |
| M3b | Scene editing, part 2 | 1 | next | Multi-selection, move with children, the encounter group editor, new factions and a Problems panel. |
| M34 | Prop scale | 2 | later | A scale key for objects. The model, the hit test, the collision and the dock paths scale with it. |
| M24 | Game tab | editor | later | The game window docks in a tab of the editor. The game stays a separate process. |
| M28 | Prefab viewer | 1 | later | One page for each asset: archetype, model parts, hardpoints and loadout. |
| M29 | Space cutscene clips | 1 | later | The in-space cutscenes show as clips in the system view, with their markers and nav points. |
| M26 | Nebula and asteroid field areas | 1 | later | Change the shape of a nebula or a field, cut holes, edit mining zones, add new nebulae and fields, and duplicate them. |
| M27 | Connections and network map | 1 | later | A universe map. Create jump gates, jump holes, trade lanes and new systems. |
| GA2 | Gap audit, retail comparisons | engine | later | Compare each GA1 row with the retail game. |
| M7 | Dialogue | 1 | later | Edit mission dialogue and add voice lines. |
| M30 | Space cutscene authoring | 1 | later | Make new in-space cutscenes. |
| M31 | Model writing | 1 or 2 | later | Merge and split meshes, edit hardpoints, and publish a built model as a new prop. |
| M32 | Vertex editing | 1 or 2 | later | Edit a model in Blender and import it again. An editor mode can follow. |
| M33 | First original campaign | 1 | later | One system, two ships, three missions and one cutscene, all new, made with the editor. |
| T1 | Tier-2 foundation and scripting | 2 | later | The runtime features that a tier-2 mod needs, and a scripting host. |
| T2 | Subsystem replacement | 2 | later | Replace the engine subsystems one at a time. Render work and engine options. |
| T3 | Own runtime | 2 | later | The runtime renders inside the editor process. |
| T4 | Tier-2 wishlist | 2 | later | Walk inside stations, build stations, mining loops, new ships and modern graphics. |

## Long-term goal

The editor engine becomes OpenLancer's own code. The work replaces the engine fork one subsystem at a time and keeps the data formats. At the end, OpenLancer does not need Librelancer. Players then play the mods in the game that they own, for example HD Edition or vanilla 1.1.
