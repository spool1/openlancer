# The agent server (MCP)

OpenLancer has an agent server. An agent program, for example Claude Code, connects to it and reads the data of a profile. The server uses the Model Context Protocol (MCP), so each agent program with MCP support can use it.

The agent reads the data as the editor shows it. It reads the systems and their items, the data lists, the assets, the missions and the cutscenes. It also reads the raw INI text and the problems of the checks. In the editor, the agent can also open views, select items, and get images of a system, of a cutscene and of a panel.

The agent can also edit the items of the active mod. It sets fields and texts, renames, deletes and copies items, and undoes its own edits. It creates objects, zones and lights, places assets and prop groups, moves items and makes items from templates. It also changes the mission scripts with the commands of the mission studio. Each agent edit goes through the same commands, checks and Undo history as an edit of the user. An edit changes the edit view of the editor. It is not saved until you or the agent save the mod. See "The edit tools", "The Scene and data tools", "The mission tools" and "Save". In the editor, the agent can also start and stop Play, also a play of a mission from a trigger. See "Play". The editor marks each agent edit, and you can pause the agent edits. See "What you see in the editor".

## The two hosts

Two programs can host the server. They give the same tools, with the same names and results.

| Host | How it starts | Transport | Tools |
|---|---|---|---|
| The editor | **Agent > Start agent server** in the editor | HTTP on `http://127.0.0.1:<port>/mcp` | All tools |
| oltool | The agent program starts `oltool mcp serve` | Standard input and output (stdio). It opens no port. | All tools. The tools of the editor give `not_available`. `--read-only` and `--no-save` leave out tools. See "The oltool host". |

Use the editor host when you work in the editor. The agent then sees your unsaved edits, and the view tools show its work to you. Use the oltool host for a script or for an agent that works without the editor. An editor-only tool gives the error `not_available` in the oltool host.

## Connect an agent to the editor

1. Open a profile in the editor.
2. Pick **Agent > Start agent server**.
3. At the first start, read the notice. It tells what the agent reads. Click **Start server**.
4. Pick **Agent > Connect an agent...**.
5. Click **Copy**.
6. Paste the text into the file `.mcp.json` in the folder of your agent project.
7. Start the agent program in that folder.

The status bar shows the state of the server, for example "Agent server: on, port 47815". While an agent calls, it shows the name of the agent program and the number of its edits since the last save. An example is "Agent: claude-code, 3 edits". While you pause the agent edits, it shows "Agent paused". Click the status bar segment to open the Agent menu.

### The .mcp.json entry

**Connect an agent...** gives this text with the real port and the real folder of OpenLancer:

```json
{
  "mcpServers": {
    "openlancer": {
      "type": "http",
      "url": "http://127.0.0.1:47815/mcp",
      "headersHelper": "\"<OpenLancer folder>\\oltool.exe\" mcp token"
    }
  }
}
```

The text does not hold the token. When the agent program connects, it runs the helper `oltool mcp token`. The helper gives the token of the running server.

### The token

Each start of the server makes a new random token of 256 bits. Each request needs the header `Authorization: Bearer <token>`. A request without the right token gets the status 401.

The editor writes the port, the token and its process into a file in `%LOCALAPPDATA%\OpenLancer\agent`. That folder is in your user profile, so other users of the computer cannot read it. The editor deletes the file when the server stops.

`oltool mcp token` prints the header as JSON, for the `headersHelper` of Claude Code. Claude Code gives the address of the server in the variable `CLAUDE_CODE_MCP_SERVER_URL`. The helper gives the token only when all of these conditions are true:

- An editor with a running server has written the file, and that editor still runs.
- With an address, the address uses `127.0.0.1` and the port of that server.
- Without an address, only one editor runs a server.

Else the helper prints no token and exits with the code 2. It writes the reason to the standard error, for example "2 editors run an agent server". Read this reason when the agent program does not connect. Use the address with `127.0.0.1`, not `localhost`. The helper gives no token for `localhost`, because another program can listen on the IPv6 address of `localhost`.

### The Agent menu

| Item | What it does |
|---|---|
| Start agent server | Starts the server on the port of the settings. At the first start, a notice tells what the agent reads. |
| Stop agent server | Stops the server at once. |
| Pause agent edits | Stops all agent edits until you pick **Resume agent edits**. The agent can still read the data and stop each game of Play. |
| Follow agent | Opens and frames the item of each agent edit. It is on by default. It stops when you move the camera of the Scene or select an item. |
| Connect an agent... | Shows the text for `.mcp.json` and a Copy button. |
| Show agent messages | Shows the messages of the server in the Console. |
| Start the agent server with the editor | Starts the server when the editor starts. The server is off by default. |
| Agent settings | The port, the tool groups and the limits. |

When another program uses the port, the server does not start. The status bar then shows "Agent server: port 47815 is in use". Close the other program, or pick another port in **Agent > Agent settings**. Then change the port in `.mcp.json` too. The server never moves to another port by itself.

### The agent settings

**Agent > Agent settings** has the port and the tool groups. Its section **Advanced** has the limits of one agent call.

| Setting | Default | What it does |
|---|---|---|
| Agent server port | 47815 | The port of the server on this computer. Stop the server to change it. |
| Auto-save and continue when an MCP agent starts Play | Off | When an agent starts Play and the active mod has unsaved edits, the editor saves them and plays. Off: the editor asks you first. See "Agent Play" in [PLAY.md](PLAY.md). The setting applies to the tools `play` and `mission_play`. |
| Agent tool groups | All groups | The agent can use the tools of the selected groups: Scene, Missions, Cutscenes and Play. The `scene_` tools, the `prop_group_` tools and `render_scene` are in the group Scene. `render_cutscene` is in the group Cutscenes. `play`, `play_stop` and `mission_play` are in the group Play. The other `mission_` tools and the read tools of the missions are in the group Missions. `mission_play` plays a mission script, so it also refuses when you clear the group Missions. These read tools are always available: `studio_status`, `get_item`, `find_references`, `list_values`, `get_raw_ini`, `check` and `get_console`. The edit tools of "The edit tools" are always available too, and so are `save`, `export_mod`, `publish_checklist`, `create_loadout`, `create_from_template`, `open_view`, `select_items` and `capture_panel`. A tool of a cleared group tells the agent that you cleared the group. An edit tool also refuses an item of a cleared group, for example an object of a system when you clear the group Scene. |
| Rows of a page without a limit | 50 | The rows of a list result when the agent gives no limit. |
| Largest page limit | 200 | The largest limit that the agent can ask for in one list result. |
| Size of a result (KB) | 256 | The most text of one result. A larger result asks the agent for pages. An edit with a larger result changes nothing. |
| Calls each second | 10 | The most calls that the server takes in one second. A read of the catalog of the prefabs counts as a call. |
| Wait for the editor (s) | 30 | How long a call waits while you edit a field, rename an item, drag an item, or a dialog is open. A longer wait gives the error `busy`. A read of the catalog of the prefabs has the same wait. |
| Time of a check or an image (s) | 60 | How long a check of a retail target or an image can take. |
| Size of a request (MB) | 4 | The largest request. The change applies at the next start of the server. |
| Largest side of an image (pixels) | 2048 | The most pixels on each side of an image. A larger panel capture becomes smaller. A `render_scene` or `render_cutscene` call with a larger `width` or `height` gives the error `limit`. |
| Items of one edit | 200 | The most items that one call of `delete_items`, `duplicate_items` or `prop_group_save` names, and the most moves of one `scene_transform` call. |
| Sections of one edit | 2000 | The most sections that one edit call changes, adds or removes. A larger edit changes nothing and gives the error `limit`. |
| Steps of one batch | 100 | The most steps of one `edit_batch` call, and the most commands of one `mission_edit` call. |
| Legs of one route | 50 | The most legs of one `scene_place_patrol_route` call. Each leg is one zone. A retail route has at most 12 legs. |
| Time of an export (min) | 10 | How long one `export_mod` call can take. A longer export gives the error `limit`. A write of the packages that started goes on to its end. |

## The tools

`oltool mcp tools` prints the name, the title and the description of each tool. `oltool mcp tools --json` prints the tool list of the protocol with the schemas.

### The read tools

These tools work in both hosts. They change nothing.

| Tool | What it gives |
|---|---|
| `studio_status` | The profile, the active mod, the revision, the tool groups and the limits. The agent calls it first. |
| `list_systems`, `get_system` | The star systems, and one system with its counts, bases, connections and problems. |
| `list_items`, `get_item` | The items of a system, and one item with its fields, their origins and their problems. |
| `find_references` | Each line and each file that names an item, also in the mission scripts. |
| `list_values` | The values that a field can take, from one data list. |
| `search_assets`, `list_prop_groups` | The assets of the Assets panel, and the prop groups of the mods. |
| `list_missions`, `get_mission`, `get_trigger`, `describe_mission_name` | The mission scripts, one script, one trigger, and the plain name of a condition or an action. |
| `mission_dry_run` | A dry run of one mission script, without a game world. The trigger rules of the game run step by step on the inputs that the agent gives. The result gives each step and the state after the last step. The section "The dry run" of `docs/MISSIONS.md` gives the rules. |
| `mission_start_plan` | The plan of a play of one mission script from a start, as the Play start page shows it. A plan that cannot start gives the status `refused` and the reason. See "The start plan". |
| `list_cutscenes`, `get_cutscene` | The cutscene scripts, and one script with its entities and events. |
| `get_raw_ini` | The INI text of a section or a file, with the problems of each line. |
| `check` | The problems of the checks of the editor, in pages. |

### The tools of the editor

These tools work only in the editor host.

- `get_console` gives the entries of the Console.
- `open_view` opens a system in the Scene, a mission in the Mission tab or a cutscene in the Cutscene tab.
- `select_items` selects an item, as a click does. By default, it also opens the view of the item and moves the view to it. With `frame: false`, it opens no view and does not move the camera.
- `render_scene` gives an image of a system from a camera that the agent chooses. The camera of the Scene does not move, and the Scene keeps its system. The result also gives the place of each item in the image.
- `render_cutscene` gives an image of a cutscene at a time. The preview in the Cutscene tab does not change. A space script shows without its system.
- `capture_panel` gives an image of a panel as you see it, with its marks and labels.
- `play` and `play_stop` start and stop Play in OpenLancer. See "Play".
- `mission_play` plays a mission script from a start in OpenLancer, and `mission_play_trace` gives the trigger changes of that play. See "Mission play".

`open_view` and `select_items` change what the editor shows. They do not change the data. The image tools change nothing.

### Images

An image has at most 2048 pixels on each side, and a larger panel gives a smaller image. A `render_scene` or `render_cutscene` call with a larger `width` or `height` gives the error `limit`. **Agent > Agent settings > Advanced** can set a smaller limit. The editor does not draw a frame while it renders an image. An image of a system that the Scene does not show also loads that system first.

The agent program can send each image to its model service. A capture of a panel can show the names of your folders.

To measure the images, start the editor with `OpenLancer.Editor <profile.json> --bench-agent-render <system>`. Give the nickname of a system of the profile. The bench opens the profile without an active mod and opens the system. It renders one image of 1024 by 640 to warm up. Then it renders one image from each of 10 directions, as `render_scene` does.

For each image, the bench writes two times to the engine log of the editor. The first is the time on the UI thread. The second is the time until the PNG is ready. The time on the UI thread includes the work of the GPU. Then the bench closes. The gate is a median under 100 ms. A median above the gate, or an image that fails, gives the exit code 1. The bench needs no agent server and does not change the editor settings.

### The edit tools

These tools change the data of the active mod. They work in both hosts. The oltool host with `--read-only` does not list them.

| Tool | What it does |
|---|---|
| `set_fields` | Sets fields of one item by their INI keys. |
| `set_text` | Sets the name, the infocard or another text of one item, or a text id of a mission line. The text goes into the `strings.json` of the active mod. |
| `rename_item` | Gives an item a new nickname, and changes each line that names it. A line that the active mod cannot change keeps the old nickname and shows "Missing". A data item must be an item that the active mod adds. |
| `delete_items` | Deletes items of a system with their children, and mission items. Other lines that name them show "Missing". |
| `duplicate_items` | Copies items of a system with their children, and mission items. With `offset`, the copies of a system move by the offset. A copy without a position does not move, and the result warns about it. |
| `undo` | Undoes the last edit of the agent. |
| `redo` | Redoes the last edit that the agent undid. |
| `edit_batch` | Runs edit steps as one Undo step, all or nothing. |

Each edit is one Undo step. The Edit menu shows its label with "Agent: " before it, for example "Undo Agent: Delete my_station_1". You can undo and redo each agent edit, as each of your own edits. `undo` undoes only an agent edit at the top of the history. `redo` redoes only an agent edit that the agent undid. So the agent never undoes your edit, and never redoes an edit that you undid with Ctrl+Z. All agent programs of one editor have the same edit origin. So `undo` of one agent program can undo the last edit of another agent program.

The five tools `set_fields`, `set_text`, `rename_item`, `delete_items` and `duplicate_items` also change mission items. "The generic tools on mission items" tells which references each tool takes. The other edit tools change only items of INI files that are not mission files. A reference of a mission item gives them `invalid_argument` with the detail `MissionItem`. A reference of a cutscene gives the error `not_available`, because the cutscene tools come with a later version.

A rename of an object of a system also changes the mission lines that name it, as in the editor. The question of the rename names these mission scripts. Of the items of a system, `delete_items` takes objects, zones, lights, nebulae and asteroid fields. `duplicate_items` takes objects, zones and lights of a system. `rename_item` takes an item of a system, and a ship, equipment or a good that the active mod adds. Another section, for example a loadout, gives `refused`.

#### Values of set_fields

`set_fields` takes the INI key of each field, as `get_item` gives it:

- A text, a number or `true` or `false` is one line with one value, for example `"loadout": "my_loadout"`.
- An array of values is one line, for example `"pos": [100, 0, -500]`.
- An array of arrays gives one line for each array, for example `"cargo": [["my_good", 5], ["my_other_good", 1]]`.
- `null` or `[]` removes the key. The engine then uses its default.

A value never holds a comma. The key line, a key of linked lines and the `file` of a system are locked, and `get_item` marks them. Use `rename_item` for the nickname. A value that a list check refuses still applies, as in the Inspector. The result gives the problem in `new_problems`.

A mission item takes the field forms of `mission_add_item` in place of these forms, and it runs in strict mode. See "The generic tools on mission items".

#### Preview, confirmation and revision

Each edit tool takes these arguments:

| Argument | What it does |
|---|---|
| `preview` | Runs the edit and gives its result, then changes nothing. The revision, the history and the unsaved state stay the same. The views do not change. |
| `with_patch` | With `preview`: also gives the patch text of each INI file that the edit changes. |
| `if_revision` | Refuses the edit with `stale_revision` when the edit view changed after this revision. Each result gives the revision as `rev`. A save does not change it. |
| `confirm` | `rename_item`, `delete_items` and `mission_edit`: applies an edit that changes more than its targets. The prop group writes: writes the files. |

When you would see a dialog for a delete or a rename, the tool gives `needs_confirmation`, and nothing changes. A delete or a rename that leaves "Missing" lines also needs it. The server tells the agent to show the changes to you and to ask you first. When you agree, the agent calls the tool again with `confirm: true`. A preview tells it in `needs_confirmation`, and the warnings of the preview tell why.

The result of an edit gives the new items (`adds`), the changed items (`changes`), the removed items (`deletes`) and the renamed items (`renames`). It also gives the "Missing" lines, the warnings, the text mentions, and the problems that the edit adds and removes. Each reference names the item as it is after the call. So an item that a call adds and then deletes is not in the result, and two renames of one item are one rename. A call that changes nothing gives the status `no_change`, an empty `changes` list and no Undo step.

#### Batches

`edit_batch` takes a list of steps. Each step names an edit tool and its arguments. All steps are one Undo step with the label of the batch. Each step sees the result of the steps before it, so a later step can name an item that an earlier step made or renamed.

The result also gives the result of each step, with the revision and the files of the batch. A step can delete items, so `edit_batch` has the destructive hint of the protocol, as `delete_items` has.

When one step fails, the error `step_failed` gives its index and its error, and the batch changes nothing. A step that needs a confirmation fails unless its arguments have `confirm: true`. `undo`, `redo`, `edit_batch`, the read tools and the tools of the editor cannot be steps. `prop_group_save`, `prop_group_rename` and `prop_group_delete` cannot be steps either, because they write files at once.

#### Limits and pauses

One call names at most 200 items and changes at most 2000 sections. One batch has at most 100 steps, one `mission_edit` call has at most 100 commands, and one route has at most 50 legs. **Agent > Agent settings > Advanced** changes these limits. A call above a limit gives the error `limit` and changes nothing. The size limit of a result applies to an edit too. The edit checks the size of its result before it applies, so a larger edit also changes nothing.

While you pause the agent edits, each tool that changes data, writes files or starts a play gives the error `paused`. Examples are the edit tools, `undo`, `redo`, `save`, `export_mod`, `play` and `mission_play`. The read tools still work, also `publish_checklist` and `mission_play_trace`. `play_stop` also works. It stops each game of Play, also a game that you started. Pick **Agent > Pause agent edits** to pause the agent edits, and **Resume agent edits** to let the agent edit again.

The edit tools refuse with `read_only` when the editor takes no edits, and the message tells the reason. For example, another editor has the mod open, or the profile or the active mod is in the game folder. A mod in the OpenLancer repo also refuses until you allow its edits.

### The Scene and data tools

These tools call the commands of the Scene, the Assets panel and the Database of the editor. Each tool is one Undo step, except the three prop group writes. They work in both hosts.

| Tool | What it does |
|---|---|
| `scene_create_object` | Creates a station, a planet, a sun, a satellite or another solar object, as the Create menu of the Scene does. |
| `scene_create_zone` | Creates a zone with a shape, a size and a rotation. |
| `scene_create_light` | Creates a point light. |
| `scene_spawn_asset` | Places an asset of `search_assets` at a point, as a drop on the Scene does. |
| `scene_transform` | Moves, rotates and sizes objects, zones and lights, as the gizmo does. |
| `scene_set_component` | Adds a component with its value to an object, or removes a component. |
| `prop_group_place` | Places the members of a prop group as new items of a system. |
| `prop_group_save` | Saves items with their children as a new prop group of the active mod. |
| `prop_group_rename` | Gives a prop group of the active mod a new nickname. |
| `prop_group_delete` | Deletes a prop group of the active mod. The copies in the systems stay. |
| `scene_place_wreck` | Places a wreck with cargo: a new object of a wreck archetype and a new loadout of a ship. |
| `scene_place_patrol_route` | Places a patrol or trade route: one cylinder zone for each leg. |
| `scene_set_encounters` | Sets the encounter groups of a zone, with its faction weights and density restrictions. |
| `create_loadout` | Makes a new loadout with an archetype, equipment and cargo. |
| `create_from_template` | Makes a new ship or new equipment from a template, as "New from template" of the Database does. |

A create can also give the new item a nickname, a rotation, a size, a name and more fields. These parts go into the same Undo step, and the label is "Create" and the nickname. The result gives the new item with its last reference and selects it. Without a nickname, the item gets the next free nickname, as in the Create menu. The editor rounds a position to whole numbers.

A station archetype with a docking sphere needs a base, because the Create menu asks for one. A station archetype without a docking sphere takes no base. A `parent` must be an object of the same system. An archetype of another template gives `invalid_value` with the reason.

`scene_spawn_asset` takes the template of the type of a solar archetype. A ship, an asteroid or a model makes a new static prop archetype. The Scene shows its object after a save, when the editor loads the new archetype, and the result tells it.

`scene_transform` takes rows with a reference and a position, a rotation or a size. The mode `absolute` sets the values, and `relative` adds each value to the value now. A relative rotation adds the degrees to the values of `rotate`. Each item moves alone, as with the gizmo, so the children do not move. An item without a position refuses. An object and a light have no size.

`scene_set_component` adds only a component that "Add component" of the Inspector offers. For example, Dockable station needs an archetype with a docking sphere. The value of an added component must be an entry of its list, as in the picker of the Inspector. Another value gives `invalid_value` with the closest names. The component Solar cannot go, as Unity keeps a component that another component needs.

`prop_group_place` refuses a group with lines that name entries that this profile does not have. The error lists each such line. With `leave_out_error_lines: true`, the tool places the group without these lines, as "Place without these lines" does.

`prop_group_save`, `prop_group_rename` and `prop_group_delete` write the files of the group in the `props` folder of the active mod at once. Undo does not change these writes, as in the Unity Project window. So each one needs `confirm: true`, and its result tells that the change is not on the undo stack. A preview writes nothing. A group of another mod refuses a rename and a delete. A rename to the nickname that the group has gives `no_change`. Before each prop group tool reads the groups, it reads the `props` folders again. So a group file that another program wrote is known, and a save never replaces it.

#### The builders

`scene_place_wreck`, `scene_place_patrol_route`, `scene_set_encounters` and `create_loadout` build items that no single menu item of the editor makes. Each call is one Undo step. The rules of these builders are in the rule data file `list-rules.json` of the editor, in `sceneBuilders`.

A value of a row that names other data must be an entry of its list, as a picker of the Inspector gives it. Examples are an equipment, a cargo good, a faction and an encounter. The tool writes each value as the list spells it. A value that its list does not have gives `invalid_value`. The error names each such row, for example `encounters[1].factions[0].faction`, and gives the closest names.

- `create_loadout` adds the loadout to the first loadout file of `freelancer.ini`, the ship loadout file in retail. Its archetype is a ship or a solar archetype. A hardpoint has no list in the rule data. So a hardpoint that the `hp_type` lines of the ship do not name gives a warning, not an error. The editor does not edit the loadout file, so only undo can change or remove the new loadout. To correct a warning, undo the call and call the tool again.
- `scene_place_wreck` takes a solar archetype of the type `MISSION_SATELLITE` and the ship that the wreck was. It adds a loadout with that ship, the `equip` rows and the `cargo` rows. It also adds the object with the `loadout` line and `visit = 16`, as most retail wrecks have. The loadout gets the nickname of the wreck with `_loadout`. Only undo can change the new loadout. Without `name`, the wreck gets the name "Wreck". It always gets a short infocard.
- `scene_place_patrol_route` adds one cylinder zone for each leg between two next points. The zone has its center at the middle of the leg, and its local Y axis goes along the leg. Its `size` is the radius and the length of the leg. Its `path_label` is the name of the route and the number of the leg, from 1. The other keys come from the route template of the usage, `patrol` or `trade`. `fields` changes these keys on each zone. It cannot give a key that the tool writes itself, for example `pos`, `size`, `usage` or `encounter`. Such a key gives `invalid_argument`, and the error names the argument to use. Each coordinate of a point is from -10000000 to 10000000 meters, as the position of a wreck. A path label that the system has gives `name_taken`.
- `scene_set_encounters` writes the `faction_weight` and the `density_restriction` lines before the first `encounter` line. Then each `encounter` line comes with its `faction` lines, because the game gives a faction line to the encounter line before it. Without `faction_weights` or `density_restrictions`, the zone keeps these lines. A kept `density_restriction` line after an `encounter` line moves before the first `encounter` line, because the game reads it at each place in the zone. A `faction_weight` line after an `encounter` line belongs to that encounter, so the new groups replace it. An empty `encounters` list removes the encounter groups.

An encounter must have an `[EncounterParameters]` section in the system file. When the system has none for an encounter, and another system has one, the same Undo step adds a copy of it. The result tells it in `fixes`.

#### Example: a patrol and a wreck

These steps build an NPC patrol and a wreck with cargo in a system.

1. Find a free area with `list_items`, the system and the kind `zone`.
2. Find a faction with `list_values` and the list `faction`. Read the label of each row.
3. Find a patrol encounter with `list_values`, the list `encounter_of_system` and the system.
4. Call `scene_place_patrol_route` with a name, three points, a radius of 750 and one encounter group with the faction.
5. Find a wreck archetype with `list_values`, the list `solar_archetype` and the text "MISSION_SATELLITE".
6. Find the ship that the wreck was with `list_values` and the list `ship`.
7. Call `scene_place_wreck` with a position, the two archetypes, the cargo rows, a nickname and a name.
8. In the editor, call `render_scene` with the wreck in `frame`.
9. Call `check`.
10. Correct each error with `set_fields`.
11. Save the mod in the editor.

### The mission tools

The mission tools change the mission scripts of the active mod with the commands of the mission studio. They work in both hosts. `mission_edit` runs any commands of the mission studio. The trigger tools of "The trigger tools" add and change triggers and their lines with named arguments. `mission_add_item` adds a ship, an NPC or another item of a mission, as "The item tool" tells. `mission_place_prefab` places a behavior prefab, as "The prefab tool" tells. The generic edit tools rename, delete, copy and change mission items, as "The generic tools on mission items" tells. In the editor, `mission_play` plays a mission from a start, as "Mission play" tells.

`mission_edit` runs a list of commands. Each call is one Undo step with all its commands, as each other edit tool. It takes `preview`, `with_patch`, `if_revision` and `confirm`, as the edit tools do.

| Argument | What it does |
|---|---|
| `commands` | The mission commands in order. Each command is an object with a `kind`, for example `add_trigger`, `set_condition_value` or `remove_trigger`. The input schema of the tool gives each kind with its fields. |
| `label` | The label of the Undo step. Without it, the call takes the label of its one command, or "Mission edit". |
| `options` | `reject_new_warnings: true` also refuses an edit that adds a warning. Then a new warning gives the error `new_errors` with the detail `NewWarnings`. A new error gives the detail `NewErrors`. |
| `expect_versions` | The versions of the scripts that the agent read, by install path. When a script has another version now, the call gives `stale_revision` with the detail `ScriptChanged`. |

The input schema gives each command and the value names of each key of a line. So it is large: about 190 KB of JSON. An agent program can send the whole tool list to its model, and then the schema fills a part of the context of the model.

A line of a trigger is an object with its key and its values by name, for example `{"key": "Act_ActTrig", "values": {"trigger": "my_next"}}`. `describe_mission_name` gives the value names of each condition and action. A line can also give its value texts by position in `texts`.

Each command sees the result of the commands before it. So a later command can name a trigger that an earlier command added. When one command refuses, the call changes nothing, and the error names the command, for example `commands[2]`. A path of a reference must be an install path. A new script must be under `DATA\MISSIONS`. The commands do not change the world files of the missions, for example the pilot files, because encounters and bases use them too.

The mission tools always run in strict mode. An edit that adds an error to a script gives the error `new_errors` with the first new error, and nothing changes. `options` with `reject_new_errors: false` gives `invalid_argument`. A line that a delete or a rename leaves "Missing" does not count as a new error. But the delete or the rename needs `confirm: true`, as in "Preview, confirmation and revision". The other changes that the mission studio asks about need it too. An example is a change of a build in the shared ship build file of the missions.

Strict mode compares the problems of the mission checks before and after the edit. The save gate also has other rules. For example, in a mod that names a retail target, the loadout of a ship build must have an engine. `check` with the scope `mission` does not give the errors of these rules. `check` with the scope `all` gives each error that stops a save, so call it before `save`.

The result gives `inverse`: a mission command that reverts the call. To revert the call after later edits of other items, give the inverse in `commands` of a new `mission_edit` call. The inverse checks each section that the call changed or added. When a later edit changed such a section, the inverse gives `stale_revision` with the detail `SectionChanged`, and nothing changes. `undo` reverts only the last edit of the agent. `row_moves` gives the lines whose place in their trigger changed.

Each script has a version. It starts at 1 and grows at each edit, undo and redo of the script. The result of each edit call gives the versions of the scripts that it changed in `versions`. A rename of an object that changes the mission lines that name it gives them too. `list_missions` gives the version of each script.

`mission_edit` can be a step of `edit_batch`. The strict check of the missions runs once, at the end of the batch. So a step can start a trigger that a later step adds. Each mission step gives its own `inverse` in `steps`, and the batch gives no inverse. One undo reverts the whole batch.

One call has at most 100 commands. A `command_group` counts as one command. The limit of the sections of one edit and the size of a request bound its work.

#### The trigger tools

Each trigger tool changes one trigger or its lines. It runs the commands of the gestures of the mission studio in one request, so the text of the files is the same as after the gesture. Each call is one Undo step and runs in strict mode, as `mission_edit` does. Each tool takes `preview`, `with_patch` and `if_revision`, and it can be a step of `edit_batch`. The result gives `inverse`, `row_moves` and `versions`, as the result of `mission_edit` does.

| Tool | What it does |
|---|---|
| `mission_add_trigger` | Adds a trigger to `script` with `condition` and at most 64 `actions`. `starts_with_mission`, `repeats` and `system` set the state lines of the trigger. Each trigger of `started_by` gets an action that starts the new trigger. |
| `mission_set_condition` | Replaces the condition of `trigger`. |
| `mission_add_action` | Adds `action` to `trigger`, before the line `before`, or at the end. |
| `mission_set_action` | Sets each value of `values` in the action `line`, by the name of the value. With `expect_key`, a line with another key gives `stale_revision` with the detail `RowChanged`. |
| `mission_move_action` | Moves the action `line` before the line `before` of its trigger, or to the end. |
| `mission_link_triggers` | Adds a link from the trigger `from` to the trigger `to`. `start` adds `Act_ActTrig`, `stop` adds `Act_DeactTrig`, and `restart` adds both, the stop first. With `remove: true`, the tool removes the lines of the link. |
| `mission_insert_trigger` | Puts a new trigger on the start line `line`. The trigger of the line then starts the new trigger, and the new trigger starts the old target. The `actions` of the new trigger come before its start of the old target. |

A line is `{key, values}` with the value names of `describe_mission_name`, as in `mission_edit`. For example, `{"key": "Cnd_Timer", "values": {"seconds": 3}}` waits 3 seconds. A value is a number, a text, true or false, or a list for a position or a value that repeats.

A line can also give its value texts by position in `texts`, in place of `values`. A line with both gives `invalid_argument`. The editor checks only the form of a line with texts. A key without values, for example `Cnd_True`, needs no values. The editor then writes `Cnd_True = no_params`, as the game data and the mission studio do.

`trigger` and `from` take a `trg:` reference, or the `sec:` reference of a trigger section. `line` and `before` take a `line:` reference, for example `line:DATA\MISSIONS\M01A\m01a.ini#Trigger/my_trigger/3`. `get_trigger` gives the reference of each line. `before` must name a line of the same trigger. `to` and each trigger of `started_by` take a nickname or a `trg:` reference of the same script. A trigger of another script gives `invalid_argument`.

A new nickname has letters, digits, `_` and `-`, with at most 47 characters, and it cannot be a number. Without a nickname, the editor makes a free name, for example `m01a_trigger_3`.

A link to a trigger that does not exist adds an error, so strict mode refuses it. In `edit_batch`, the strict check runs at the end, so a step can link to a trigger that a later step adds. A link from a trigger of a linked prefab instance goes through the output of its prefab, as the link gesture of the graph does. The output then starts the target, and the instance keeps the lines of its prefab. With `remove: true`, the tool takes the target from the output. A trigger of an instance without such an output gives `refused` with the detail `PrefabMember`.

Each other trigger or line that does not exist gives `not_found`, and a trigger also gives the closest names. An error names its argument in `field`, for example `actions[2]` or `values.ship`. The result names each new or changed item in its shortest form: `trg:` for a trigger and `line:` for a line.

This call adds a trigger that the trigger `my_start` starts. The new trigger waits 3 seconds, then spawns a ship:

```json
{
  "script": "msn:DATA\\MISSIONS\\M01A\\m01a.ini",
  "nickname": "my_ambush",
  "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } },
  "actions": [ { "key": "Act_SpawnShip", "values": { "ship": "my_pirate" } } ],
  "started_by": [ "my_start" ]
}
```

#### The item tool

`mission_add_item` adds one item to `script`. `kind` tells the kind of the item, and `fields` gives its keys. Each call is one Undo step and runs in strict mode, as the trigger tools do. The tool takes `preview`, `with_patch` and `if_revision`, and it can be a step of `edit_batch`. The result gives the `sec:` reference of the new item in `adds`, and `inverse` and `versions`.

| Kind | The new item |
|---|---|
| `ship` | A ship, `[MsnShip]`. `fields.npc` must name an `[NPC]` of the script. |
| `formation` | A formation, `[MsnFormation]`. Its `ship` lines name ships that come before it in the script. |
| `npc` | The pilot and the voice of a ship, `[NPC]`. `npc_ship_arch` names a ship build. |
| `ship_build` | A ship build, `[NPCShipArch]`, in the ship build file of the script. |
| `solar` | A solar, `[MsnSolar]`. |
| `loot` | A loot, `[MsnLoot]`. |
| `order_list` | An order list, `[ObjList]`. `steps` gives its orders. |
| `objective` | An objective, `[NNObjective]`. |
| `conversation` | A conversation, `[Dialog]`. |
| `random_encounter` | A random encounter, `[MsnRandEnc]`. |

The fields are the keys of the section, as `get_item` gives them for an item of the same kind. A key that the section does not have gives `invalid_value` with the detail `NotAKey`, and the message gives the keys of the section. A random encounter takes each key, because the encounter reads the keys of its own grammar. The editor checks only the keys of a random encounter that it knows. The value of each field has one of these forms:

- One value: a number, a text, `true` or `false`. For example, `"system": "my_system"`.
- A list of numbers or texts for a position, a rotation or the values of one line. For example, `"position": [0, 0, 1000]` or `"npc_class": ["lawful", "d5"]`.
- An object of values by name, for a key with more than one value. For example, `"rel_pos": {"heading": 90, "anchor": "my_station", "range": 500}`. `describe_mission_name` does not give the names of the keys of a section. Read the item with `get_item` to see them.
- `{"texts": [...]}`: the value texts by position. The editor checks only the form of the line. For example, `"type": {"texts": ["ids", "12345"]}` for an objective. An empty list is a line without values: the editor gives each default value, or refuses a value that the key needs.
- For a key that repeats, for example `label`, `cargo` or the `ship` lines of a formation, a list of these forms. Each form is one line. One form without a list is one line too. For a key that repeats with a position, a list of numbers is one position. For example, `"formation_position": [0, 0, 100]` of a random encounter is one line, and `[[0, 0, 100], [0, 0, 200]]` is two lines.
- `null` or an empty list writes no line.

The tool writes the lines in the order of `fields`, with two exceptions:

- The `encounter_type` line of a random encounter comes first, because the encounter reads only the keys after it.
- The `formation` and `ship` lines of a formation come last, because the game can stop when a key of the ship comes after them.

Give the nickname in `nickname`, not in `fields`. The schema gives the ship key `cargo` only one value name, the good. To give the count of the good too, use the texts form, for example `"cargo": [{"texts": ["my_good", "5"]}]`.

A ship needs `fields.npc`, the nickname of an `[NPC]` of the script. Without it, the tool gives `invalid_argument` with the detail `NeedsNpc`. To make the build, the NPC and the ship in one Undo step, use one `edit_batch`. Its steps are `mission_add_item` calls with the kinds `ship_build`, `npc` and `ship`. `mission_edit` with `add_mission_ship` and `new_build` also makes all three.

A ship starts at `position`, or near an object with `rel_pos`, not at both. When a formation of the script names the new ship, the ship goes before the formation in the same Undo step. The game puts only an earlier ship in a formation. The save then places the ship with an `ol.before` line.

`steps` gives the orders of an order list in order, as lines of the 23 directives. An example is `{"key": "GotoVec", "values": {"movement": "goto_cruise", "point": [0, 0, 1000], "range": 100}}`. `describe_mission_name` gives the values of each directive. An order without values, for example `BreakFormation`, gets `no_params`, as the game data writes it. Only the kind `order_list` takes `steps`.

A new nickname has letters, digits, `_` and `-`, with at most 47 characters. A random encounter has at most 31 characters, because the game has room for only 31 characters of that name. A longer name gives `invalid_value` with the detail `NameInvalid`. Without a nickname, the editor makes a free name, for example `m01a_ship_2`.

A script that names no ship build file cannot get a ship build: the tool gives `refused` with the detail `NoShipBuildFile`. The inverse of a new NPC or ship build gives `refused` with the detail `RequiredBy` while a ship or an NPC names it. Remove the ship or the NPC first. The inverse of a new ship that a formation names gives `needs_confirmation`, because the `ship` line of the formation then names a ship that is gone. Call `mission_edit` again with the inverse and `confirm: true`.

This call adds a ship with the NPC `my_pirate_npc` at a position in the system `my_system`:

```json
{
  "script": "msn:DATA\\MISSIONS\\M01A\\m01a.ini",
  "kind": "ship",
  "nickname": "my_pirate",
  "fields": { "npc": "my_pirate_npc", "system": "my_system", "position": [8000, 0, -2000], "label": ["my_pirates"] }
}
```

#### The prefab tool

`mission_place_prefab` places one linked instance of a behavior prefab in `script`, as a drop of the prefab on the graph does. `script` is a mission script. A prefab cannot hold a prefab instance, so a `prefab:` reference as `script` gives `refused` with the detail `PrefabInPrefab`. `prefab` is a `prefab:builtin:` or `prefab:mod:` reference. Each call is one Undo step and runs in strict mode, as the trigger tools do. The tool takes `preview`, `with_patch` and `if_revision`, and it can be a step of `edit_batch`. `adds` gives the members of the instance and the `inst:` reference of the instance. A member trigger has its `trg:` reference, and a member item has its `sec:` reference, for example the `[ObjList]` of the built-in prefab Patrol. The result also gives `inverse` and `versions`.

`get_item` with the `prefab:` reference gives the fields, the outputs and the roles of a prefab. Each field gives its `kind` and its `default` rows. A list field also gives its `columns`. A condition field and a lines field give the `keys` that they take.

`fields` gives the values of the fields by field id. A field without a value takes the default of the prefab. The value of a field has the form of its kind:

| Kind of the field | Form of the value | Example |
|---|---|---|
| A value kind, for example `formation`, `trigger` or `number` | One value, or a list of numbers for a position or a rotation | `"ships": "my_wave"` |
| `list` | A list of rows. Each row is an object of values by column id. | `"waypoints": [{"point": [0, 0, 500]}, {"point": [1000, 0, 0]}]` |
| `triggers` | A list of triggers | `"targets": ["my_next"]` |
| `condition` | One condition line | `"spawnWhen": {"key": "Cnd_Timer", "values": {"seconds": 5}}` |
| `lines` | A list of action lines or order lines | `"sides": [{"key": "Act_SetVibe", "values": {"from": "my_ship", "to": "my_other_ship", "attitude": -0.9}}]` |

A line has the form of a line of the trigger tools. A trigger is a nickname or a `trg:` reference of the same script. A list is the form of a value only for a position or a rotation. So a list for a trigger, a name or a number gives `invalid_argument`, also in a column of a list row. `null` gives the field no value, and a field that needs a value then gives `invalid_value`. The editor writes the rows of the record of the instance in their text form, as the Inspector does. The agent never writes that text.

`outputs` gives the triggers that each output of the instance starts, by output id, for example `{"done": ["my_next"]}`. Each trigger then gets a start line in the member of the output.

`id` gives the id of the instance. The names of the members start with the id, for example `my_wave_spawn`. An id has letters, digits, `_` and `-`, with at most 47 characters. Without an id, the editor makes a free id.

A prefab that does not exist gives `not_found` with the closest names. An id in use, or a member name that another section of the script has, gives `name_taken`. A value that the field refuses gives `invalid_value` with the detail `FieldValue`. A value that a line of the game cannot hold gives `invalid_value` with the detail `PatchText`, for example a text with a comma or the number `1e300`. A value in another form gives `invalid_argument`. A field, a column or an output that the prefab does not have gives `invalid_argument` too.

The error names its argument in `field`:

- A value in another form, a column, an output or a trigger of another script: the place of the value, for example `fields.waypoints[1].point`, `fields.targets[0]` or `outputs.done[0]`.
- `FieldValue`: the field, for example `fields.delay`. A bad value in a row of a list field names the field too, for example `fields.marks`.
- `PatchText`: the value or the row, for example `fields.delay`, `fields.marks[0]` or `fields.sides[0]`.
- The game reads only the whole part of some values, for example the point of `GotoVec`. Such a value with a fraction gives `invalid_value` with the detail `TemplateLine` and the field `fields`, because the compile of the prefab finds it. Each other error of the compile names `fields` too.
- The strict check of the whole call gives `new_errors` without a field, for example for an output that starts a trigger that the script does not have.

This call places the built-in prefab Spawn wave. The wave spawns 5 seconds after the trigger starts, and the trigger `my_reward` starts when all its ships are destroyed:

```json
{
  "script": "msn:DATA\\MISSIONS\\M01A\\m01a.ini",
  "prefab": "prefab:builtin:spawnWave",
  "id": "my_wave",
  "fields": { "ships": "my_pirates", "spawnWhen": { "key": "Cnd_Timer", "values": { "seconds": 5 } } },
  "outputs": { "done": [ "my_reward" ] }
}
```

#### The start plan

`mission_start_plan` gives the plan of a play of `script` from `start`, as the Play start page of the editor shows it. It changes nothing. `start` is `mission_start`, or the `trg:` or `sec:` reference of a trigger of the script. `start_place` is `suggested`, `camera`, or `base:` and the nickname of a base. `target` takes only `openlancer` in this version. A play from a trigger in a retail game comes with a later version.

The result gives the plan in `plan`: the status, the kind of the start, the story state, the start place and the setup triggers. It also gives the replay path and the warnings. A plan that cannot start is data: `status` is `refused`, and `reason` tells why. For example, OpenLancer starts only the scripts of the 14 story slots, so the plan of another script gives `refused`. A trigger that the script does not have gives `not_found`. A trigger of another script gives `invalid_argument`. A `prefab:` reference as `script` gives `invalid_argument` too, because a play starts only a mission script.

#### The generic tools on mission items

`set_fields`, `set_text`, `rename_item`, `delete_items` and `duplicate_items` also change the items of a mission script and of the ship build file of a script. Each call runs the commands of the mission studio in one Undo step and in strict mode, as the mission tools do. One call can name items of a system and mission items together. All its commands are then one Undo step. The result gives `versions`, and `inverse` and `row_moves` when the call changed a mission item.

| Tool | Mission references | What it does with them |
|---|---|---|
| `set_fields` | `sec:` of an item, for example a ship, an NPC, a ship build or the `[Mission]` section | Sets each key with the field forms of `mission_add_item`. `null` or `[]` removes the key. |
| `set_text` | `line:`, `trg:` and `sec:` | Puts the text into the `strings.json` of the active mod, and the line gets its placeholder. See below. |
| `rename_item` | `trg:` and `sec:` | Renames the trigger or the item, as F2 does. Each line that names it changes too. |
| `delete_items` | `trg:`, `inst:` and `sec:` | Deletes as Delete in the graph and the Hierarchy does. See below. |
| `duplicate_items` | `trg:` and `sec:` | Copies as Ctrl+D does. A trigger copy gets the name of the trigger with `_2`, `_3` and so on. Each copy goes to the end of its file. |

`set_fields` does not change a trigger, because its lines are conditions and actions: it gives `refused`. Use the trigger tools or `mission_edit` for them. The key line `nickname` gives `refused` too. Use `rename_item` for the name. A key that the section does not have gives `invalid_value` with the detail `NotAKey`, and the message gives the keys of the section. The orders of an order list are not fields: use `mission_edit` with `set_orders`.

With a `line:` reference, `field` of `set_text` is the name of the value, for example `text` of `Act_ChangeState`. With a `trg:` or `sec:` reference, `field` is the key of a line, for example `individual_name` of an `[NPC]`. The tool then sets the first text id of the first line of the key. An item without such a line gets the line in the same Undo step, as the mission studio does. A trigger without such a line gives `invalid_argument`: add the action first. A key without a text id gives `invalid_argument` with the detail `NotTextId`. The message then gives the keys of the item that hold a text id.

`delete_items` deletes a trigger, a prefab instance with its members, and each other item. A mission ship goes with its NPC and its ship build when nothing else uses them. When lines name a deleted item, they stay and show "Missing", and the delete needs `confirm: true`. An `[NPC]` that a ship names and a ship build that an `[NPC]` names give `refused` with the detail `RequiredBy`. The `[Mission]` section gives `refused`. To delete a whole script, use `mission_edit` with `delete_script`.

One `delete_items` call can name an item and an item that its delete removes. Examples are a prefab instance and one of its members, and a mission ship and its NPC. The tool deletes each item one time, as Delete in the graph does. It deletes the prefab instances first, then the triggers, the other items and the ships, and last the NPCs and the ship builds. So the order of the references does not change the result. An NPC that another ship still names gives `RequiredBy`.

Two triggers can have the same nickname. The game runs only the first, and the second has a `sec:` reference with an ordinal, for example `sec:...#Trigger/ol_wave~1`. `delete_items` deletes the second trigger with this reference. `rename_item` and `duplicate_items` find a trigger by its nickname, so they refuse the second trigger with `refused` and the detail `MissionItem`.

`duplicate_items` with `offset` moves no mission copy. The result warns about each mission copy. To move a copy with a position, for example a ship or a formation, use `set_fields`.

Each tool refuses the other mission references with `invalid_argument` and the detail `MissionItem`. Examples are a `label:` reference, a `line:` reference in `delete_items`, and a `msn:` or `act:` reference. The message names the tool for the change, for example `mission_edit` with `remove_action` to delete a line.

The generic tools and the other edit tools that take a `sec:` reference refuse a section of another file under `DATA\MISSIONS`, because encounters and bases use the data of these files too. A pilot file and the other world files give `refused` with the detail `WorldFile`, as in `mission_edit`. A shared ship build file, for example `DATA\MISSIONS\npcships.ini`, gives `refused` with the detail `SharedBuildChange`. `mission_edit` changes the lines of a build of this file after a confirmation: `set_item_key` and `set_line` give `needs_confirmation`. Random encounters can use the build, so the agent asks you first. Then it calls `mission_edit` again with `confirm: true`. The mission commands do not rename, delete or copy a build of this file.

### Save

`save` saves the edits of the active mod into its patch files, as **File > Save** does. In the editor, the save writes its report to the Console, loads the game data again and validates the saved files. The result of `save` then also gives the result of the validation. The oltool host does not validate.

The save gate runs first, as in the editor. When the checks find errors, the save stops and writes nothing. `save` then gives the error `save_blocked` with the messages of the first errors. Call `check` to read each error. When a file of the mod changed outside OpenLancer, `save` gives `changed_on_disk` and writes nothing. In the editor, a dialog then asks you to replace the file or to keep it. The agent never replaces such a file. While an export runs, `save` gives `busy` and writes nothing, because the export reads the saved files.

The result gives the written and the deleted files, relative to the folder of the mod. A save does not change the revision `rev`, and it is not an Undo step. The server instructions tell the agent to call `check` before `save`, and to save only when you ask for it.

### The export

`export_mod` exports the saved files of the active mod as packages, as the Export dialog does. It runs the checks of each target, the same-version guard, the write of the packages and the record in the publish history. The packages always go into the default export folder of the mod: `exports\<mod id>\<version>` in the workspace of the profile. The tool takes no folder.

| Argument | What it does |
|---|---|
| `targets` | The target games of the packages. Without it, the export takes each target of `mod.json` that has a linked game. In the oltool host, the base folder of the profile is the one linked game. |
| `formats` | The package forms of a retail target: `drop_in`, `flmm` or both. Without it, the export makes both. |
| `replace_version` | Replaces an earlier export of the same version with other game files, as **Replace version** of the Export dialog does. |

The result tells each package and if the export wrote it. `recorded` tells if the publish history recorded the export. An export with a failed package also records the packages that it wrote.

A mod with an infocard needs the drop-in form now. An infocard of the editor starts with an XML header with quotes, and the FLMM form has no known escape for a quote. So an FLMM package of such a mod fails, and the export tells it. `scene_place_wreck` always writes an infocard. Give `"formats": ["drop_in"]` for such a mod.

The export refuses unsaved edits, because it uses the saved files. Save first. It also refuses while another export runs, a version that is not safe in a path and a publish history that does not load. A version with the mark "published" always gives the error `guard_conflict`. An earlier export of the version with other game files gives `guard_conflict` without `replace_version`.

An export can take minutes. A client that asks for progress gets a progress notification at each step, before the result of the call. The time limit of an export is 10 minutes. When the call stops before the write, for example at a cancel of the client, the export writes nothing. A write that started always goes on to its end, and the publish history records it. While an export of the agent runs, the Export dialog and the Mods window wait for it. **File > Save** also writes nothing while an export of the agent runs, and the Console tells why.

### The publish checklist

`publish_checklist` gives the steps of the publish checklist of the active mod, as the Mods window and `oltool publish --json` give them. Each step is done, or tells what is missing and why. The checklist reads the saved files, so it does not count unsaved edits. The result then has a note. The tool changes nothing, so it also works while you pause the agent edits.

### Play

`play` and `play_stop` work only in the editor host. `play` starts the game in OpenLancer, as the Play menu does.

| Argument | What it does |
|---|---|
| `start` | `space` for a point in a system, or `base` for a dock at a base. |
| `system`, `position`, `orientation` | The start in space: the system, the point, and the heading of the ship in degrees, as the `rotate` key holds it. The Scene must show the system, because Play checks the start point with the shapes of the Scene. When a planet, a station or a death zone holds the point, Play moves the start out of it. |
| `base` | The base of a start at a base. |

A play of an agent never saves on its own. When the active mod has unsaved edits, the editor asks you to save them, with the buttons "Save and continue" and "Cancel". Cancel gives the agent the error `refused`, and Play does not start. The setting "Auto-save and continue when an MCP agent starts Play" skips the question. When the call ends before you answer, for example at its time limit, the question closes as Cancel does. "Agent Play" in [PLAY.md](PLAY.md) gives each case.

While a game runs, `play` gives `refused`, and the agent calls `play_stop` first. The Play menu closes the running game instead. `play` also gives `refused` when the checks of the Play menu refuse the start. An example is a system that the game data does not have. These checks are the same for the menu and the agent.

When Play saves the active mod, the save gate runs first, as for `save`. When the save stops, `play` gives `save_blocked` or `changed_on_disk`, and the game does not start. A second play request of an agent gives `busy` until the earlier request ends, because one dialog shows the question. Without an open profile, `play` gives `not_available`, as the tools that read the profile do. `play_stop` also works without a profile.

`play_stop` stops the game of Play in OpenLancer, else the game of a play copy, as Shift+F5 does. It also stops a mission play. It gives what it stopped and the state of Play. A play and a stop are not Undo steps.

#### Mission play

`mission_play` plays one mission script in OpenLancer from a start. It works only in the editor host. It takes the arguments of `mission_start_plan`: `script`, `start`, `target` and `start_place`. So `mission_start_plan` with the same arguments gives the plan of the play first. OpenLancer plays only the scripts of the 14 story slots. `target` takes only `openlancer` in this version.

The rules of `play` apply to `mission_play` too:

- The play never saves on its own. When the active mod has unsaved edits, the editor asks you first, and Cancel gives `refused`. The setting "Auto-save and continue when an MCP agent starts Play" skips the question.
- When the call ends before you answer, the question closes as Cancel does.
- One dialog shows the question for both tools. So a second request of either tool gives `busy` until the first request ends.
- When the save gate stops the save of Play, the tool gives `save_blocked` or `changed_on_disk`.
- Without an open profile, the tool gives `not_available`. `mission_play_trace` also works without a profile.

These cases are special to `mission_play`:

- While a mission play runs, the game starts again at the new start, as F5 does. While another play runs, the tool gives `refused`, and the agent calls `play_stop` first.
- When the plan of the start cannot start, the tool gives `refused` with the reason of the plan. A trigger or a script that does not exist gives `not_found`, and `field` is `start` or `script`. The editor does this check again after you answer the question.
- The play never changes the play start of the script in your settings. F5 still plays your play start.
- When you clear the group Missions or the group Play, the tool gives `not_available`.

The result gives the state of Play, the start, the kind and the name of the start, and the warnings of the plan. Play makes the plan from the saved script. When the saved script does not have the trigger of the start, the game plays the mission start, and `start` is `mission_start`.

`mission_play_trace` gives the trigger changes of the mission of the OpenLancer game, oldest first: each trigger that starts, runs or stops. It works only in the editor host, and it changes nothing. Each change has a sequence number in its play, its time in seconds and the `trg:` reference of its trigger. The number of the play grows at each start of a game and at each start again of a mission play.

The reference gives the name of the trigger now. When you rename a trigger after the game loaded the mission, the trace gives the new name. This is also true after an undo or a redo of the rename. So `get_trigger` and the mission tools find the trigger of each change. Play saves before it starts the game, so at the load the game has the names of the editor. Play > Restart the mission does not save. After a rename that you did not save, the game then loads the old name again, and the trace gives the old name.

| Argument | What it does |
|---|---|
| `play` | The play number of the last result, or 0. Another number than the current play gives all changes of the current play. |
| `since` | Give only the changes after this sequence number. Use the sequence of the last change of the previous result. A value above 0 needs `play`. Without `play`, the tool gives `invalid_argument`. |
| `limit` | The most changes of the result. When the play has more changes, `more` is true. Call the tool again with the play number of the result as `play` and the last sequence as `since`. |

The result also gives the state of Play, the mission and its script. `dropped` gives the number of changes that the game dropped because its queue was full. While only the game of a play copy runs, the result has no changes, because that game sends no changes to the editor. `game_runs` is then true.

### What you see in the editor

The editor shows each agent edit in the Console, in the Scene, in the Hierarchy and in the status bar.

| Place | What it shows |
|---|---|
| Console | Each agent edit adds an Info entry with the source "Agent" and the label of the edit, for example "Create Zone_test_01.". Click the entry to select its item. A refused call of a tool that changes data adds a warning with the tool and the reason, also a call that waited too long. An error in the second part of a call adds no entry yet. Examples are a Play that you cancel, an export with `guard_conflict` and a save that the save gate of Play stops. An undo and a redo of the agent add an entry too. A preview, a read tool and the question `needs_confirmation` add no entry. When the editor cannot show an agent edit, it adds an Error entry with the reason. The edit stays in the Undo history. **Agent > Show agent messages** shows only the entries of the agent. |
| Scene | A pulse ring in the agent color goes around each item of an agent edit for 4 seconds. |
| Hierarchy | A robot glyph at the right end of a row shows an item that an agent edit changed after the last save. The blue mark of the unsaved edit stays, so the row shows both marks. `list_items` gives the same mark as `agent_changed`. In the tree of a mission, the row of a trigger or of another section of the script shows the glyph too. The header row shows it for the `[Mission]` section. The rows of a mission have no blue mark. |
| Status bar | The agent program and the number of its edits since the last save, or "Agent paused". |

An undo of an agent edit removes its marks, and a redo puts them back. A save clears the marks.

An agent call never runs during a gesture of your own. It waits while you edit a field, rename an item or drag an item, and while a dialog is open. In the mission studio, it waits while one of these is open:

- The rename popup of a row of a mission in the Hierarchy.
- The rename field of a node of the graph, and quick add.
- A drag of a node or of a link of the graph.
- A drag of an item of the library, of an action row of the graph or of a row of the tree of a mission.
- The rename popup and quick add of the Mission page in the Inspector, and the rename popup of a prefab in the Assets panel.

The call runs in the frame after the gesture ends. A gesture in a tab that you hide does not keep the call waiting. After the wait limit of the agent settings, the call gives the error `busy` with the reason.

#### Follow mode

With **Agent > Follow agent** on, the editor opens the system of each agent edit and moves the camera to its item. An agent edit of a mission opens the script in the Mission tab, and the graph moves to the item. While the Mission tab shows the raw INI, the view does not move, also when you show the graph again. An item without a node, for example a mission ship, shows in the Hierarchy and the Inspector when it is selected. An item without a place in the Scene, for example a loadout, shows in the Inspector, and the view does not change. So does an item that the Mission tab cannot show yet, for example a trigger of a new script before the mission run reads the script.

The selection follows Unity. A create or a copy selects the new item, a delete clears the selection, and a rename keeps it. When the agent undoes its delete, follow mode selects the item again. When you undo an agent delete, the Scene selects the item again, as after the undo of your own delete. This is also true with follow mode off. An undo of an agent delete in a mission keeps the selection of the Mission tab, as the undo of your own mission edit does.

Follow mode does not leave prefab mode, as Unity keeps prefab mode open when the scene changes. While the Mission tab shows a prefab, an agent edit of a mission script or of its ship build file shows only in the Console. The selection and the view stay.

When you move the camera of the Scene or select an item, follow mode stops. Click **Follow agent** to start it again. The settings keep the last value that you clicked, so follow mode is on again at the next start of the editor. With follow mode off, the agent changes the view and the selection only with `open_view` and `select_items`. When an agent edit removes the selected item, the selection goes, as after an undo. When an agent edit renames the selected item or moves the row of a selected line, the selection stays on the same item.

#### Pause

**Agent > Pause agent edits** stops each agent edit until you pick **Resume agent edits**. A click on the agent segment of the status bar opens the same menu. While you pause, each tool that changes data, writes files or starts a play gives the error `paused`. The read tools, the view tools, `publish_checklist` and `play_stop` still work. The pause stays when you stop and start the server again.

## Resources and prompts

The server gives these resources. An agent program can read them before its first call. The resources hold the texts and the rule data of OpenLancer. The catalog of the prefabs also holds the prefab assets and the prop groups of the open profile.

| Resource | What it gives |
|---|---|
| `openlancer://schema/mission-names` | The 129 conditions and actions of a trigger. Each one has its plain name, its event or action group, its values with their kinds and help, and what OpenLancer does with it. These are the rows of `describe_mission_name` without the counts of the base game. |
| `openlancer://schema/mission-sections` | The 12 section types of a mission script and the ship build (`[NPCShipArch]`), with their keys and values. A type that `mission_add_item` adds has its kind, for example `ship` for `[MsnShip]`. |
| `openlancer://schema/objlist-directives` | The 23 orders of an order list, with their values. The `steps` of `mission_add_item` take them. |
| `openlancer://catalog/prefabs` | The behavior prefabs with their fields: the built-in prefabs and the prefab assets of the mods. Also the templates of `scene_create_object`, the builders `scene_place_wreck` and `scene_place_patrol_route`, and the prop groups of the profile. Without a profile, it gives the built-in prefabs and the rule data only. |
| `openlancer://catalog/lists` | The data lists with their ids and labels. `list_values` takes the ids. |
| `openlancer://guide/rules` | The rules of the editor and what they mean for an agent. |
| `openlancer://guide/refs` | The forms of the item references. |

The catalog of the prefabs reads the open profile, so its read waits in the queue of the tool calls. It waits while you edit a field or do a gesture, as a call does. It counts in the calls each second. Above the calls each second or after the wait limit, the read gives a protocol error with the reason. The other resources read only the rule data, so they do not wait.

The server gives one prompt. An agent program shows a prompt as a command. In Claude Code, the command is `/mcp__openlancer__two_trigger_mission`.

| Prompt | Argument | What it gives |
|---|---|---|
| `two_trigger_mission` | `script`: the `msn:` reference of a mission script. `list_missions` gives the scripts. | The steps to add a pirate ambush with two triggers. The agent adds the ship build, the NPC and the ship with `mission_add_item`. It adds the two triggers and the loot in one `edit_batch`. Then it runs `mission_dry_run` and `check`. The names in the steps are examples. |

The prompt needs the edit tools. So a host that only reads, for example `oltool mcp serve --read-only`, lists no prompt. The prompts `build_outpost` and `patrol_and_wreck` and the resource `openlancer://guide/workflows` wait for a decision of the project. The prompt `short_cutscene` comes with the cutscene tools.

## Permission rules for the agent program

An agent program can ask you before each tool call. A permission rule of the agent program can allow a set of tools without a question. Claude Code gives each tool of the server `openlancer` the name `mcp__openlancer__<tool>`.

The name of a read tool starts with `list_`, `get_`, `find_`, `search_`, `describe_` or `check`, or is `studio_status`, `mission_dry_run`, `mission_start_plan`, `mission_play_trace` or `publish_checklist`. So these rules allow all read tools:

```
mcp__openlancer__list_*
mcp__openlancer__get_*
mcp__openlancer__find_*
mcp__openlancer__search_*
mcp__openlancer__describe_*
mcp__openlancer__check
mcp__openlancer__studio_status
mcp__openlancer__mission_dry_run
mcp__openlancer__mission_start_plan
mcp__openlancer__mission_play_trace
mcp__openlancer__publish_checklist
```

The image tools also change nothing, but their names do not match these prefixes. Allow them only when you accept that their images go to the model service of the agent program:

```
mcp__openlancer__render_*
mcp__openlancer__capture_panel
```

`open_view` and `select_items` change what the editor shows. Without a rule, the agent program asks you before each call. For the oltool host, the names start with `mcp__openlancer-headless__` (the name of the server in the example below).

The rules above do not allow an edit tool, `save` or a play tool. So the agent program asks you before each edit, each save and each play, unless you add a rule for it. Each edit is one Undo step, so you can undo an edit that you allowed. A save is not an Undo step.

## The oltool host

Add a server that starts oltool to `.mcp.json`:

```json
{
  "mcpServers": {
    "openlancer-headless": {
      "type": "stdio",
      "command": "<OpenLancer folder>\\oltool.exe",
      "args": ["mcp", "serve", "<workspace>\\profiles\\my-profile.json", "--mod", "my-mod"]
    }
  }
}
```

`oltool mcp serve <profile.json> --mod <id>` opens the profile with the mod as the active mod. Then it serves the tools on its standard input and output. It writes its own messages to the standard error.

The oltool host edits the active mod with the same tools, checks and Undo history as the editor host. It has its own Undo history, which ends with the server. It takes the lock of the mod, as the editor does. So when the editor has the mod open, the oltool host takes no edits, and the edit tools give `read_only`. Use the agent server of that editor instead. Each edit stays unsaved until a call of `save`.

The oltool host writes the unsaved edits to the recovery file of the mod 2 seconds after the last edit. It writes them again at the end of its standard input. The agent program can end the server at any time, and the next start of the editor then offers to restore the edits. A waiting recovery file of an earlier session stops the edits until you decide.

| Option | What it does |
|---|---|
| `--read-only` | Opens the profile only to read. The server takes no lock, writes no file and lists only the read tools. |
| `--no-save` | Leaves out `save` and `export_mod`. The edits stay unsaved. The recovery file still keeps them. The prop group tools stay, so `prop_group_save`, `prop_group_rename` and `prop_group_delete` still write and delete the files of a group at once. |
| `--restore-recovery` | Restores the unsaved edits of an earlier session from the recovery file at the start. |
| `--discard-recovery` | Deletes the unsaved edits of an earlier session at the start. |

The oltool host never edits a mod in the OpenLancer repo, because it cannot ask you. It opens such a mod only to read, and the edit tools give `read_only`. A profile or an active mod in the game folder also opens only to read.

## The audit log

The editor writes each call of an agent into an audit log: one file for each run of the editor, for example `%LOCALAPPDATA%\OpenLancer\logs\agent-20261020-142233.jsonl`. Each line holds the time, the tool, the arguments and a summary of the result. A call that did not run, for example a call above the rate limit, has no arguments in the log. The log holds no token. At 64 MB, the log stops, and the Console tells you.

`oltool mcp replay <profile.json> --mod <id> <log>` runs the calls of a log again and compares each result with the recorded result. It exits with the code 1 at the first result that differs. The edit calls of the log change the profile that the replay gets, so give it a copy of the profile and its mods. The replay saves only at a recorded `save` call, and it writes no recovery file. With `--preview`, the replay changes no file. The edits change only the data in memory, so a later call can still use the items of an earlier call. The replay then skips the calls that write files: `save`, `export_mod`, `prop_group_save`, `prop_group_rename` and `prop_group_delete`.

## Safety

### 127.0.0.1 only

The server accepts connections from this computer only:

- It listens on 127.0.0.1, on one port. No setting, environment variable or settings file can add another address.
- It closes each connection that does not come from this computer.
- It refuses a request with another host name and a request from a web page.
- It refuses a request with the forward header of a proxy or a tunnel.
- Each request needs the token.

Never put a proxy or a tunnel in front of the port of the agent server. A Tailscale Funnel is such a tunnel. The oltool host opens no port.

### The active mod only

The agent works on the open profile with its active mod, as you do in the editor. It reads the data of the base game and of the mods up to the active mod. An edit tool changes only the edit view of the active mod. `save` writes the edits into the patch files of the active mod. `export_mod` writes packages into the default export folder of the mod only. The prop group tools write the files of a group in the folder of the active mod. No tool writes into the game folder or into another mod. No tool takes a path on the disk: each path in a reference is an install path, for example `DATA\SHIPS\loadouts.ini`.

### Data that goes to the agent

The agent reads the game data and the mod data of the profile, the Console messages and images of the editor panels. The agent program can send all of it to its model service. The Console messages and the images can show the names of your folders.

Names and texts in the data can hold text that looks like an instruction. The tools give such text as a value in a field of the result, never as an instruction. The agent can only call the fixed tools.
