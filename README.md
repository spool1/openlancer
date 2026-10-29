# OpenLancer

OpenLancer makes Freelancer (2003) openly moddable. It adds a mod overlay with a load order, and an editor for the game content. The editor has data editors, a 3D scene editor for star systems, a mission studio, and a cutscene editor. An MCP server lets AI agents read the game content, see the editor views and edit the systems and the data of a mod. Mission and cutscene edits for agents come next. OpenLancer runs on its own fork of the [Librelancer](https://codeberg.org/Librelancer/Librelancer) engine. The fork is a git submodule at `external/Librelancer` (see docs/UPSTREAM.md).

![The OpenLancer editor: the Scene view of New York (Li01) with the Hierarchy and the Assets panel](docs/images/editor-view.gif)

> **No game files are in this repository.** It holds only OpenLancer's own code and documentation. There is no game data, no game executable and no game DLL. To use OpenLancer, you link your own copy of Freelancer (2003).

## Supported games

| Game | Edit and check mods | Export (drop-in zip and FLMM package) | Play the mod in the game (Ctrl+F5) |
|---|---|---|---|
| Freelancer 1.1 (retail) | Yes | Yes | Yes |
| Freelancer: HD Edition 0.7.1 | Yes | Yes | Yes |
| Freelancer 1.0 (retail) | Yes. It has the same INI files as 1.1. | Yes, for 1.1 | Patch the game to 1.1 first. The 1.0 program does not start on Windows 10 and 11. |
| A source build of the Freelancer-Decomp project that you build yourself | Yes, with its INI files of 1.1 | Yes, for 1.1 | Yes, with **Play > Play in source build** |

F5 plays the mod in the OpenLancer engine with the data of any linked game. The editor also identifies JFLP. Exports always hold only the changes of your mod, and they never write into a game folder. OpenLancer contains no code of the Freelancer-Decomp project.

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first. Fork the repository and open a pull request. The owner reviews each pull request before it goes into `master`. [ROADMAP.md](ROADMAP.md) shows the milestones.

## Legal

OpenLancer is an independent project. It is not affiliated with, endorsed by or sponsored by Microsoft, Digital Anvil, the Librelancer project or any holder of a "Freelancer" trademark. OpenLancer uses the name "Freelancer" only to identify the 2003 game that it works with. All trademarks belong to their owners.

This repository contains no game files and no game data. You need your own copy of Freelancer (2003). OpenLancer is licensed under the MIT License (see LICENSE). The third-party notices are in NOTICE.

## Folder layout

The repo is one folder in a larger workspace. The game data stays outside the repo.

```
<workspace>\
  Freelancer.iso      your retail disc image
  GameData\           private, never in git
    base\             extracted vanilla data (read-only)
    mods\  profiles\  out\  fonts\
  OpenLancer\         this repo
    assets\icon\            the icon of the programs: openlancer.svg and the files of tools/make_icon.py
    external\Librelancer\   submodule (see docs/UPSTREAM.md)
    release\                the texts and the license data of a binary release (tools/release.ps1)
    tests\
    tools\
```

## Set up

### Players

You need your own installed copy of Freelancer (2003): the retail game 1.0 or 1.1, with or without JFLP, or Freelancer: HD Edition.

1. Unzip the OpenLancer release to a folder of your choice.
2. Start `OpenLancer.Editor.exe`.
3. On the page "Link your Freelancer game", click **Select game folder...**. Select the folder that contains the `EXE` and `DATA` folders of your game, for example `C:\Program Files (x86)\Microsoft Games\Freelancer`. You can also drag the folder to the editor window.
4. The editor shows the game and the version that it found. Click **Create profile and open**.

The editor makes a profile for your game and an empty mod, `my-mod`, and opens the profile. Your edits go into `my-mod`. OpenLancer reads the files of the game folder. It never changes them. Your profiles and mods go to `Documents\OpenLancer`. **File > Game link...** shows the linked games and their profiles. When you move the game, **Relink...** in that window gives the profiles the new folder. See [docs/GAME_LINK.md](docs/GAME_LINK.md).

### Developers

1. Clone with submodules: `git clone --recursive <url> OpenLancer`.
2. Extract the game data to `GameData\base` and check it with `python tools\verify_base.py ..\GameData\base`. The English v1.0 retail disc gives `8418 5b4ee64cf64bb9c5525cee786f54380170dbde5617d1aaf5ef3655237c9cfaeb`.
3. Build the engine submodule once. See "Build the submodule" in [docs/UPSTREAM.md](docs/UPSTREAM.md).
4. Run `dotnet test OpenLancer.slnx`.
5. Run `start.bat` to build and start the editor. The editor lists the profiles of `GameData\profiles` and of the workspace folder. To link the game as a player does, click **File > Game link... > Link a game folder...** and select `GameData`. The editor finds `GameData\base`.

The data tests read `..\GameData\base`. Set `OPENLANCER_BASE` to use a different folder. Without game data, the data tests are skipped. `oltool game <folder>` prints the check of a game folder (see [docs/GAME_LINK.md](docs/GAME_LINK.md)).

## Make a mod

![The system editor: the Hierarchy of New Tokyo (Ku01), the Scene with the gizmo on Planet Kitadake, the Inspector with named fields, and the Assets panel with rendered thumbnails](docs/images/system-editor.png)

*Select an object in the Hierarchy or the Scene, change it in the Inspector, or drag an asset from the Assets panel into the Scene.*

See [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md). `samples/mods/ol-sample` is a complete example: it changes a price, and it adds a new ship that Manhattan sells.

1. Make a profile, for example `GameData\profiles\my.json` with `{ "mods": [ "../mods/my-mod" ] }`.
2. Run `oltool validate GameData\profiles\my.json`.
3. Run `oltool deploy GameData\profiles\my.json`.
4. Start the game with `lancer.exe GameData\profiles\my.lancer.ini`.

To make the packages that players install, pick **Mod > Export mod...** in the editor, or run `oltool export`. The export makes a drop-in zip and an FLMM package for HD Edition 0.7.1 and for Freelancer 1.1 from the files of your linked games. It also makes a zip of the mod folder for OpenLancer. See "Export" in [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md).

**Mod > Mods...** shows each mod of the profile and the workspace with its export state. Give a mod the display name that players see, and write the texts of its Nexus page before the export. A publish checklist tells what a release still needs, and the History tab shows each export and test play. See "The Mods window" in [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md).

Some data loads in OpenLancer but crashes the retail game, for example a good with the price 0. For a mod with a retail target, the editor stops a save, a Play in the game and an export with such data. The Console tells why. Run `oltool validate GameData\profiles\my.json --target hde-0.7.1 --game "<HD Edition folder>"` to check a profile for a retail game. See "Content checks of the retail game" in [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md).

## Play from the editor

Run `start.bat` to build and start the editor. Open a profile and a system, then press F5. The game starts at the Scene camera with the edits of the active mod, and a save reloads the running game. See [docs/PLAY.md](docs/PLAY.md).

To play the mods in your own retail game, pick **Play > Play in HD Edition 0.7.1** (or another linked game), or press Ctrl+F5. The editor installs the mods into a play copy next to the game and starts the game of the play copy. Your game folder does not change. **Play > Host test server** starts FLServer of the play copy with the mods. See "Play in the game" in [docs/PLAY.md](docs/PLAY.md) and "Play copies" in [docs/GAME_LINK.md](docs/GAME_LINK.md).

## Build missions

The mission studio shows each mission script of the game as a graph of triggers, beside the 3D Scene. You can read every story mission, edit it in a mod, use behavior prefabs, and test the trigger rules with a dry run. The game reads the result as normal mission INI, so a mission mod works on Freelancer 1.1 and HD Edition.

![The mission studio: a mission is edited in the graph, a prefab is placed from the library, and the Inspector updates](docs/images/OpenLancerMissionEditorViewGif.gif)

![The mission studio with Mission 4: the library of prefabs, events, actions and conditions, the graph of the act "New York" with Spawn wave, Objective and Offer mission nodes, the Hierarchy of the mission, and the Inspector with the story slot, the overview counts and the problems](docs/images/OpenLancerMissionEditorView.png)

*The library at the left of the graph holds the behavior prefabs, the events, the actions and the conditions. The Inspector shows the story slot of the mission, the count of each part, and the problems. "All 7 steps complete" is the completeness panel.*

Open the Missions tab of the editor to see the mission scripts of the game data and the mods. The 14 story missions come first, in the order of the game. Each row gives the start stage, the next mission, the problems and the lines that OpenLancer does not run. Double-click a mission to open it in the Mission tab.

The Mission tab shows the mission as a graph beside the Scene. Each box is one trigger: the event that starts it, and what it does. Frames group the acts and the behavior prefabs, for example a spawn wave. A lens shows one part of the mission, for example the battles or the conversations. Select a trigger, and the Scene shows the ships, points and volumes that it uses. Select an item in the Scene, and the graph shows the triggers that use it.

The Hierarchy lists the items of the open mission, and the Inspector shows each item with named fields and a completeness panel. The Raw INI view shows the whole script with the marks of the checks. The toggle "base-game messages" shows the problems that the base game has too. They are hidden by default.

With an active mod, you can edit the mission. Each gesture is one entry of the edit history, and a save writes a patch of the script:

- In the graph, drag from the "+" pin of a trigger to the Start or the Stop pin of another trigger. Right-click a link to insert a trigger or to remove the link. Delete, Ctrl+D, F2, Ctrl+C and Ctrl+V work on the selected triggers. Space opens quick add.
- Drag an event, an action or a condition from the library strip at the left of the graph onto a trigger or onto an empty place.
- Change the fields of a page in the Inspector. Use "Pick in Scene" to set a position with a click in the Scene.
- Click "Fix" on a problem or on a missing step when the fix needs no choice.
- Use the "+" menu of the Hierarchy to add an item. Drag a ship from the Assets panel onto the group "Cast" to add a mission ship.
- Drag a behavior prefab from the library into the graph, for example "Spawn wave", and set its fields in the Inspector. An override of a field is bold with a violet bar, with "Revert" and "Apply to prefab". "Open prefab" edits the prefab and each of its instances.

To test the logic of a mission without the game, open the **Dry run** panel of the Mission tab. Start the mission, run a trigger, fire the world events that the active triggers wait for, or step the timers. The graph marks each trigger as waiting, ran, stopped or next. **Back** undoes one step. The dry run uses the trigger rules of the retail game.

A graph file of the mod keeps the places of the nodes that you move, the acts that you edit and the prefab instances. The game does not read it. See "Editor files of missions" in [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md). [docs/MISSIONS.md](docs/MISSIONS.md) is the help page of the mission studio.

To measure the frame time of the Mission tab, start the editor with `OpenLancer.Editor <profile.json> --bench-mission <install path>...`. For example, give the scripts `DATA\MISSIONS\M10\m10.ini` and `DATA\MISSIONS\M06\m06.ini`. The bench opens the profile without an active mod and shows each mission with the Scene beside it. For each mission, it draws 300 frames in the Beats lens and 300 frames in the Logic lens with "Show lines". The frames pan and zoom the graph.

Then the bench writes the median and the 95th percentile of the frame times to the engine log of the editor, and closes. The gates are a median under 16 ms in the Beats lens and under 33 ms in the Logic lens. A case above its gate, or a script that the mission checks do not have, gives the exit code 1. The bench does not change the editor settings. The log is the file of the cutscene bench `--bench-cutscene` (see "The Cutscene tab" in [docs/CUTSCENES.md](docs/CUTSCENES.md)).

## Study and edit cutscenes

Open the Cutscenes tab of the editor to browse the retail cutscenes and the cutscenes of the mods. Open one to preview it on a timeline and to see each value. With Mod > None (read-only), the editor takes no edits. With an active mod, a save writes the changes of a cutscene as a small THN patch. See [docs/CUTSCENES.md](docs/CUTSCENES.md).


![The cutscene editor: a free camera flies through the retail cutscene s002xe on Manhattan, with its cameras, markers, paths and timeline](docs/images/cutscene-editor.gif)

![The cutscene editor: the entities of the cutscene, the preview with cameras and markers, the timeline of events and the Inspector of a character](docs/images/cutscene-editor.png)

## Agents (MCP)

OpenLancer has an agent server: an MCP (Model Context Protocol) server. An AI agent, for example Claude Code, uses it to read the game content of a profile, to see the views of the editor and to edit the active mod. The goal is that agents make content with the same tools as a modder: systems and areas, NPC ships, loot wrecks, missions and cutscenes. The agent edits the items of the systems and the data of the active mod now. It also saves, exports and starts Play. The mission and cutscene edit tools come next.

**Connect an agent**

1. In the editor, pick **Agent > Start agent server**.
2. Pick **Agent > Connect an agent...**. It shows the `.mcp.json` entry for Claude Code, with the port and the folder of `oltool`, and a Copy button.
3. Put the entry in the `.mcp.json` file of your agent, and start the agent.

Without the editor, `oltool mcp serve <profile.json> --mod <id>` starts the same server over stdio, without the view tools. With `--read-only`, it only reads. See [docs/MCP.md](docs/MCP.md) for the details.

**Safety**

- The server listens only on 127.0.0.1, on one fixed port. Each request needs the token of the session. No other computer can connect.
- A tool reads the profile through the edit session of the editor. The path rules refuse each path outside the profile.
- The edit tools use the same commands, checks, save gate and undo as the editor. They change only the active mod, never a game folder or another mod. Each agent edit is one Undo step.
- The editor marks each agent edit in the Console, the Scene and the Hierarchy, and follows it with the camera. **Agent > Pause agent edits** stops all agent edits until you resume them.
- When an agent starts Play and the mod has unsaved edits, the editor asks you to save first. A setting turns on an automatic save for agents.

**Tools of the server now (read, view and edit)**

| Group | Tools | What they do |
|---|---|---|
| Status | `studio_status` | The profile, the active mod, the revision, the undo steps and the limits |
| Systems and items | `list_systems`, `get_system`, `list_items`, `get_item`, `find_references` | The star systems, their objects, zones, lights, nebulae and fields, and each line that names an item |
| Data | `list_values`, `search_assets`, `list_prop_groups`, `get_raw_ini` | The valid values of a field, the assets of the Assets panel, the prop groups, and the raw INI with its problems |
| Missions | `list_missions`, `get_mission`, `get_trigger`, `describe_mission_name`, `mission_dry_run` | The mission scripts, their triggers in plain words, the meaning of each condition and action, and a dry run of the trigger rules |
| Cutscenes | `list_cutscenes`, `get_cutscene` | The cutscene scripts with their entities and events |
| Checks | `check`, `get_console` | The problems of the mod, and the Console of the editor |
| Views (editor only) | `open_view`, `select_items`, `render_scene`, `render_cutscene`, `capture_panel` | Open a system, a mission or a cutscene, select an item, and get images of the Scene, a cutscene frame or a panel. The views of the user do not move. |
| Edits | `set_fields`, `set_text`, `rename_item`, `delete_items`, `duplicate_items`, `undo`, `redo`, `edit_batch` | Change the fields, the texts and the nicknames of items, delete and copy items of a system, and undo the edits of the agent |
| Scene and data | `scene_create_object`, `scene_create_zone`, `scene_create_light`, `scene_spawn_asset`, `scene_transform`, `scene_set_component`, `scene_place_wreck`, `scene_place_patrol_route`, `scene_set_encounters`, `prop_group_place`, `prop_group_save`, `prop_group_rename`, `prop_group_delete`, `create_loadout`, `create_from_template` | Create and move the items of a system, place wrecks, patrol routes and prop groups, and make loadouts and items from templates |
| Save, export and Play | `save`, `export_mod`, `publish_checklist`, `play`, `play_stop` | Save the active mod, export it, give the publish checklist, and start and stop Play (editor only) |

**Tools that come next (edits)**

| Part | Tools |
|---|---|
| Mission edits (MCP1-3) | `mission_edit`, `mission_add_trigger`, `mission_set_condition`, `mission_add_action`, `mission_set_action`, `mission_move_action`, `mission_link_triggers`, `mission_insert_trigger`, `mission_add_item`, `mission_place_prefab`, `mission_start_plan`, `mission_play`, `mission_play_trace` |
| Cutscene edits (MCP1-4) | `cutscene_create`, `cutscene_add_entity`, `cutscene_add_event`, `cutscene_move_events`, `cutscene_set_duration`, `cutscene_set_flags` |

Examples of requests to an agent now: "Show me an image of New York from above." "Which triggers of Mission 4 spawn ships, and when?" "Run Mission 1 in a dry run up to the first checkpoint, and tell me which triggers wait." "Make a patrol route with three legs in New York, put a wreck with cargo at its end, and save the mod."
