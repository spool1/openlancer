# The agent server (MCP)

OpenLancer has an agent server. An agent program, for example Claude Code, connects to it and reads the data of a profile. The server uses the Model Context Protocol (MCP), so each agent program with MCP support can use it.

The agent reads the data as the editor shows it. It reads the systems and their items, the data lists, the assets, the missions and the cutscenes. It also reads the raw INI text and the problems of the checks. In the editor, the agent can also open views, select items, and get images of a system, of a cutscene and of a panel.

The agent can also edit the items of the active mod. It sets fields and texts, renames, deletes and copies items, and undoes its own edits. It creates objects, zones and lights, places assets and prop groups, moves items and makes items from templates. Each agent edit goes through the same commands, checks and Undo history as an edit of the user. An edit changes the edit view of the editor. It is not saved until you or the agent save the mod. See "The edit tools", "The Scene and data tools" and "Save". In the editor, the agent can also start and stop Play. See "Play". The editor marks each agent edit, and you can pause the agent edits. See "What you see in the editor".

## The two hosts

Two programs can host the server. They give the same tools, with the same names and results.

| Host | How it starts | Transport | Tools |
|---|---|---|---|
| The editor | **Agent > Start agent server** in the editor | HTTP on `http://127.0.0.1:<port>/mcp` | All tools |
| oltool | The agent program starts `oltool mcp serve` | Standard input and output (stdio). It opens no port. | All tools. The tools of the editor give `not_available`. |

Use the editor host when you work in the editor. The agent then sees your unsaved edits, and the view tools show its work to you. Use the oltool host for a script or for an agent that works without the editor. An editor-only tool gives the error `not_available` in the oltool host.

## Connect an agent to the editor

1. Open a profile in the editor.
2. Pick **Agent > Start agent server**.
3. At the first start, read the notice. It tells what the agent reads. Click **Start server**.
4. Pick **Agent > Connect an agent...**.
5. Click **Copy**.
6. Paste the text into the file `.mcp.json` in the folder of your agent project.
7. Start the agent program in that folder.

The status bar shows the state of the server, for example "Agent server: on, port 47815". While an agent calls, it shows the name of the agent program and the number of its edits since the last save, for example "Agent: claude-code, 3 edits". While you pause the agent edits, it shows "Agent paused". Click the status bar segment to open the Agent menu.

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
| Pause agent edits | Stops all agent edits until you pick **Resume agent edits**. The agent can still read the data. |
| Follow agent | Opens and frames the item of each agent edit. It is on by default. It stops when you move the camera or select an item. |
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
| Auto-save and continue when an MCP agent starts Play | Off | When an agent starts Play and the active mod has unsaved edits, the editor saves them and plays. Off: the editor asks you first. See "Agent Play" in [PLAY.md](PLAY.md). The setting applies to the tool `play`. |
| Agent tool groups | All groups | The agent can use the tools of the selected groups: Scene, Missions, Cutscenes and Play. The `scene_` tools, the `prop_group_` tools and `render_scene` are in the group Scene. `render_cutscene` is in the group Cutscenes. `play` and `play_stop` are in the group Play. These read tools are always available: `studio_status`, `get_item`, `find_references`, `list_values`, `get_raw_ini`, `check` and `get_console`. The edit tools of "The edit tools" are always available too, and so are `save`, `export_mod`, `publish_checklist`, `create_loadout`, `create_from_template`, `open_view`, `select_items` and `capture_panel`. A tool of a cleared group tells the agent that you cleared the group. An edit tool also refuses an item of a cleared group, for example an object of a system when you clear the group Scene. |
| Rows of a page without a limit | 50 | The rows of a list result when the agent gives no limit. |
| Largest page limit | 200 | The largest limit that the agent can ask for in one list result. |
| Size of a result (KB) | 256 | The most text of one result. A larger result asks the agent for pages. An edit with a larger result changes nothing. |
| Calls each second | 10 | The most calls that the server takes in one second. |
| Wait for the editor (s) | 30 | How long a call waits while you edit a field or a dialog is open. |
| Time of a check or an image (s) | 60 | How long a check of a retail target or an image can take. |
| Size of a request (MB) | 4 | The largest request. The change applies at the next start of the server. |
| Largest side of an image (pixels) | 2048 | The most pixels on each side of an image. A larger panel capture becomes smaller. A `render_scene` or `render_cutscene` call with a larger `width` or `height` gives the error `limit`. |
| Items of one edit | 200 | The most items that one call of `delete_items`, `duplicate_items` or `prop_group_save` names, and the most moves of one `scene_transform` call. |
| Sections of one edit | 2000 | The most sections that one edit call changes, adds or removes. A larger edit changes nothing and gives the error `limit`. |
| Steps of one batch | 100 | The most steps of one `edit_batch` call. |
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
| `set_text` | Sets the name, the infocard or another text of one item. The text goes into the `strings.json` of the active mod. |
| `rename_item` | Gives an item a new nickname, and changes each line that names it. A line that the active mod cannot change keeps the old nickname and shows "Missing". A data item must be an item that the active mod adds. |
| `delete_items` | Deletes items of a system with their children. Other lines that name them show "Missing". |
| `duplicate_items` | Copies items of a system with their children. With `offset`, the copies move by the offset. A copy without a position does not move, and the result warns about it. |
| `undo` | Undoes the last edit of the agent. |
| `redo` | Redoes the last edit that the agent undid. |
| `edit_batch` | Runs edit steps as one Undo step, all or nothing. |

Each edit is one Undo step. The Edit menu shows its label with "Agent: " before it, for example "Undo Agent: Delete my_station_1". You can undo and redo each agent edit, as each of your own edits. `undo` undoes only an agent edit at the top of the history. `redo` redoes only an agent edit that the agent undid. So the agent never undoes your edit, and never redoes an edit that you undid with Ctrl+Z.

The edit tools change only items of INI files. A mission script and a cutscene give the error `not_available`. The mission tools and the cutscene tools come with a later version. `delete_items` takes items of a system only: objects, zones, lights, nebulae and asteroid fields. `duplicate_items` takes objects, zones and lights of a system.

#### Values of set_fields

`set_fields` takes the INI key of each field, as `get_item` gives it:

- A text, a number or `true` or `false` is one line with one value, for example `"loadout": "my_loadout"`.
- An array of values is one line, for example `"pos": [100, 0, -500]`.
- An array of arrays gives one line for each array, for example `"cargo": [["my_good", 5], ["my_other_good", 1]]`.
- `null` or `[]` removes the key. The engine then uses its default.

A value never holds a comma. The key line, a key of linked lines and the `file` of a system are locked, and `get_item` marks them. Use `rename_item` for the nickname. A value that a list check refuses still applies, as in the Inspector. The result gives the problem in `new_problems`.

#### Preview, confirmation and revision

Each edit tool takes these arguments:

| Argument | What it does |
|---|---|
| `preview` | Runs the edit and gives its result, then changes nothing. The revision, the history and the unsaved state stay the same. The views do not change. |
| `with_patch` | With `preview`: also gives the patch text of each INI file that the edit changes. |
| `if_revision` | Refuses the edit with `stale_revision` when the edit view changed after this revision. Each result gives the revision as `rev`. A save does not change it. |
| `confirm` | `rename_item` and `delete_items`: applies an edit that changes more than its targets. The prop group writes: writes the files. |

When you would see a dialog for a delete or a rename, the tool gives `needs_confirmation`, and nothing changes. A delete or a rename that leaves "Missing" lines also needs it. The agent then calls the tool again with `confirm: true`. A preview tells it in `needs_confirmation`.

The result of an edit gives the new items (`adds`), the changed items (`changes`), the removed items (`deletes`) and the renamed items (`renames`). It also gives the "Missing" lines, the warnings, the text mentions, and the problems that the edit adds and removes. A call that changes nothing gives the status `no_change` and no Undo step.

#### Batches

`edit_batch` takes a list of steps. Each step names an edit tool and its arguments. All steps are one Undo step with the label of the batch. Each step sees the result of the steps before it, so a later step can name an item that an earlier step made or renamed.

When one step fails, the error `step_failed` gives its index and its error, and the batch changes nothing. A step that needs a confirmation fails unless its arguments have `confirm: true`. `undo`, `redo`, `edit_batch`, the read tools and the tools of the editor cannot be steps. `prop_group_save`, `prop_group_rename` and `prop_group_delete` cannot be steps either, because they write files at once.

#### Limits and pauses

One call names at most 200 items and changes at most 2000 sections. One batch has at most 100 steps, and one route has at most 50 legs. **Agent > Agent settings > Advanced** changes these limits. A call above a limit gives the error `limit` and changes nothing. The size limit of a result applies to an edit too. The edit checks the size of its result before it applies, so a larger edit also changes nothing.

While you pause the agent edits, each tool that changes data, writes files or starts a play gives the error `paused`. Examples are the edit tools, `undo`, `redo`, `save`, `export_mod` and `play`. The read tools still work, also `publish_checklist`. `play_stop` also works, so the agent can stop its game. Pick **Agent > Pause agent edits** to pause the agent edits, and **Resume agent edits** to let the agent edit again.

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

- `create_loadout` adds the loadout to the first loadout file of `freelancer.ini`, the ship loadout file in retail. Its archetype is a ship or a solar archetype. A hardpoint has no list in the rule data. So a hardpoint that the `hp_type` lines of the ship do not name gives a warning, not an error.
- `scene_place_wreck` takes a solar archetype of the type `MISSION_SATELLITE` and the ship that the wreck was. It adds a loadout with that ship, the `equip` rows and the `cargo` rows. It also adds the object with the `loadout` line and `visit = 16`, as most retail wrecks have. The loadout gets the nickname of the wreck with `_loadout`. Without `name`, the wreck gets the name "Wreck". It always gets a short infocard.
- `scene_place_patrol_route` adds one cylinder zone for each leg between two next points. The zone has its center at the middle of the leg, and its local Y axis goes along the leg. Its `size` is the radius and the length of the leg. Its `path_label` is the name of the route and the number of the leg, from 1. The other keys come from the route template of the usage, `patrol` or `trade`. `fields` changes these keys on each zone. It cannot give a key that the tool writes itself, for example `pos`, `size`, `usage` or `encounter`. Such a key gives `invalid_argument`, and the error names the argument to use. Each coordinate of a point is from -10000000 to 10000000 meters, as the position of a wreck. A path label that the system has gives `name_taken`.
- `scene_set_encounters` writes the `faction_weight` and the `density_restriction` lines before the first `encounter` line. Then each `encounter` line comes with its `faction` lines, because the game gives a faction line to the encounter line before it. Without `faction_weights` or `density_restrictions`, the zone keeps these lines. A kept `density_restriction` line after an `encounter` line moves before the first `encounter` line, because the game reads it at each place in the zone. A `faction_weight` line after an `encounter` line belongs to that encounter, so the new groups replace it. An empty `encounters` list removes the encounter groups.

An encounter must have an `[EncounterParameters]` section in the system file. When the system has none for an encounter, and another system has one, the same Undo step adds a copy of it. The result tells it in `fixes`.

#### Example: a patrol and a wreck

These steps build an NPC patrol and a wreck with cargo in a system.

1. Find a free area with `list_items`, the system and the kind `zone`.
2. Find a faction with `list_values`, the list `faction` and a text, for example "pirate".
3. Find a patrol encounter with `list_values`, the list `encounter_of_system` and the system.
4. Call `scene_place_patrol_route` with a name, three points, a radius of 750 and one encounter group with the faction.
5. Find a wreck archetype with `list_values`, the list `solar_archetype` and the text "MISSION_SATELLITE".
6. Find the ship that the wreck was with `list_values` and the list `ship`.
7. Call `scene_place_wreck` with a position, the two archetypes, the cargo rows, a nickname and a name.
8. In the editor, call `render_scene` with the wreck in `frame`.
9. Call `check`.
10. Correct each error with `set_fields`.
11. Save the mod in the editor.

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

While a game runs, `play` gives `refused`, and the agent calls `play_stop` first. The Play menu closes the running game instead. `play` also gives `refused` when the checks of the Play menu refuse the start, for example for a system that the game data does not have. These checks are the same for the menu and the agent.

When Play saves the active mod, the save gate runs first, as for `save`. When the save stops, `play` gives `save_blocked` or `changed_on_disk`, and the game does not start. A second play request of an agent gives `busy` until the earlier request ends, because one dialog shows the question. Without an open profile, `play` gives `not_available`, as the tools that read the profile do. `play_stop` also works without a profile.

`play_stop` stops the game of Play in OpenLancer, else the game of a play copy, as Shift+F5 does. It gives what it stopped and the state of Play. A play and a stop are not Undo steps.

### What you see in the editor

The editor shows each agent edit in the Console, in the Scene, in the Hierarchy and in the status bar.

| Place | What it shows |
|---|---|
| Console | Each agent edit adds an Info entry with the source "Agent" and the label of the edit, for example "Create Zone_test_01.". Click the entry to select its item. A refused call of a tool that changes data adds a warning with the tool and the reason. An undo and a redo of the agent add an entry too. A preview and a read tool add no entry. **Agent > Show agent messages** shows only the entries of the agent. |
| Scene | A pulse ring in the agent color goes around each item of an agent edit for 4 seconds. |
| Hierarchy | A robot glyph at the right end of a row shows an item that an agent edit changed after the last save. The blue mark of the unsaved edit stays, so the row shows both marks. `list_items` gives the same mark as `agent_changed`. |
| Status bar | The agent program and the number of its edits since the last save, or "Agent paused". |

An undo of an agent edit removes its marks, and a redo puts them back. A save clears the marks.

#### Follow mode

With **Agent > Follow agent** on, the editor opens the system of each agent edit and moves the camera to its item. The selection then follows Unity. A create or a copy selects the new item, a delete clears the selection, and a rename keeps it. An undo of an agent delete selects the item again. An item without a place in the Scene, for example a loadout, shows in the Inspector, and the view does not change.

When you move the camera of the Scene or select an item, follow mode stops. Click **Follow agent** to start it again. The settings keep the last value that you clicked, so follow mode is on again at the next start of the editor. With follow mode off, the agent changes the view and the selection only with `open_view` and `select_items`. When an agent edit removes the selected item, the selection goes, as after an undo.

#### Pause

**Agent > Pause agent edits** stops each agent edit until you pick **Resume agent edits**. A click on the agent segment of the status bar opens the same menu. While you pause, each tool that changes data, writes files or starts a play gives the error `paused`. The read tools, the view tools, `publish_checklist` and `play_stop` still work. The pause stays when you stop and start the server again.

## Permission rules for the agent program

An agent program can ask you before each tool call. A permission rule of the agent program can allow a set of tools without a question. Claude Code gives each tool of the server `openlancer` the name `mcp__openlancer__<tool>`.

The name of a read tool starts with `list_`, `get_`, `find_`, `search_`, `describe_` or `check`, or is `studio_status`, `mission_dry_run` or `publish_checklist`. So these rules allow all read tools:

```
mcp__openlancer__list_*
mcp__openlancer__get_*
mcp__openlancer__find_*
mcp__openlancer__search_*
mcp__openlancer__describe_*
mcp__openlancer__check
mcp__openlancer__studio_status
mcp__openlancer__mission_dry_run
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
| `--no-save` | Leaves out `save` and `export_mod`. The edits stay unsaved. The recovery file still keeps them. |
| `--restore-recovery` | Restores the unsaved edits of an earlier session from the recovery file at the start. |
| `--discard-recovery` | Deletes the unsaved edits of an earlier session at the start. |

The oltool host never edits a mod in the OpenLancer repo, because it cannot ask you. It opens such a mod only to read, and the edit tools give `read_only`. A profile or an active mod in the game folder also opens only to read.

## The audit log

The editor writes each call of an agent into an audit log: one file for each run of the editor, for example `%LOCALAPPDATA%\OpenLancer\logs\agent-20261020-142233.jsonl`. Each line holds the time, the tool, the arguments and a summary of the result. The log holds no token. At 64 MB, the log stops, and the Console tells you.

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
