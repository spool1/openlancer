# The agent server (MCP)

OpenLancer has an agent server. An agent program, for example Claude Code, connects to it and reads the data of a profile. The server uses the Model Context Protocol (MCP), so each agent program with MCP support can use it.

The agent reads the data as the editor shows it. It reads the systems and their items, the data lists, the assets, the missions and the cutscenes. It also reads the raw INI text and the problems of the checks. In the editor, the agent can also open views and select items. It gets images of a system, a cutscene, a panel and the running game.

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
| Follow agent | Opens and frames the item of each agent edit. It is on by default. It stops when you move the camera of the Scene or the graph of the Mission tab, or select an item. |
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
| Agent tool groups | All groups | The agent can use the tools of the selected groups: Scene, Missions, Cutscenes and Play. The `scene_` tools, the `prop_group_` tools and `render_scene` are in the group Scene. The `cutscene_` tools, `list_cutscenes`, `get_cutscene` and `render_cutscene` are in the group Cutscenes. `play`, `play_stop`, `mission_play` and `play_capture` are in the group Play. The other `mission_` tools and the read tools of the missions are in the group Missions. `mission_play` plays a mission script, so it also refuses when you clear the group Missions. These read tools are always available: `studio_status`, `get_item`, `find_references`, `list_values`, `get_raw_ini`, `check` and `get_console`. The edit tools of "The edit tools" are always available too, and so are `save`, `export_mod`, `publish_checklist`, `create_loadout`, `create_from_template`, `open_view`, `select_items` and `capture_panel`. A tool of a cleared group tells the agent that you cleared the group. An edit tool also refuses an item of a cleared group, for example an object of a system when you clear the group Scene, or a cutscene event when you clear the group Cutscenes. |
| Rows of a page without a limit | 50 | The rows of a list result when the agent gives no limit. |
| Largest page limit | 200 | The largest limit that the agent can ask for in one list result. |
| Size of a result (KB) | 256 | The most text of one result. A larger result asks the agent for pages. An edit with a larger result changes nothing. |
| Calls each second | 10 | The most calls that the server takes in one second. A read of the catalog of the prefabs counts as a call. |
| Wait for the editor (s) | 30 | How long a call waits while you edit a field, rename an item or drag an item, and while a dialog is open. A call also waits while the editor loads the game data. A longer wait gives the error `busy`. A read of the catalog of the prefabs has the same wait. |
| Time of a check or an image (s) | 60 | How long a check of a retail target or an image can take. An image of the game (`play_capture`) can take 60 s more for its `delay`. |
| Size of a request (MB) | 4 | The largest request. The change applies at the next start of the server. |
| Largest side of an image (pixels) | 2048 | The most pixels on each side of an image. A larger panel capture or image of the game becomes smaller. A `render_scene`, `render_cutscene` or `play_capture` call with a larger `width` or `height` gives the error `limit`. |
| Items of one edit | 200 | The most items that one call of `delete_items`, `duplicate_items` or `prop_group_save` names. Also the most moves of one `scene_transform` call, the most events of one `cutscene_move_events` call and the most rows of `expect_events`. |
| Sections of one edit | 2000 | The most sections that one edit call changes, adds or removes. Also the most keys of `fields` of cutscene items that one call sets, in all steps of `edit_batch`. A larger edit changes nothing and gives the error `limit`. |
| Steps of one batch | 100 | The most steps of one `edit_batch` call, and the most commands of one `mission_edit` call. |
| Legs of one route | 50 | The most legs of one `scene_place_patrol_route` call. Each leg is one zone. A retail route has at most 12 legs. |
| Time of an export (min) | 10 | How long one `export_mod` call can take. A longer export gives the error `limit`. A write of the packages that started goes on to its end. |

## The tools

`oltool mcp tools` prints the name, the title and the description of each tool. `oltool mcp tools --json` prints the tool list of the protocol with the schemas.

### The read tools

These tools work in both hosts. They change nothing.

| Tool | What it gives |
|---|---|
| `studio_status` | The profile, the active mod, the revision, the tool groups, the limits and the state of the game data (`data_load`). The agent calls it first. See "Save". |
| `list_systems`, `get_system` | The star systems, and one system with its counts, bases, connections and problems. `get_system` also gives the place on the nav map, the zoom of the system map and the jump objects of the system. |
| `get_universe`, `get_route` | The systems with their jump connections, and the shortest routes from one system. See "The universe tools". |
| `list_items`, `get_item` | The items of a system, and one item with its fields, their origins and their problems. A cutscene entity or event gives its fields by their THN paths. An object row of `list_items` and a connection row of `get_system` give the `solar_radius` of the archetype. |
| `find_references` | Each line and each file that names an item, also in the mission scripts. |
| `list_values` | The values that a field can take, from one data list. |
| `search_assets`, `list_prop_groups` | The assets of the Assets panel, and the prop groups of the mods. |
| `list_missions`, `get_mission`, `get_trigger`, `describe_mission_name` | The mission scripts, one script, one trigger, and the plain name of a condition or an action. |
| `mission_dry_run` | A dry run of one mission script, without a game world. The trigger rules of the game run step by step on the inputs that the agent gives. The result gives each step and the state after the last step. The section "The dry run" of `docs/MISSIONS.md` gives the rules. |
| `mission_start_plan` | The plan of a play of one mission script from a start, as the Play start page shows it. A plan that cannot start gives the status `refused` and the reason. See "The start plan". |
| `list_cutscenes`, `get_cutscene` | The cutscene scripts, and one script with its entities, its events and their fields, in pages. |
| `get_raw_ini` | The INI text of a section or a file, with the problems of each line. |
| `check` | The problems of the checks of the editor, in pages. |

### The tools of the editor

These tools work only in the editor host.

- `get_console` gives the entries of the Console.
- `open_view` opens a system in the Scene, a mission in the Mission tab or a cutscene in the Cutscene tab.
- `select_items` selects an item, as a click does. By default, it also opens the view of the item and moves the view to it. With `frame: false`, it opens no view and does not move the camera.
- `render_scene` gives an image of a system from a camera that the agent chooses. The camera of the Scene does not move, and the Scene keeps its system. The result also gives the place of each item in the image.
- `render_cutscene` gives an image of a cutscene at a time. The preview in the Cutscene tab does not change. A space script shows without its system. A script of the image with a path text that is not only a path does not go to the engine. The call then gives `refused` with the detail `NoPreview`, and the message names the entity.
- `capture_panel` gives an image of a panel as you see it, with its marks and labels.
- `play` and `play_stop` start and stop Play in OpenLancer. See "Play".
- `mission_play` plays a mission script from a start in OpenLancer, and `mission_play_trace` gives the trigger changes of that play. See "Mission play".
- `play_capture` gives an image of the running game of Play in OpenLancer. See "Images of the game".

`open_view` and `select_items` change what the editor shows. They do not change the data. The image tools change nothing.

### Images

An image has at most 2048 pixels on each side. A larger panel or game window gives a smaller image. A `render_scene`, `render_cutscene` or `play_capture` call with a larger `width` or `height` gives the error `limit`. **Agent > Agent settings > Advanced** can set a smaller limit. The editor does not draw a frame while it renders an image. An image of a system that the Scene does not show also loads that system first.

The agent program can send each image to its model service. A capture of a panel can show the names of your folders.

To measure the images, start the editor with `OpenLancer.Editor <profile.json> --bench-agent-render <system>`. Give the nickname of a system of the profile. The bench opens the profile without an active mod and opens the system. It renders one image of 1024 by 640 to warm up. Then it renders one image from each of 10 directions, as `render_scene` does.

For each image, the bench writes two times to the engine log of the editor. The first is the time on the UI thread. The second is the time until the PNG is ready. The time on the UI thread includes the work of the GPU. Then the bench closes. The gate is a median under 100 ms. A median above the gate, or an image that fails, gives the exit code 1. The bench needs no agent server and does not change the editor settings.

### The edit tools

These tools change the data of the active mod. They work in both hosts. The oltool host with `--read-only` does not list them.

| Tool | What it does |
|---|---|
| `set_fields` | Sets fields of one item by their INI keys, or of a cutscene entity or event by their THN paths. |
| `set_text` | Sets the name, the infocard or another text of one item, or a text id of a mission line. The text goes into the `strings.json` of the active mod. It does not change cutscene items. Use `set_fields` for their texts. |
| `rename_item` | Gives an item a new nickname, and changes each line that names it. A line that the active mod cannot change keeps the old nickname and shows "Missing". A data item and a system must be items that the active mod adds. A rename of a ship or equipment also changes the templates of the cutscene COMPOUNDs that name it. A cutscene entity gets a new name, and the events of its script that name it change too. |
| `delete_items` | Deletes items of a system with their children, mission items, and cutscene entities and events. Other lines that name them show "Missing". A cutscene entity goes with the events that name it. A system that the active mod adds goes with its file. |
| `duplicate_items` | Copies items of a system with their children, mission items and cutscene events. With `offset`, the copies of a system move by the offset. A copy without a position, a mission copy and a copy of a cutscene event do not move, and the result warns about them. With `time_offset`, each copy of an event is at the time of its event plus the offset. |
| `undo` | Undoes the last edit of the agent. |
| `redo` | Redoes the last edit that the agent undid. |
| `edit_batch` | Runs edit steps as one Undo step, all or nothing. |

Each edit is one Undo step. The Edit menu shows its label with "Agent: " before it, for example "Undo Agent: Delete my_station_1". You can undo and redo each agent edit, as each of your own edits. `undo` undoes only an agent edit at the top of the history. `redo` redoes only an agent edit that the agent undid. So the agent never undoes your edit, and never redoes an edit that you undid with Ctrl+Z. All agent programs of one editor have the same edit origin. So `undo` of one agent program can undo the last edit of another agent program.

The five tools `set_fields`, `set_text`, `rename_item`, `delete_items` and `duplicate_items` also change mission items. "The generic tools on mission items" tells which references each tool takes. Four of them also change cutscene items, as "The generic tools on cutscene items" tells. The other edit tools change only items of INI files that are not mission files. A reference of a mission item gives them `invalid_argument` with the detail `MissionItem`. A reference of a cutscene item gives them `invalid_argument` with the detail `CutsceneItem`, and the message names the tools that change cutscenes.

A rename of an object of a system also changes the mission lines that name it, as in the editor. The question of the rename names these mission scripts. Of the items of a system, `delete_items` takes objects, zones, lights, nebulae and asteroid fields. A jump gate or a jump hole goes with its pair, its traffic zones and its gate lock lines. The delete needs `confirm: true` when the other end is in another file. A one-way object whose `goto` line names an end gives `refused`, because it is no part of the pair. Name it in the same call, or delete it first.

`delete_items` also takes a system that the active mod adds, by its `system:` reference. The delete always needs `confirm: true`. The system file goes too, and each object of another system whose `goto` line names the system goes with its pair. The mission lines that name the system show "Missing". A system with a base gives `refused` in this version. A system of the game or of a lower mod gives `refused` with the detail `GameSystem`. A call can also name items of the system. These items go with the system.

`duplicate_items` takes objects, zones and lights of a system. A jump gate, a jump hole, an airlock gate and a trade lane ring give `refused`, because a copy has no partner. `rename_item` takes an item of a system, and a ship, equipment or a good that the active mod adds. It also takes a system that the active mod adds. The rename of a system changes the `goto` lines, the `[Base]` lines and the mission lines that name it, and its file keeps its path. Another section, for example a loadout, gives `refused`.

A rename of a ship or equipment also changes the template of each cutscene COMPOUND that names it, as the data API does. A rename of a good also renames its equipment, so the COMPOUNDs of that equipment change too. The result gives these entities in `changes` as `thn:` references. Such a change alone needs no `confirm: true`. When the rename asks for another reason, the question also names their cutscenes. A cutscene that the editor cannot change keeps the old name, and the rename asks with a warning.

#### Values of set_fields

`set_fields` takes the INI key of each field, as `get_item` gives it:

- A text, a number or `true` or `false` is one line with one value, for example `"loadout": "my_loadout"`.
- An array of values is one line, for example `"pos": [100, 0, -500]`.
- An array of arrays gives one line for each array, for example `"cargo": [["my_good", 5], ["my_other_good", 1]]`.
- `null` or `[]` removes the key. The engine then uses its default.

A value never holds a comma. The key line, a key of linked lines, the `file` of a system and the `goto` line of an object are locked, and `get_item` marks them. The `goto` line links the two ends of a jump connection, so it gives `refused` with the detail `FieldNotWritable`. Use `rename_item` for the nickname. Use `scene_set_encounters` for the `encounter`, `faction` and `faction_weight` lines of a zone. A value that a list check refuses still applies, as in the Inspector. The result gives the problem in `new_problems`.

A mission item takes the field forms of `mission_add_item` in place of these forms, and it runs in strict mode. See "The generic tools on mission items". A cutscene item takes THN paths and the value forms of "The generic tools on cutscene items".

#### Preview, confirmation and revision

Each edit tool takes these arguments:

| Argument | What it does |
|---|---|
| `preview` | Runs the edit and gives its result, then changes nothing. The revision, the history and the unsaved state stay the same. The views do not change. |
| `with_patch` | With `preview`: also gives the text that a save writes for each INI file and each cutscene that the edit changes. This is the patch, or the whole file when the mod has one. |
| `if_revision` | Refuses the edit with `stale_revision` when the edit view changed after this revision. Each result gives the revision as `rev`. A save does not change it. |
| `confirm` | `rename_item`, `delete_items` and `mission_edit`: applies an edit that changes more than its targets. The prop group writes: writes the files. |

When you would see a dialog for a delete or a rename, the tool gives `needs_confirmation`, and nothing changes. A delete or a rename that leaves "Missing" lines also needs it. The server tells the agent to show the changes to you and to ask you first. When you agree, the agent calls the tool again with `confirm: true`. A preview tells it in `needs_confirmation`, and the warnings of the preview tell why.

The result of an edit gives the new items (`adds`), the changed items (`changes`), the removed items (`deletes`) and the renamed items (`renames`). It also gives the "Missing" lines, the warnings, the text mentions, and the problems that the edit adds and removes. Each reference names the item as it is after the call. So an item that a call adds and then deletes is not in the result, and two renames of one item are one rename. A call that changes nothing gives the status `no_change`, an empty `changes` list and no Undo step.

`select` names the item that the call selects last, with its last reference. So a create and a rename of the new item in one call give the new nickname. A call that deletes that item gives no `select`, as Unity selects nothing after a delete of the selected item. Each step of `edit_batch` gives the `select` of its own commands.

#### Batches

`edit_batch` takes a list of steps. Each step names an edit tool and its arguments. All steps are one Undo step with the label of the batch. Each step sees the result of the steps before it, so a later step can name an item that an earlier step made or renamed.

The result also gives the result of each step, with the revision and the files of the batch. A step can delete items, so `edit_batch` has the destructive hint of the protocol, as `delete_items` has.

When one step fails, the error `step_failed` gives its index and its error, and the batch changes nothing. A step that needs a confirmation fails unless its arguments have `confirm: true`. `undo`, `redo`, `edit_batch`, the read tools and the tools of the editor cannot be steps. `prop_group_save`, `prop_group_rename` and `prop_group_delete` cannot be steps either, because they write files at once.

#### Limits and pauses

One call names at most 200 items or cutscene events, changes at most 2000 sections and sets at most 2000 fields of cutscene items. One batch has at most 100 steps, one `mission_edit` call has at most 100 commands, and one route has at most 50 legs. **Agent > Agent settings > Advanced** changes these limits. One call also runs at most 1000 mission commands, and no setting changes this budget (see "The mission tools"). A call above a limit gives the error `limit` and changes nothing. The size limit of a result applies to an edit too. The edit checks the size of its result before it applies, so a larger edit also changes nothing.

While you pause the agent edits, each tool that changes data, writes files or starts a play gives the error `paused`. Examples are the edit tools, `undo`, `redo`, `save`, `export_mod`, `play` and `mission_play`. The read tools still work, also `publish_checklist`, `mission_play_trace` and `play_capture`. `play_stop` also works. It stops each game of Play, also a game that you started. Pick **Agent > Pause agent edits** to pause the agent edits, and **Resume agent edits** to let the agent edit again.

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

A station archetype with a docking sphere needs a base, because the Create menu asks for one. A station archetype without a docking sphere takes no base. A `parent` must be an object of the same system. An archetype of another template gives `invalid_value` with the reason. A jump gate, a jump hole, an airlock gate and a trade lane ring need a partner or a lane. So `scene_create_object` and `scene_spawn_asset` refuse their archetypes. Use `connection_create` for a jump gate pair or a jump hole pair.

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
10. If the loadout of the wreck has an error, call `undo` before the next edit. Only `undo` changes the loadout of a placed wreck, and it reverts the last edit of the agent.
11. If you called `undo`, call `scene_place_wreck` again with the correct rows.
12. Correct each other error with `set_fields`, for example an error of the patrol.
13. Save the mod in the editor.

### The universe tools

These tools make new star systems and jump connections, and read the jump graph of the edit view. They work in both hosts. Each edit tool is one Undo step, also when it changes two system files, the gate locks and the text of the mod. Each edit tool can be a step of `edit_batch`. A later step can name a system or an object that an earlier step made.

| Tool | What it does |
|---|---|
| `system_create` | Makes a new star system of the active mod like a template system. It adds the `[system]` to `universe.ini`, and a system file with a sun and its death zone. |
| `connection_create` | Makes a jump gate pair or a jump hole pair between two systems, or a jump hole pair inside one system. |
| `connection_set_lock` | Sets the lock of a jump gate or a jump hole at the start of a new game, and of its partner. |
| `get_universe` | Gives the systems with their places on the nav map and their jump objects, and their jump connections. |
| `get_route` | Gives the shortest routes from one system, as the route tables of the retail games give them. |

`system_create` puts the new file into the folder of the active mod, as "New systems" of `docs/MOD_FORMAT.md` tells. The nickname has 2 to 15 letters, digits or `_`, and starts with a letter. `map_pos` takes two whole numbers from 0 to 15. Each value that names other data, for example `local_faction` or the archetype of `sun`, must be an entry of its list.

The result gives `system`, `file` and `sun`. It also warns that no jump gate or jump hole leads to the new system yet. In the editor, the Scene opens the new system only after a save and the reload of the game data, and the result tells it. The refusals are:

| Detail | Error | Cause |
|---|---|---|
| `NameTaken` | `name_taken` | Another system has the nickname or its FLHash. |
| `PathInUse` | `invalid_value` | The game data, a lower mod or the active mod has the file of the nickname. |
| `MapPosRange` | `invalid_value` | A place outside 0 to 15. |
| `LikeNoFile` | `invalid_value` | The template system has no file. |
| `LikeNoSun` | `invalid_value` | The template system has no sun, and `sun` gives no archetype and star. |
| `SunType` | `invalid_value` | The archetype of `sun` is not of the type `SUN`. |

`connection_create` takes `kind`, `from` and `to`. Each end names its system. Without `object`, the end is a new object. With `object`, the end is an existing jump gate or jump hole of the system without a partner. The tool then writes only its `goto` line. The other members of such an end give `invalid_argument`.

A new end gets its lines, its nickname, its name, its position and its traffic zone as "Jump connections and gate locks" of `docs/MOD_FORMAT.md` tells. The result gives `pair`, `traffic_zones`, the `loadout` and the `pilot` of each end, `routes` and `jump_objects`. `routes` counts the routes of the table of all connections that the edit adds, makes shorter, makes longer and removes. The refusals are:

| Detail | Error | Cause |
|---|---|---|
| `JumpLimit` | `refused` | A system would have more than 10 jump objects. A hole pair inside one system counts 2. |
| `GateSameSystem` | `invalid_value` | A jump gate pair inside one system. |
| `KindMismatch` | `invalid_value` | The archetype or the existing end is of another kind. |
| `EndPaired` | `invalid_value` | The existing end has a partner that goes back to it. |
| `NameTaken` | `name_taken` | Another object has the nickname of a new end. |
| `EndOtherSystem` | `invalid_argument` | An `obj:` reference of an end names an object of another system. |

The `field` of each error names the argument, for example `to.object`. The message is the text that the editor shows for the same refusal.

`connection_set_lock` sets `locked` to `none`, `player`, `npc` or `both`. With `pair`, the default, the partner gets the same lock when it goes back to the object. A gate of the game or of a lower mod works too, because the patch of the gate lock file changes. A player lock gives a warning: the route tables leave out the jump gate or the jump hole, also after a mission unlocks it. A second call with the same lock gives `no_change`. An object that is no jump gate and no jump hole gives `invalid_value` with the detail `NotConnection`.

`get_universe` gives one page of systems in the order of `universe.ini`, and the connections of these systems in the order of each system file. With `system`, it gives one system and its connections. Each connection gives its kind, the object that it goes to, if that object goes back (`pair_ok`), and its lock. `route_files_change` is true when the route tables of the mods differ from the route tables of the game without mods. Ctrl+F5 and an export for a retail game then write the route files, as "Route tables" of `docs/MOD_FORMAT.md` tells.

`get_route` gives the shortest route from `from` to each system that the table reaches, and the systems that it does not reach. `kind` picks the table: `all`, `gates` or `holes`. The tables leave out each jump gate and each jump hole that is locked for the player. OpenLancer Play computes its own routes, so these tables show what the retail games read.

In `edit_batch`, a step of these edit tools gives the result of its edit, without `system`, `pair` and the other parts above. The `adds` of the step name the new items.

### The mission tools

The mission tools change the mission scripts of the active mod with the commands of the mission studio. They work in both hosts. `mission_edit` runs any commands of the mission studio. The trigger tools of "The trigger tools" add and change triggers and their lines with named arguments. `mission_add_item` adds a ship, an NPC or another item of a mission, as "The item tool" tells. `mission_place_prefab` places a behavior prefab, as "The prefab tool" tells. The generic edit tools rename, delete, copy and change mission items, as "The generic tools on mission items" tells. In the editor, `mission_play` plays a mission from a start, as "Mission play" tells.

`mission_edit` runs a list of commands. Each call is one Undo step with all its commands, as each other edit tool. It takes `preview`, `with_patch`, `if_revision` and `confirm`, as the edit tools do. The result names each item as it is after all commands of the call, as "The edit tools" tells. So a trigger that one call adds and then removes gives no add, no delete and no `select`.

| Argument | What it does |
|---|---|
| `commands` | The mission commands in order. Each command is an object with a `kind`, for example `add_trigger`, `set_condition_value` or `remove_trigger`. The input schema of the tool gives each kind with its fields, and a description of each kind and of each field. A field that is true when the command does not give it has `default: true`, for example `change_references` of `rename_item` and `rename_trigger`. So a rename changes each line that names the item, unless the command gives `change_references: false`. |
| `label` | The label of the Undo step. Without it, the call takes the label of its one command, or "Mission edit". |
| `options` | `reject_new_warnings: true` also refuses an edit that adds a warning. Then a new warning gives the error `new_errors` with the detail `NewWarnings`. A new error gives the detail `NewErrors`. |
| `expect_versions` | The versions of the scripts that the agent read, by install path. When a script has another version now, the call gives `stale_revision` with the detail `ScriptChanged`. |

The input schema gives each command with its help and the value names of each key of a line. So it is large: about 230 KB of JSON. An agent program can send the whole tool list to its model, and then the schema fills a part of the context of the model.

A line of a trigger is an object with its key and its values by name, for example `{"key": "Act_ActTrig", "values": {"trigger": "my_next"}}`. `describe_mission_name` gives the value names of each condition and action. A line can also give its value texts by position in `texts`.

Each command sees the result of the commands before it. So a later command can name a trigger that an earlier command added. When one command refuses, the call changes nothing, and the error names the command, for example `commands[2]`. An error of one field of a command names the field too, for example `commands[0].ship_build_file`. A path of a reference must be an install path. A new script must be under `DATA\MISSIONS`. The `ship_build_file` of `create_script` is an install path in a folder under `DATA\MISSIONS` too, for example `DATA\MISSIONS\MY\npcships.ini`. The result gives the `msn:` reference of a new ship build file in `adds`. `delete_script` takes it, but it is not a script, so read its builds with `get_item`.

`new_ship_build_file` gives a script that exists a new empty ship build file, as "New file..." of the field "Ship build file" in the editor does. The file goes into the folder of the script, for example `DATA\MISSIONS\MY\npcships_2.ini`, and the `npc_ship_file` line of the script names it. The command and the editor use the same check, with the same text.

A name has only letters, digits, `_` and `-`, with at most 64 characters. A name that Windows uses for a device, for example `nul` or `com1`, is not correct in any letter case. A bad name gives `invalid_value` with the detail `NameInvalid`, as the other names of the mission commands do. A path with a file gives `name_taken` with the detail `PathInUse`. A script directly in `DATA\MISSIONS` cannot get its own file, so the command gives `BadPath`.

The builds of the old file do not move. The game looks for the build of a mission ship in the ship build file of its script first. Then it looks in the shared files `DATA\MISSIONS\npcships.ini` and `DATA\MISSIONS\npcships_test.ini`. It does not spawn a ship whose build it does not find.

So each batch that points the line at another file gives a warning, also with `set_item_key`. The warning names the builds that are only in the old file, and the two files. The mission checks do not find this problem, so strict mode does not refuse the batch.

The commands do not change the world files of the missions, for example the pilot files and the encounter files, because encounters and bases use them too. The `npc_ship_file` line of a script can name only a ship build file or a new file. A ship build file is in a folder under `DATA\MISSIONS` outside the encounters folder, and it has only `[NPCShipArch]` sections. The shared ship build file `DATA\MISSIONS\npcships.ini` is also correct. A command that sets the line to another file gives `invalid_value` with the detail `NotShipBuildFile`, and nothing changes. A command in a `command_group` gets the same check.

A command that puts a section other than `[NPCShipArch]` into the ship build file of a script gives `NotShipBuildFile` too. An example is `add_section` with the type `Room`. A new script cannot take the path of a ship build file that a script names. So the ship build file stays a mission file, and the inverse of each edit of it works. A file with a section that is not a ship build is a world file, also when a script names it, so the commands give `WorldFile` for it.

The commands read the mission files again after each command, also in a `command_group`. A command can point the `npc_ship_file` line of a script at another file. Then the old ship build file is a world file for the later commands of the call, unless another script names it. So a later command that changes the old file gives `WorldFile`, and nothing changes. Change the old file first, and then point the line at the other file. The inverse of such a call points the line back first, so it can change the old file again.

The mission tools always run in strict mode. An edit that adds an error to a script gives the error `new_errors` with the first new error, and nothing changes. `options` with `reject_new_errors: false` gives `invalid_argument`. A line that a delete or a rename leaves "Missing" does not count as a new error. But the delete or the rename needs `confirm: true`, as in "Preview, confirmation and revision". The other changes that the mission studio asks about need it too. An example is a change of a build in the shared ship build file of the missions.

The mission checks of a script also check each ship build of its ship build file. The ship archetype, the loadout and the pilot of a build must be in the game data, else the game drops a mission ship with the build. The state graph must be one of the words FIGHTER, TRANSPORT, GUNBOAT, CRUISER, NOTHING or PLAYER. `get_item` gives the names of each line of a ship build with its problems.

Strict mode compares the problems of the mission checks before and after the edit. The save gate also has other rules. For example, in a mod that names a retail target, the loadout of a ship build must have an engine. `check` with the scope `mission` does not give the errors of these rules. `check` with the scope `all` gives each error that stops a save, so call it before `save`.

The result gives `inverse`: a mission command that reverts the call. To revert the call after later edits of other items, give the inverse in `commands` of a new `mission_edit` call. The inverse checks each section that the call changed or added. When a later edit changed such a section, the inverse gives `stale_revision` with the detail `SectionChanged`, and nothing changes. `undo` reverts only the last edit of the agent. `row_moves` gives the lines whose place in their trigger changed. Each line comes once, with its row before the call and its row after the call. Each command that adds, removes or moves a line of a trigger adds its moves, for example `set_trigger_start` and `link_triggers` with `before`. `set_trigger_start` and `set_trigger_repeats` remove the old state line and add a new one at the place of a state line, so each line between the two rows moves by one row. A line that the call changes keeps its row. A new line has no row before the call, so it is not in `row_moves`. So an agent can map each `line:` reference that it read before the call to its new row.

Each script has a version. It starts at 1 and grows at each edit, undo and redo of the script. The result of each edit call gives the versions of the scripts that it changed in `versions`. A rename of an object that changes the mission lines that name it gives them too. `list_missions` gives the version and the state of each script. The state `replaced` means that a whole file of a mod gives the script. A new script of the active mod is a whole file, so it has the state `replaced` too, also when the base game has no such script.

`mission_edit` can be a step of `edit_batch`. The strict check of the missions runs once, at the end of the batch. So a step can start a trigger that a later step adds. Each mission step gives its own `inverse` and `row_moves` in `steps`, and the batch gives no inverse. The `row_moves` of a step count from the rows before that step. One undo reverts the whole batch.

One call has at most 100 commands. A `command_group` counts as one command of the 100, and each step of `edit_batch` has its own limit too. One call also runs at most 1000 mission commands in all. This budget counts each command, and each command in a `command_group` at each level. It also counts each command of each mission step of `edit_batch`, and the commands that the trigger tools and the generic tools make. The budget is not a setting, so the editor does not stop for a long time on one call.

A call above the budget gives `limit` with the detail `MissionBudget` and the field `commands`, and nothing changes. In `edit_batch`, the field names the step where the count goes above the budget, for example `steps[10].arguments.commands`. The inverses that the result gives must fit the budget too. A large delete can have a large inverse. The count includes the `command_group` that joins the inverses of a call or a step with more than one mission request. An example is `mission_add_item` for a ship with more keys. When the inverses have more than 1000 commands, the call gives `limit` with the detail `InverseBudget`, and nothing changes.

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
- A list of numbers or texts for a position, a rotation or the values of one line. For example, `"position": [0, 0, 1000]` or `"npc_class": ["lawful", "d5"]`. A position or a rotation also takes the text with commas that `get_trigger` and `get_item` give, for example `"position": "0, 0, 1000"`. So an agent can read a line, change one value and write the values back.
- An object of values by name, for a key with more than one value. For example, `"rel_pos": {"heading": 90, "anchor": "my_station", "range": 500}`. `describe_mission_name` does not give the names of the keys of a section. Read the item with `get_item` to see them.
- `{"texts": [...]}`: the value texts by position. The editor checks only the form of the line. For example, `"type": {"texts": ["navmarker", "my_system", "12345", "12346", "0", "0", "0"]}` for an objective. An empty list is a line without values: the editor gives each default value, or refuses a value that the key needs.

The first value of the `type` line of an objective is its form: `ids`, `navmarker`, `rep_inst` or `base`. The form sets the names of the values after it, for example `{"form": "navmarker", "system": "my_system", "text_id": 1, "explanation_id": 2, "position": [0, 0, 0]}`. The resource `openlancer://schema/mission-sections` gives the values of each form in `forms`. To give an objective its own text, add the objective and call `set_text` with the field `type` in one `edit_batch`. An objective needs a type, so a call that adds an objective without a type gives `new_errors`. For example:

```json
{ "steps": [
  { "tool": "mission_add_item", "arguments": { "script": "msn:DATA\\MISSIONS\\MY\\my.ini", "kind": "objective", "nickname": "my_goal", "fields": { "state": "HIDDEN" } } },
  { "tool": "set_text", "arguments": { "ref": "sec:DATA\\MISSIONS\\MY\\my.ini#NNObjective/my_goal", "field": "type", "text": "Destroy the pirate ambusher." } } ] }
```
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

Another file under `DATA\MISSIONS` that is not a script and not the ship build file of a script is a world file, for example a pilot file or `formations.ini`. The generic tools take a section of a world file through the commands of the Inspector, so they give the same result as the Inspector. `set_fields`, `set_text`, `rename_item`, `delete_items` and `duplicate_items` take its `sec:` or `list:` reference, for example `list:pilot/my_pilot`. The editor reads the pilot files only for its lists now, so `set_fields` gives `refused` with the detail `ReadOnlySection`, as the Inspector shows these fields read-only. A `line:` or `trg:` reference of a world file gives `refused` with the detail `WorldFile`. `mission_edit` gives `WorldFile` with the same text for each section of a world file, because the mission commands change only the mission files. A shared ship build file, for example `DATA\MISSIONS\npcships.ini`, gives `refused` with the detail `SharedBuildChange` in each tool except `mission_edit`. `mission_edit` changes the lines of a build of this file after a confirmation: `set_item_key` and `set_line` give `needs_confirmation`. Random encounters can use the build, so the agent asks you first. Then it calls `mission_edit` again with `confirm: true`. The mission commands do not rename, delete or copy a build of this file.

### The cutscene tools

The cutscene tools change the cutscene scripts of the active mod with the commands of the Cutscene tab. They work in both hosts and are in the group Cutscenes. Each call is one Undo step. Each tool takes `preview`, `with_patch` and `if_revision`, and it can be a step of `edit_batch`.

| Tool | What it does |
|---|---|
| `cutscene_create` | Makes a new cutscene script, as **New cutscene** does. The script has a scene, the monitor `Monitor`, the camera `Camera_01` and a cut to the camera at 0. `name` is the file name without `.thn`. `duration` is the length in seconds, and the default is 10. |
| `cutscene_add_entity` | Adds an entity to a script, with the records that the game needs for its type. `type` is the entity type, `name` the name of the entity, and `template` its template. `position` is in meters, and `orientation` is three angles in degrees, as the Inspector shows them. `fields` gives more values. |
| `cutscene_add_event` | Adds an event to a script, as **Add event** of a Timeline row does. `type` is the event type, `time` the time in seconds, and `targets` the names of the target entities. `length` is the length in seconds. `fields` gives more values. |
| `cutscene_move_events` | Moves events of one cutscene in time and changes their length, as a drag on the Timeline does. `time_delta` and `length_delta` give the seconds to add. An event without a length has the length 0. |
| `cutscene_set_duration` | Sets the length of a script in seconds, as the duration field of the Inspector does. |
| `cutscene_set_flags` | Sets the flags of one entity, as the flag boxes of the Inspector do. The flags are REFERENCE, LIT_DYNAMIC, LIT_AMBIENT and HIDDEN. A SOUND has SPATIAL in place of LIT_DYNAMIC. The flags that the list does not name go off. |

A new cutscene goes into the folder `DATA\MODS\<mod id>\CUTSCENES` of the active mod, and a save writes it as a whole file. A name can have letters, digits, `_` and `-`, and at most 64 characters, as in the name field of **New cutscene**. The tool refuses a name that Windows uses for a device, for example `nul` or `com1`, in any letter case. Another name gives `refused` with the detail `NameInvalid`, and the message gives the rule. A name of a cutscene that exists gives `name_taken`, also in other letter case.

Each argument takes one kind of `thn:` reference. `cutscene` takes a script, `entity` takes an entity, and `events` takes events. Another reference gives `invalid_argument`. All events of one call must be in one cutscene. One call names at most 200 events. `targets` of `cutscene_add_event` takes entity names, not references. A reference in `targets` gives `not_found`, because no entity has that name. The message then tells that `targets` takes names.

A cutscene that the editor cannot change gives `refused` with the reason. An example is a cutscene whose THN patch in the active mod has an error.

#### New entities and events

`cutscene_add_entity` takes the entity types of the retail scripts: CAMERA, MARKER, DEFORMABLE, COMPOUND, LIGHT, SOUND, PSYS, MOTION_PATH, MONITOR and SCENE. A new entity gets the records that the game needs for its type:

- Each type except MONITOR and SCENE gets `spatialprops` with the position and the rotation. The default is the origin without a turn. The game does not put a MONITOR or a SCENE at a place, so `position` and `orientation` give `invalid_argument` for them.
- A CAMERA gets the lens of the camera of a new cutscene.
- A LIGHT gets `lightprops` for a directional light that is on, as most retail lights are. Its color is white. Change `lightprops.type` and the other keys with `fields` or `set_fields`.
- A SCENE gets the ambient color 0, 0, 0. No retail script has two SCENE entities, so a second SCENE gives `refused`.
- A MOTION_PATH gets no path. Give the keys in `pathprops.path_data` with `fields`.

`template` is the costume of a DEFORMABLE, the sound of a SOUND or the effect of a PSYS. `list_values` with the list `costume` gives the costumes. A COMPOUND takes as its template the nickname of an item of its category. For example, it takes a solar archetype when `userprops.category` is `"Solar"`, and a ship when it is `"Spaceship"`. So a COMPOUND with a template needs `userprops.category` too. The tool does not write a default category, because the category depends on the template. A template with a character outside ASCII, or with the character U+0000, gives `invalid_value`.

The tool adds a DEFORMABLE without a template and without `userprops.Actor`, and a COMPOUND with a template and no category. The result gives the error in `new_problems`, and the save stops until you correct it. The same is true for a template that names no item of its data list. Such a template is not a refusal of the call.

A name of another entity of the script gives `name_taken`, also in other letter case, because the game finds an entity by its name without letter case. An empty name gives `refused` with the detail `NameInvalid`. A name with a character outside ASCII, or with the character U+0000, gives the same error.

`cutscene_add_event` takes the event types of the **Add event** menu. The resource `openlancer://catalog/thn-events` gives them with the entity types of their targets. Another type gives `invalid_value`, and the message gives the types and names the resource. Each target must be an entity of a type that fits its place, as the target list of the Inspector shows. For example, SET_CAMERA takes a MONITOR, then a CAMERA. START_MOTION takes a DEFORMABLE or a COMPOUND, so a CAMERA gives `refused`, and the message names the types that fit. Too many targets, or a missing second target that the type needs, give `invalid_argument`. A name that no entity of the script has gives `not_found` with the closest names.

The new event gets the usual values of its type, as **Add event** gives them. The time rounds to the 1 ms grid. `length` sets `props.duration` on the 1 ms grid. A SET_CAMERA event has no length, so `length` gives `invalid_value` there. Without `length`, the event has the length of its type, mostly 1 s.

`fields` of both tools takes the THN paths and the value forms of `set_fields`, with the same rules (see "The generic tools on cutscene items"). The tool sets them after it adds the item, in the same Undo step. An error names its field, for example `fields.props.duration`, and nothing changes. The result gives only the add of the new item.

#### Times, durations and flags

`cutscene_move_events` rounds each new time and length to the 1 ms grid, as the Timeline does. A move that rounds to the old values gives `no_change` with a warning that tells it. When the lower cutscene has the event and the event has no length there, a length of 0 removes the key `props.duration`. Else the event keeps the length 0. A SET_CAMERA event has no length, as the Inspector shows. So a `length_delta` other than 0 on a SET_CAMERA event gives `invalid_value`, and the message names the event. A time below 0 and a negative length give `invalid_value` too.

`cutscene_set_duration` writes the value as you give it, as the duration field does. A negative duration gives `invalid_value`. A flag that the entity type does not have gives `invalid_value` too. A flags value that the engine cannot read gives `refused` with the reason.

#### Event references and guards

An event reference is `thn:<install path>#event/<index>`. The index is the place of the event in the event list of the script, from 0. An add, a delete and a reorder of an event can change the index of the events after it. So each row of `events` can also give `expect_type` and `expect_time`: the type and the time that the agent read.

When the event at the index has another type or time, the call gives `stale_revision` with the detail `RowChanged`, and nothing changes. The message gives the type and the time of the event now. The agent then reads the cutscene again with `get_cutscene`. With the guards, a replay of the audit log on a copy of the profile changes the same events.

The result gives each new and changed item as it is after the call. So an event reference in `adds` or `changes` has the index after the call. A deleted event has its index before the call, because the script does not have the event after the call. A new script is one add, and `select` names the last new item. Each step of `edit_batch` gives its references as they are after that step.

The result also gives the problems of the cutscene checks that the call adds and removes, in `new_problems` and `fixed_problems`. The save stops for each such problem. The game crashes when it plays a cutscene with an error. The message of an error starts with "The game crashes when it plays this cutscene."

A template that names no item of the data lists is a problem too. An example is a costume or an effect that the game data does not have. The message of a template that does not crash the game tells what the scene does without the item. When the lists have the name but the game cannot use the item, the message tells why.

The check reads the data lists with the unsaved edits. So a change of the active mod in the game data that deletes, renames or breaks the item also gives the problem. The result of a call with such a change, for example an INI edit, gives the problems of each cutscene that the save gate checks. These are the cutscenes that the active mod changes. They are also the cutscenes of the edit view that use an item that such a change deletes, renames or breaks. An example is a retail cutscene that the call and the active mod do not change.

`check` with the scope `all` lists each problem of these cutscenes. `save` refuses the save and gives the text of the save gate of the editor. A call that changes only cutscenes gives no problem of another cutscene.

To find these cutscenes, the save gate reads an index of the templates and the clips that each script uses. The index runs each script, so it also finds a template that the Lua text of a script joins from parts, for example with `..`. The server builds the index when it opens the profile. Until the index is complete, the first call after such a change waits for it.

A problem of a cutscene that the active mod does not change has the field `note`. It is the note of the Problems section of the Cutscene page: a change of the active mod in the game data causes the problem, and you correct it with an undo of that change or with another item or clip. `check`, `get_cutscene` and the problems of a call result give the note. The error `save_blocked` adds it one time after the messages of the errors. A problem of a cutscene that the active mod changes, or that has unsaved edits, has no note.

`list_values` gives the names of the data lists of the templates:

| Template | Lists |
|---|---|
| DEFORMABLE | `costume` |
| COMPOUND | By the category: `solar_archetype`, `ship`, `petal_room`, `petal_cart`, `petal_prop`, `equipment`, or `asteroid` and `asteroid_mine` |
| PSYS | `effect` and `vis_effect` |

A clip of a START_MOTION event on a character that the game does not have is a problem too: the character does not move. An empty clip is such a problem, so give the clip in the `fields` of `cutscene_add_event`. The clip is the value of `props.animation`. Without this key, the event crashes the game. `save` refuses such a clip with the text of the save gate of the editor. A COMPOUND plays the scripts of its own model, so the check skips a clip on a COMPOUND.

Get the clip names from `list_values` with the list `animation`. The detail of each row is the file of the clip. The list has the clips of the `.anm` files that the `[Animations]` lines of bodyparts.ini name, with the files of the mods. The tools cannot change these lines. The active mod can remove a clip, for example with its own `.anm` file without the clip. `check` and `save` then give the problem of each cutscene that plays the clip, also a cutscene that the active mod does not change.

Each such problem gives the reference of its element. It gives the reference of the script when the problem is about the whole script. It also does so when another entity has the same name, or another event has the same time, type and targets. The active mod causes these problems, so their origin is the active mod. `check` with the scope `all` and the error `save_blocked` name the same references.

`get_cutscene` gives the entities and the events of a script, each with its fields. They come in pages, as the rows of a list tool do: first the entities, then the events. `entity_count` and `event_count` give the numbers of the whole script, and `next_cursor` gives the next page. A large retail script has more than a thousand elements. Use a smaller `limit` when a page is larger than the size limit of a result, or read one element with `get_item`.

In the editor, the Console entry of a cutscene edit links to its element, and follow mode opens the script in the Cutscene tab. "What you see in the editor" tells more.

#### The generic tools on cutscene items

`set_fields`, `rename_item`, `delete_items` and `duplicate_items` also change the entities and the events of a cutscene, with the commands of the Cutscene tab and the Inspector. Each call is one Undo step. One call can name cutscene items with items of a system and mission items. A cutscene item is in the group Cutscenes, so these tools refuse it with `not_available` when you clear that group.

| Tool | Cutscene references | What it does with them |
|---|---|---|
| `set_fields` | `#entity/` and `#event/` | Sets each value by its THN path, as the fields of the Inspector do. See below. |
| `rename_item` | `#entity/` | Renames the entity, as its name field does. The event targets and the speakers of the script that name the entity change too. |
| `delete_items` | `#entity/` and `#event/` | Deletes as Delete in the Timeline does. An entity goes with the events that name it. |
| `duplicate_items` | `#event/` | Copies the events as Duplicate in the Timeline does. The copies start at the end of the events. |

`set_text` does not change cutscene items. It gives `invalid_argument`, and the message names `set_fields`. A reference of a whole script gives `set_fields` the error `refused`: use `cutscene_set_duration` for the duration. `duplicate_items` gives `refused` for an entity, because the editor has no copy of an entity. Another kind of `thn:` reference gives `invalid_argument` with the detail `CutsceneItem`.

`get_cutscene` and `get_item` give the fields of an element by their THN paths. An entity gives each key of its record, and an event gives each key of its props, for example `cameraprops.fovh`, `spatialprops.pos` and `props.duration`. A record gives the keys in it, at each depth. The name, the type, the template and the flags of an entity are own properties, not fields. So are the time, the type and the targets of an event. Each value has the form that `set_fields` takes. So you can give a field back to `set_fields` as it is, and the call changes nothing.

The game uses the last entity with a name, also in other letter case. Some retail scripts have two entities with one name. The earlier entity then has `read_only_reason` and no fields, because its reference names the later entity. So the edit tools change only the later entity.

The value forms of the THN keys:

| Kind of key | Form | Example |
|---|---|---|
| Number | A number. | `"cameraprops.fovh": 20` |
| Text | A text. | `"props.animation": "Sc_wave"` |
| Yes or no | `true` or `false`. A stored number stays a number, 0 or 1. | `"lightprops.on": false` |
| Vector | Three numbers. | `"spatialprops.pos": [0, 10, -50]` |
| Quaternion | Four numbers: w, x, y and z. | `"props.spatialprops.q_orient": [1, 0, 0, 0]` |
| Rotation | Three angles in degrees, as the Inspector shows them. The file holds a matrix. A value of the matrix within 0.000001 of 0, 1 or -1 becomes that value. | `"spatialprops.orient": [0, 90, 0]` |
| Name | A THN name of the group of the key, or the number of such a name. Another number gives `invalid_value`. A stored number stays a number. | `"lightprops.type": "L_DIRECT"` |
| Bits | A list of the names of the bits that are on, or a whole number. The other named bits of the key go off, so `[]` turns off all named bits. A bit without a name keeps its state. | `"props.event_flags": ["Loop"]` |
| Motion path | `closed`, and one row for each key: a `point`, and an `orientation` (w, x, y and z) when the path has them. A JSON text also sets the path text, for example `"OPEN, {0,0,0}, {1,0,0,0}, "`. That text holds `OPEN` or `CLOSED`, numbers and tables of numbers, with a comma between two items. A number can have a minus sign. A text in quotes, a table in a table, a field, a comment and each other part give `invalid_value`. The editor does not run such a text, and the message tells the rule of a path text. The same rule applies to each new path text of another entity type, in each form of the value (see Other key). | `"pathprops.path_data": { "closed": false, "keys": [ { "point": [0, 0, 0], "orientation": [1, 0, 0, 0] } ] }` |
| Curve | `clsid`, and one row for each point: `time`, `value`, `in` and `out`. | `"props.param_curve": { "clsid": "FreeFormPCurve", "points": [ { "time": 0, "value": 0, "in": 0, "out": 0 } ] }` |
| Turn | `degrees`, and an optional `axis`: an axis name or three numbers. | `"props.spatialprops.axisrot": { "degrees": 90, "axis": "Y_AXIS" }` |
| Other key | Lua text in a JSON text, as the engine reads it. A key that the THN schema does not know has this form too. The text holds one value: a number, a text, a THN name, or a table of them. An operator, a call, a second value and another name give `invalid_value`, and the editor does not run the text. A text that the Lua 3.2 compiler cannot read to its end, a line that starts with `$` and tables in more than 32 levels also give `invalid_value`. The engine reads `pathprops.path_data` of an entity of each type as a path. It also reads the key from a `pathprops` table with list items, for example `{ 1; path_data = "..." }`. So each new path text of a CAMERA, a MARKER or another entity needs the rule of the Motion path row. This rule applies to the key `pathprops.path_data` and to a whole `pathprops` table, with or without list items. Another path text gives `invalid_value`. | `"props.custom": "{ 1, 2 }"` |

Only `null` removes the key. A key of a record goes with its own path. A record as a value gives `invalid_argument`. A value of the wrong form gives `invalid_value`, and the message gives the form. A stored value that does not fit its form shows as Lua text, and the same text keeps the value.

A THN file is ASCII. So a text with a character outside ASCII, or with the character U+0000, gives `invalid_value`. This rule applies also to a text in Lua text, and to an escape such as `\200` that gives such a character. A path with such a character gives `invalid_argument`.

Some keys have their own rules, as in the Inspector:

- `time` and `props.duration` move the event, as the time and length fields do. They round to the 1 ms grid, and a time below 0 gives `invalid_value`. A SET_CAMERA event has no length, so `props.duration` gives `invalid_value` there.
- `targets` takes one entity name for each target of the event, for example `["Monitor", "Camera_01"]`. Another number of names gives `invalid_argument` with the detail `TargetCount`, as in `cutscene_add_event`. Each entity must fit the role of its target. A target of the wrong type gives `refused`, and the message names the types that fit. A name that no entity of the script has gives `not_found`.
- `template_name` sets the template of an entity, as the template field of the Inspector does, for example `"template_name": "li_manhattan_shipdealer"`. Get the names from `list_values` with the list of the template (see the table of the lists of the templates above).
- `entity_name` and `type` give `refused`. Use `rename_item` for the name. To change the type, delete the element and add another one.
- The `flags` of an entity give `refused`. Use `cutscene_set_flags`.

`set_fields` takes `expect_type` and `expect_time` for an event reference. `delete_items` and `duplicate_items` take them in the rows of `expect_events`: each row names one event of `refs`. These guards work as the guards of `cutscene_move_events`. A guard for a reference that is not an event gives `invalid_argument`.

A rename of an entity needs `confirm: true` when other data uses the name. An example is the `name` of a `[Camera]` in a room that plays the script as its set script. Another example is a REFERENCE entity with the name in another script of the room. The rename changes only the script, so these uses keep the old name, and the message names them. The tool reads the files of the mod folders on disk for the uses. Its first call after an open or a save reads each INI file.

A new name that another entity has gives `name_taken`, also in other letter case. A new name with a character outside ASCII, or with the character U+0000, gives `refused` with the detail `NameInvalid`. The result gives the rename and the events that the rename changes.

A delete of an entity that events name needs `confirm: true`, as the editor asks. The message names the events with their indexes before the call. One call that deletes more than one entity asks with one message for all of them. The result gives each event with its index before the call, and the reason `cascade`. In `edit_batch`, the message and the result of a step give the indexes before the step.

`duplicate_items` with `time_offset` puts each copy at the time of its event plus the offset, in seconds. A copy at the time of its event has the key of the event, so an offset of 0 gives `invalid_value`. A copy before 0 gives `invalid_value` too. `time_offset` without an event gives `invalid_argument`. An event has no position, so `offset` does not move a copy of an event, and the result warns about each copy.

### Save

`save` saves the edits of the active mod into its patch files, as **File > Save** does. In the editor, the save writes its report to the Console, loads the game data again and validates the saved files. The result of `save` then also gives the result of the validation. The oltool host does not validate.

After a save that changed files, the editor loads the game data again. `studio_status` gives `data_load: reloading` until the load ends, and `ready` after it. The first load of a profile also gives `reloading`. The oltool host loads no game data, so it always gives `ready`.

During a load, only `studio_status` runs. Each other call waits until the load ends, because the load fills the game data of the editor. The call then reads the game data of the save. So a view or a play of a system or a base that the save added works after the wait. When the load takes longer than the wait limit of the agent settings, the call gives `busy` with the reason. Call `studio_status` until `data_load` is `ready`, then call the tool again.

A system that you did not save gives `refused` with the detail `NotLoaded`. The load reads only the saved files, so save the mod first. A new object, zone or light of a system that the game data has shows at once, without a save.

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

The export refuses unsaved edits, because it uses the saved files. Save first. It also refuses while another export runs, a version that is not safe in a path and a publish history that does not load. The export runs the save gate of the mod first, as `save` does. When the gate finds errors, the export gives `save_blocked` with the detail `Gate` and writes nothing. The message starts with "The export of mod '<id>' stopped." and then gives the messages of the first errors, as the message of `save` does. An example is a patch with `ol.delete` of a costume that only an unchanged retail cutscene loads. Call `check` with the scope `all` to read each error. A version with the mark "published" always gives the error `guard_conflict`. An earlier export of the version with other game files gives `guard_conflict` without `replace_version`.

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
- The player gets the ship of the Ship field of the play start of the script, as with F5. Without a pick in the field, the player gets the ship of the profile. "The Play start page" in [MISSIONS.md](MISSIONS.md) tells about the field. When the edit view does not have that ship package or its ship, the tool gives `refused` with the reason.
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

#### Images of the game

`play_capture` gives an image of the running game of Play in OpenLancer, as the player sees it. The game hides its debug windows for the frame of the image. It works only in the editor host, and it changes nothing. So it also works while you pause the agent edits. The previews of the editor do not run the mission orders. So an image of the game is the only true image of a mission shot, a comm window or a base room.

| Argument | What it does |
|---|---|
| `delay` | The seconds to wait before the game takes its next frame, from 0 to 60. The default is 0. |
| `width`, `height` | The most pixels of the image on each side. The default is the image limit. A larger game window gives a smaller image with the same shape. |

The result gives the size of the image and the state of Play when the game took the frame. Before the state `running`, the image can show the load of the game. The game writes each image as a PNG file in the folder `captures` of the play folder (see "The play folder" in [PLAY.md](PLAY.md)). The editor reads the file from that folder only, and deletes it after the read.

These cases give an error:

- No game of Play in OpenLancer runs, or only the game of a play copy runs: `refused` with the detail `NoGame`. Start a game with `play` or `mission_play` first.
- The game did not give the image in 10 s, or the game closed: `refused` with the detail `CaptureFailed` and the reason.
- The game could not write the file: `refused` with the detail `CaptureFailed` and the reason.
- The editor cannot read the file as a PNG image: `refused` with the detail `CaptureFailed` and the reason.
- A side above the image limit: `limit`.
- You clear the group Play: `not_available`. The oltool host also gives `not_available`.

#### Example: see the game

These steps check the data reload and the images of the game with a base of the mod. The base can be a base that the last save added.

1. Ask the user to save the mod, or call `save` when the user allows it.
2. Call `studio_status`.
3. If `data_load` is `reloading`, wait 5 seconds. Then do step 2 again.
4. Call `play` with `start: base` and the base. The call can end before the game shows the base.
5. Call `play_capture` with `delay: 5`.
6. If the call gives `CaptureFailed`, or `play` in its result is not `running`, do step 5 again. Before `running`, the image can show the load of the game.
7. Look at the image. It shows the room of the base.
8. Call `play_stop`.

### What you see in the editor

The editor shows each agent edit in the Console, in the Scene, in the Hierarchy and in the status bar.

| Place | What it shows |
|---|---|
| Console | Each agent edit adds an Info entry with the source "Agent" and the label of the edit, for example "Create Zone_test_01.". Click the entry to select its item. The entry of a cutscene edit opens the cutscene and selects its element. When another event has the same time, type and targets, the entry selects the cutscene. A later edit can move that other event to the index of the element. A refused call of a tool that changes data adds a warning with the tool and the reason, also a call that waited too long. An error in the second part of a call adds no entry yet. Examples are a Play that you cancel, an export with `guard_conflict` and a save that the save gate of Play stops. An undo and a redo of the agent add an entry too. A preview, a read tool and the question `needs_confirmation` add no entry. When the editor cannot show an agent edit, it adds an Error entry with the reason. The edit stays in the Undo history. **Agent > Show agent messages** shows only the entries of the agent. |
| Scene | A pulse ring in the agent color goes around each item of an agent edit for 4 seconds. |
| Hierarchy | A robot glyph at the right end of a row shows an item that an agent edit changed after the last save. The blue mark of the unsaved edit stays, so the row shows both marks. `list_items` gives the same mark as `agent_changed`. In the tree of a mission, the row of a trigger or of another section of the script shows the glyph too. The header row shows it for the `[Mission]` section. The rows of a mission have no blue mark. While the Cutscene tab shows, the top row of the cutscene shows the glyph after an agent edit of the script. The editor records a cutscene edit for the whole script, so the rows of the entities have no glyph. |
| Status bar | The agent program and the number of its edits since the last save, or "Agent paused". |

An undo of an agent edit removes its marks, and a redo puts them back. A save clears the marks.

An agent call never runs during a gesture of your own. It waits while you edit a field, rename an item or drag an item, and while a dialog is open. In the mission studio, it waits while one of these is open:

- The rename popup of a row of a mission in the Hierarchy.
- The rename field of a node of the graph, and quick add.
- A drag of a node or of a link of the graph.
- A drag of an item of the library, of an action row of the graph or of a row of the tree of a mission.
- The rename popup and quick add of the Mission page in the Inspector, and the rename popup of a prefab in the Assets panel.

The call runs in the frame after the gesture ends. A gesture in a tab that you hide does not keep the call waiting. After the wait limit of the agent settings, the call gives the error `busy` with the reason.

A call also waits while the editor loads the game data, for example after a save. Only `studio_status` runs during the load, so the agent can see the state of the load. See "Save".

#### Follow mode

With **Agent > Follow agent** on, the editor opens the system of each agent edit and moves the camera to its item. An agent edit of a mission opens the script in the Mission tab, and the graph moves to the item. While the Mission tab shows the raw INI, the view does not move, also when you show the graph again. An item without a node, for example a mission ship, shows in the Hierarchy and the Inspector when it is selected. An item without a place in the Scene, for example a loadout, shows in the Inspector, and the view does not change. So does an item that the Mission tab cannot show yet, for example a trigger of a new script before the mission run reads the script.

The selection follows Unity. A create or a copy selects the new item, a delete clears the selection, and a rename keeps it. When the agent undoes its delete, follow mode selects the item again. When you undo an agent delete, the Scene selects the item again, as after the undo of your own delete. This is also true with follow mode off. An undo of an agent delete in a mission keeps the selection of the Mission tab, as the undo of your own mission edit does.

An agent edit of a cutscene opens the script in the Cutscene tab and shows the Timeline. An add selects as your own add in the Timeline does: the new events, else the last new entity. So a copy of two events selects the two copies. A new script without new elements gets the selection. A change or a rename keeps the selection. A delete selects the cutscene, as your own delete in the Timeline does. When the agent undoes its delete, follow mode selects the element again. The Cutscene view keeps its mode and its camera, so it does not move to an entity.

Follow mode does not leave prefab mode, as Unity keeps prefab mode open when the scene changes. While the Mission tab shows a prefab, an agent edit of a mission script or of its ship build file shows only in the Console. The selection and the view stay.

When you move the camera of the Scene, pan or zoom the graph of the Mission tab, or select an item, follow mode stops. A selection in the Timeline or in the Hierarchy of a cutscene stops it too. A move of the camera of the Cutscene view does not stop it. Click **Follow agent** to start it again. The settings keep the last value that you clicked, so follow mode is on again at the next start of the editor. With follow mode off, the agent changes the view and the selection only with `open_view` and `select_items`. When an agent edit removes the selected item, the selection goes, as after an undo. When an agent edit renames the selected item or moves the row of a selected line, the selection stays on the same item.

#### Pause

**Agent > Pause agent edits** stops each agent edit until you pick **Resume agent edits**. A click on the agent segment of the status bar opens the same menu. While you pause, each tool that changes data, writes files or starts a play gives the error `paused`. The read tools, the view tools, `publish_checklist`, `play_capture` and `play_stop` still work. The pause stays when you stop and start the server again.

## Resources and prompts

The server gives these resources. An agent program can read them before its first call. The resources hold the texts and the rule data of OpenLancer. The catalog of the prefabs also holds the prefab assets and the prop groups of the open profile.

| Resource | What it gives |
|---|---|
| `openlancer://schema/mission-names` | The 129 conditions and actions of a trigger. Each one has its plain name, its event or action group, its values with their kinds and help, and what OpenLancer does with it. These are the rows of `describe_mission_name` without the counts of the base game. |
| `openlancer://schema/mission-sections` | The 12 section types of a mission script and the ship build (`[NPCShipArch]`), with their keys and values. A type that `mission_add_item` adds has its kind, for example `ship` for `[MsnShip]`. |
| `openlancer://schema/objlist-directives` | The 23 orders of an order list, with their values. The `steps` of `mission_add_item` take them. |
| `openlancer://catalog/prefabs` | The behavior prefabs with their fields: the built-in prefabs and the prefab assets of the mods. Also the templates of `scene_create_object`, the builders `scene_place_wreck` and `scene_place_patrol_route`, and the prop groups of the profile. Without a profile, it gives the built-in prefabs and the rule data only. |
| `openlancer://catalog/lists` | The data lists with their ids and labels. `list_values` takes the ids. |
| `openlancer://catalog/thn-events` | The event types that `cutscene_add_event` adds, in the order of the Add event menu. Each one has the entity types of its first and its second target, if it needs the second target, and if it has a length. Also the entity types that `cutscene_add_entity` adds, and if each one takes a position. |
| `openlancer://guide/rules` | The rules of the editor and what they mean for an agent. |
| `openlancer://guide/refs` | The forms of the item references. |

The catalog of the prefabs reads the open profile, so its read waits in the queue of the tool calls. It waits while you edit a field or do a gesture, as a call does. It counts in the calls each second. Above the calls each second or after the wait limit, the read gives a protocol error with the reason. The other resources read only the rule data, so they do not wait.

The server gives two prompts. An agent program shows a prompt as a command. In Claude Code, the commands are `/mcp__openlancer__two_trigger_mission` and `/mcp__openlancer__short_cutscene`.

| Prompt | Argument | What it gives |
|---|---|---|
| `two_trigger_mission` | `script`: the `msn:` reference of a mission script. `list_missions` gives the scripts. | The steps to add a pirate ambush with two triggers. The agent adds the ship build, the NPC and the ship with `mission_add_item`. It adds the two triggers and the loot in one `edit_batch`. Then it runs `mission_dry_run` and `check`. The names in the steps are examples. |
| `short_cutscene` | `name`: the name of the new cutscene script, for example `my_intro`. | The steps to make a short cutscene. The agent makes the script with `cutscene_create`. It adds a second camera, a character with a costume of `list_values` and a marker with `cutscene_add_entity`. It adds a camera move and a cut to the second camera with `cutscene_add_event` and `set_fields`. Then it makes two images with `render_cutscene`, corrects the timing and runs `check`. It does not save until you ask. The names in the steps are examples. |

The prompts need the edit tools. So a host that only reads, for example `oltool mcp serve --read-only`, lists no prompt. A host without `save`, for example `oltool mcp serve --no-save`, lists both prompts. Only the editor host makes the images of `short_cutscene`. In the host without a window, the agent goes to the next step. The prompts `build_outpost` and `patrol_and_wreck` and the resource `openlancer://guide/workflows` are not in this version. They come after version 1.0 ([#70](https://github.com/spool1/openlancer/issues/70)).

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
mcp__openlancer__play_capture
```

`open_view` and `select_items` change what the editor shows. Without a rule, the agent program asks you before each call. For the oltool host, the names start with `mcp__openlancer-headless__` (the name of the server in the example below).

The rules above do not allow an edit tool, `save`, `play`, `mission_play` or `play_stop`. So the agent program asks you before each edit, each save and each play, unless you add a rule for it. Each edit is one Undo step, so you can undo an edit that you allowed. A save is not an Undo step.

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

The editor writes each call of an agent into an audit log: one file for each run of the editor, for example `%LOCALAPPDATA%\OpenLancer\logs\agent-20261020-142233.jsonl`. Each line holds the time, the tool, the arguments and a summary of the result. A call that did not run, for example a call above the rate limit, has no arguments in the log. A call that gives an error has its arguments only up to 16 KB, because it changed nothing. The log holds no token. At 64 MB, the log stops, and the Console tells you.

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

The agent reads the game data and the mod data of the profile and the Console messages. It also gets images of the editor panels and of the game. The agent program can send all of it to its model service. The Console messages and the images can show the names of your folders.

Names and texts in the data can hold text that looks like an instruction. The tools give such text as a value in a field of the result, never as an instruction. The agent can only call the fixed tools.
