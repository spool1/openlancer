# The agent server (MCP)

OpenLancer has an agent server. An agent program, for example Claude Code, connects to it and reads the data of a profile. The server uses the Model Context Protocol (MCP), so each agent program with MCP support can use it.

The agent reads the data as the editor shows it. It reads the systems and their items, the data lists, the assets, the missions and the cutscenes. It also reads the raw INI text and the problems of the checks. In the editor, the agent can also open views, select items, and get images of a system, of a cutscene and of a panel.

The agent can also edit the items of the active mod: set fields and texts, rename, delete and copy items, and undo its own edits. Each agent edit goes through the same commands, checks and Undo history as an edit of the user. An edit changes the edit view of the editor. It is not saved until you save the mod. See "The edit tools".

## The two hosts

Two programs can host the server. They give the same tools, with the same names and results.

| Host | How it starts | Transport | Tools |
|---|---|---|---|
| The editor | **Agent > Start agent server** in the editor | HTTP on `http://127.0.0.1:<port>/mcp` | All tools |
| oltool | The agent program starts `oltool mcp serve` | Standard input and output (stdio). It opens no port. | The read tools. This version of the oltool host only reads. |

Use the editor host when you work in the editor. The agent then sees your unsaved edits, and the view tools show its work to you. Use the oltool host for a script or for an agent that works without the editor. An editor-only tool gives the error `not_available` in the oltool host.

## Connect an agent to the editor

1. Open a profile in the editor.
2. Pick **Agent > Start agent server**.
3. At the first start, read the notice. It tells what the agent reads. Click **Start server**.
4. Pick **Agent > Connect an agent...**.
5. Click **Copy**.
6. Paste the text into the file `.mcp.json` in the folder of your agent project.
7. Start the agent program in that folder.

The status bar shows the state of the server, for example "Agent server: on, port 47815". While an agent calls, it shows the name of the agent program. Click the status bar segment to open the Agent menu.

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
| Auto-save and continue when an MCP agent starts Play | Off | When an agent starts Play and the active mod has unsaved edits, the editor saves them and plays. Off: the editor asks you first. See "Agent Play" in [PLAY.md](PLAY.md). The setting shows only when the agent server has a tool that starts Play. These tools come with a later version. |
| Agent tool groups | All groups | The agent can use the tools of the selected groups: Scene, Missions and Cutscenes. `render_scene` is in the group Scene, and `render_cutscene` is in the group Cutscenes. These tools are always available: `studio_status`, `get_item`, `find_references`, `list_values`, `get_raw_ini`, `check`, `get_console`, the edit tools, `open_view`, `select_items` and `capture_panel`. A tool of a cleared group tells the agent that you cleared the group. An edit tool also refuses an item of a cleared group, for example an object of a system when you clear the group Scene. |
| Rows of a page without a limit | 50 | The rows of a list result when the agent gives no limit. |
| Largest page limit | 200 | The largest limit that the agent can ask for in one list result. |
| Size of a result (KB) | 256 | The most text of one result. A larger result asks the agent for pages. |
| Calls each second | 10 | The most calls that the server takes in one second. |
| Wait for the editor (s) | 30 | How long a call waits while you edit a field or a dialog is open. |
| Time of a check or an image (s) | 60 | How long a check of a retail target or an image can take. |
| Size of a request (MB) | 4 | The largest request. The change applies at the next start of the server. |
| Largest side of an image (pixels) | 2048 | The most pixels on each side of an image. A larger panel capture becomes smaller. A `render_scene` or `render_cutscene` call with a larger `width` or `height` gives the error `limit`. |
| Items of one edit | 200 | The most items that one call of `delete_items` or `duplicate_items` names. |
| Sections of one edit | 2000 | The most sections that one edit call changes, adds or removes. A larger edit changes nothing and gives the error `limit`. |
| Steps of one batch | 100 | The most steps of one `edit_batch` call. |

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

`open_view` and `select_items` change what the editor shows. They do not change the data. The image tools change nothing.

### Images

An image has at most 2048 pixels on each side, and a larger panel gives a smaller image. A `render_scene` or `render_cutscene` call with a larger `width` or `height` gives the error `limit`. **Agent > Agent settings > Advanced** can set a smaller limit. The editor does not draw a frame while it renders an image. An image of a system that the Scene does not show also loads that system first.

The agent program can send each image to its model service. A capture of a panel can show the names of your folders.

To measure the images, start the editor with `OpenLancer.Editor <profile.json> --bench-agent-render <system>`. Give the nickname of a system of the profile. The bench opens the profile without an active mod and opens the system. It renders one image of 1024 by 640 to warm up. Then it renders one image from each of 10 directions, as `render_scene` does.

For each image, the bench writes two times to the engine log of the editor. The first is the time on the UI thread. The second is the time until the PNG is ready. The time on the UI thread includes the work of the GPU. Then the bench closes. The gate is a median under 100 ms. A median above the gate, or an image that fails, gives the exit code 1. The bench needs no agent server and does not change the editor settings.

### The edit tools

These tools change the data of the active mod. They work in the editor host. The oltool host of this version only reads, so it does not list them.

| Tool | What it does |
|---|---|
| `set_fields` | Sets fields of one item by their INI keys. |
| `set_text` | Sets the name, the infocard or another text of one item. The text goes into the `strings.json` of the active mod. |
| `rename_item` | Gives an item a new nickname, and changes each line that names it. A line that the active mod cannot change keeps the old nickname and shows "Missing". A data item must be an item that the active mod adds. |
| `delete_items` | Deletes items of a system with their children. Other lines that name them show "Missing". |
| `duplicate_items` | Copies items of a system with their children. With `offset`, the copies move by the offset. |
| `undo` | Undoes the last edit of the agent. |
| `redo` | Redoes the last edit that the agent undid. |
| `edit_batch` | Runs edit steps as one Undo step, all or nothing. |

Each edit is one Undo step. The Edit menu shows its label with "Agent: " before it, for example "Undo Agent: Delete my_station_1". You can undo and redo each agent edit, as each of your own edits. `undo` undoes only an agent edit at the top of the history. `redo` redoes only an agent edit that the agent undid. So the agent never undoes your edit, and never redoes an edit that you undid with Ctrl+Z.

The edit tools change only items of INI files. A mission script and a cutscene give the error `not_available`. The mission tools and the cutscene tools come with a later version. `delete_items` and `duplicate_items` take items of a system only: objects, zones, lights, nebulae and asteroid fields.

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
| `confirm` | `rename_item` and `delete_items`: applies an edit that changes more than its targets. |

When you would see a dialog for a delete or a rename, the tool gives `needs_confirmation`, and nothing changes. A delete or a rename that leaves "Missing" lines also needs it. The agent then calls the tool again with `confirm: true`. A preview tells it in `needs_confirmation`.

The result of an edit gives the new items (`adds`), the changed items (`changes`), the removed items (`deletes`) and the renamed items (`renames`). It also gives the "Missing" lines, the warnings, the text mentions, and the problems that the edit adds and removes. A call that changes nothing gives the status `no_change` and no Undo step.

#### Batches

`edit_batch` takes a list of steps. Each step names an edit tool and its arguments. All steps are one Undo step with the label of the batch. Each step sees the result of the steps before it, so a later step can name an item that an earlier step made or renamed. When one step fails, the error `step_failed` gives its index and its error, and the batch changes nothing. A step that needs a confirmation fails unless its arguments have `confirm: true`. `undo`, `redo`, `edit_batch` and the read tools cannot be steps.

#### Limits and pauses

One call names at most 200 items and changes at most 2000 sections. One batch has at most 100 steps. **Agent > Agent settings > Advanced** changes these limits. A call above a limit gives the error `limit` and changes nothing.

While you pause the agent edits, each edit tool gives the error `paused`, and the read tools still work. A later version adds **Pause agent edits** to the Agent menu.

The edit tools refuse with `read_only` when the editor takes no edits, and the message tells the reason. For example, another editor has the mod open, or the profile or the active mod is in the game folder. A mod in the OpenLancer repo also refuses until you allow its edits.

## Permission rules for the agent program

An agent program can ask you before each tool call. A permission rule of the agent program can allow a set of tools without a question. Claude Code gives each tool of the server `openlancer` the name `mcp__openlancer__<tool>`.

The name of each read tool starts with `list_`, `get_`, `find_`, `search_`, `describe_` or `check`, or is `studio_status`. So these rules allow all read tools:

```
mcp__openlancer__list_*
mcp__openlancer__get_*
mcp__openlancer__find_*
mcp__openlancer__search_*
mcp__openlancer__describe_*
mcp__openlancer__check
mcp__openlancer__studio_status
```

The image tools also change nothing, but their names do not match these prefixes. Allow them only when you accept that their images go to the model service of the agent program:

```
mcp__openlancer__render_*
mcp__openlancer__capture_panel
```

`open_view` and `select_items` change what the editor shows. Without a rule, the agent program asks you before each call. For the oltool host, the names start with `mcp__openlancer-headless__` (the name of the server in the example below).

The rules above do not allow an edit tool. So the agent program asks you before each edit, unless you add a rule for it. Each edit is one Undo step, so you can take back an edit that you allowed.

## The oltool host

Add a server that starts oltool to `.mcp.json`:

```json
{
  "mcpServers": {
    "openlancer-headless": {
      "type": "stdio",
      "command": "<OpenLancer folder>\\oltool.exe",
      "args": ["mcp", "serve", "<workspace>\\profiles\\my-profile.json", "--mod", "my-mod", "--read-only"]
    }
  }
}
```

`oltool mcp serve <profile.json> --mod <id> --read-only` opens the profile with the mod as the active mod. Then it serves the tools on its standard input and output. It writes its own messages to the standard error. This version only reads, so `--read-only` is necessary.

## The audit log

The editor writes each call of an agent into an audit log: one file for each run of the editor, for example `%LOCALAPPDATA%\OpenLancer\logs\agent-20261020-142233.jsonl`. Each line holds the time, the tool, the arguments and a summary of the result. The log holds no token. At 64 MB, the log stops, and the Console tells you.

`oltool mcp replay <profile.json> --mod <id> <log>` runs the calls of a log again and compares each result with the recorded result. It opens the profile only to read, so an edit call of the log gives the error `read_only`. It exits with the code 1 at the first result that differs.

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

The agent works on the open profile with its active mod, as you do in the editor. It reads the data of the base game and of the mods up to the active mod. An edit tool changes only the edit view of the active mod. No tool of this version saves the mod. No tool writes into the game folder or into another mod. No tool takes a path on the disk: each path in a reference is an install path, for example `DATA\SHIPS\loadouts.ini`.

### Data that goes to the agent

The agent reads the game data and the mod data of the profile, the Console messages and images of the editor panels. The agent program can send all of it to its model service. The Console messages and the images can show the names of your folders.

Names and texts in the data can hold text that looks like an instruction. The tools give such text as a value in a field of the result, never as an instruction. The agent can only call the fixed tools.
