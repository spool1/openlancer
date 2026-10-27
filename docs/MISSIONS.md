# The mission studio

The editor shows each mission script of the game data and of the mods. It shows a mission as a graph of triggers beside the Scene, with named fields in the Inspector and the checks of the script. With an active mod, it also edits the mission. A save writes the changes into the mod as a patch of the script. The format of a mission patch is in [MOD_FORMAT.md](MOD_FORMAT.md), section "Mission files".

Without an active mod, the studio shows each mission and takes no edits. So you can study how the retail missions work, and no mod changes.

This version has these limits:

- The graph of prefab mode shows a slot of a field as its label in brackets, for example "[Enemy formation]", not as a chip.
- In prefab mode, the Raw INI view does not show, and the INI block of a page is read-only. The Prefab page shows the text of the prefab file.
- The Scene part does not drag mission items yet. Use "Pick in Scene" to set a position.
- The editor writes the lines of a mission. OpenLancer does not run each line yet. The column OpenLancer of the Missions tab tells what it runs.
- Prefab mode has no dry run and no play from a trigger.
- A play from a trigger runs only in OpenLancer, and only for the 14 story missions. A play from a trigger in the linked game (Ctrl+F5) comes with a later version.
- The graph shows the chips of the dry run only at full detail. Zoom in to see them.

## Open a mission

1. Run `start.bat` to build and start the editor.
2. Click File > Open profile, then click a profile.
3. Click the Missions tab at the bottom of the window. The 14 story missions come first, in the order of the game. The other scripts of the game data and the mods follow.
4. Type a name, a nickname or a value in the search field. For example, type `m04` or a trigger name.
5. Double-click a row. The mission opens in the Mission tab, beside the Scene tab. One mission is open at a time.

## The Missions tab

Each row is one mission script.

| Column | Content |
|---|---|
| Slot | The story slot. The game code has 14 slots in a fixed order. |
| Mission script | The file that the game loads for the slot. |
| Start stage, Accept stage | The story stages when the mission starts, and after the player accepts it. |
| Next mission | The time when the next mission starts after a success. The game code sets it. A mod cannot change it. |
| State | Base game, Changed (a patch of a mod changes the script), Replaced (a mod has its own file) or Missing. |
| Triggers, Errors, Warnings | The counts of the script. The number in brackets counts the hidden base-game messages. |
| OpenLancer | What the OpenLancer engine runs of the script. The retail game runs all lines. |

The toggle "base-game messages" shows the problems that the base game has too. They are hidden by default, because a mod does not cause them. The Console and all mission views use the same toggle.

## The Mission tab

The Mission tab shows the open mission as a graph. The Scene part is at its right, and a splitter is between them. The toolbar has the mission menu, the switch between Graph and Raw INI, the toggle "Dry run" and the chip "Play start". It also has the lens, "Show lines", "Frame all", the search, the menu Systems, the toggle Library, "Add..." and the toggle Scene.

### The graph

- Each node is one trigger. Its header has the glyph of its event, its name, and the chips "Start" and "Repeats". Its body shows the event and the actions as a summary. With "Show lines", it shows each line.
- The pins at the left take a start, a stop, a load from a checkpoint and a watch. The pins at the right give them. The "+" pin in the header starts or stops another trigger.
- A frame groups triggers: an act, the frame "Not started by the mission", or a behavior prefab. Click the arrow of a frame to collapse it into one node.
- The marks at the right of a header show errors, warnings, the muted base-game messages, "Never starts" and "Watched state". Hover a mark to read the reasons.

A lens shows one part of the mission. A lens does not change data.

| Lens | Shows |
|---|---|
| Beats | Each behavior prefab as one box. This is the default lens. |
| Logic | All triggers, with all frames open. |
| Battle | Spawns, orders, sides and combat conditions. Other triggers fade. |
| Conversation | Comm lines, conversations and the triggers that wait for them. |
| Cutscene | Space cutscenes, cameras and base scenes, in the order of the mission. |
| Objectives | Objectives, titles, offers and log texts, in the order of the mission. |

The keys work while the graph has the focus:

| Key | Action |
|---|---|
| F | Frame the selection. |
| A or Home | Frame the whole graph. |
| Ctrl+F | Go to the search. Enter frames the next result, and Shift+Enter the one before. |
| F3, Shift+F3 | Go to the next or the previous item that the selection uses. |
| Space | Open quick add. |
| F2 | Rename the selected trigger. |
| Ctrl+D | Duplicate the selected triggers and prefab instances. |
| Delete | Delete the selected triggers, links, acts and prefab instances. |
| Ctrl+C, Ctrl+V | Copy and paste triggers as INI text. |
| Esc | End the path view or the neighbor view. In prefab mode, go back to the mission. |

### The Scene part

The Scene part shows the system of the mission. The toggle "Mission items" of its toolbar shows the ships, points, volumes and paths of the mission in the color of the mission items. Select a trigger, and the Scene shows what it uses. Select an item in the Scene, and the graph outlines the triggers that use it. Double-click a node to frame its items in the Scene. The menu "Mission systems" opens another system of the mission.

When the Mission tab is narrow, the Scene part closes. The toggle Scene of the toolbar opens it again.

### The Raw INI view

The switch Raw INI shows the whole script as text, with the colors of the INI keys and the marks of the checks. A cursor in a section selects its item. With an active mod, you can edit the text. The editor checks the text when you leave the field. A problem keeps nothing: the Console holds the problem and your text, so you can copy it again.

A raw edit has these rules:

- A change of the first nickname of a section stops the edit. Use Rename (F2), so that the lines that name the item change too.
- When another edit changed a section while you typed, the edit stops with "The section changed while you typed." Copy your text, and edit again.
- The editor does not keep a comment inside a mission section.
- A moved ship or formation section moves, because its place has an effect in the game. Another section keeps its place, and a note tells you.
- A line of a member of a linked prefab instance shows dim and takes no edit (see "Behavior prefabs").

## The dry run

The dry run runs the trigger rules of the retail game on the graph of the open mission, step by step. It has no game world. You decide what happens in the world. The graph then shows which triggers wait, run and stop, in the order of the retail game. The dry run changes no file, and it works without an active mod.

### Start a dry run

1. Open a mission.
2. In the toolbar of the Mission tab, click the toggle "Dry run". It has a flask glyph, so it does not look like Play. The Run panel opens at the right of the graph.
3. Click "Start mission". The start triggers start.
4. Click "Heartbeat". The triggers that the start triggers start become active at the first heartbeat.
5. In the group "Waiting events", click "Happens" on the event that happens in your test.
6. To remove the last step, click "Back".

### Start a dry run at a checkpoint

1. In the Run panel, click "Load checkpoint". A menu with the checkpoints of the script opens.
2. Click a checkpoint. The steps before it go, and its restore trigger starts, as a load of a saved game does.

### Start a dry run at a trigger

1. Right-click a trigger in the graph.
2. Click "Dry run from here". The dry run turns on, and the steps before go.
3. Look at the graph. The triggers that wait at that trigger are active, as in a play from that trigger (see "Play from a trigger").

### The Run panel

| Item | What it does |
|---|---|
| Start mission | Starts a new run: the start triggers of the mission start. |
| Load checkpoint | Starts a new run: the restore trigger of a checkpoint starts, as a load of a saved game does. |
| Run trigger | Runs the selected trigger now, as if its condition is met. Only a trigger that waits can run. The node menu item "Run this trigger (dry run)" does the same. |
| Heartbeat | Advances the time by 1/30 s. The game tests timers and some conditions at each heartbeat. |
| Next timer | Advances the time until the next timer runs, at most 10 minutes. |
| Back, Reset | Back removes the last step. Reset removes all steps. |
| Timer ranges | Shortest or Longest: the time of a timer with a range. The game picks a time in the range when the mission loads. |
| Follow | The graph frames the trigger of each new change. |
| Player system, Player is in space | The facts about the player. Cnd_SystemEnter is true at once in the system of the player. A timer with RUN_IN_SPACE_ONLY counts only in space. |
| Waiting events | One row for each condition line that the active triggers wait for. "Happens" makes the event happen. "Is true" sets a fact that the game tests at each heartbeat. A fact stays true until you clear it. A fact that is true while no active trigger tests it stays in the list with a note, so you can clear it. The count of the group does not count it. |
| Run log | One row for each step and each change. Click a change to select its trigger. A trigger of a prefab instance shows the name of the prefab first, for example "Spawn wave: tr_wave2". |

A row of "Waiting events" that the run made likely comes first, with "Sent at step {n}" or "Spawned at step {n}". An example is a comm line that the run sent. A condition that counts shows its count, for example "(1 of 3)".

### The run states in the graph

Each state has a chip above the node, with a glyph and a word.

| Chip | Meaning |
|---|---|
| Waits | The trigger is active. It waits for its condition. Its outline has the run color. |
| Ran, Ran {n} times | The trigger ran in the last step. Its outline and its start links flash. |
| Has run | The trigger ran before. It is not active now. |
| Stopped | Another trigger stopped this trigger. |
| Starts next ({k}) | The game starts this trigger at the next event or heartbeat. The number gives the order. |
| {n} wait | A collapsed frame has {n} triggers that wait. |

The Trigger page of the Inspector gets the section "Run" while the graph shows a dry run that started. It shows the state of the trigger, the step of its last start, its runs, and its last change with its cause.

### Rules of the dry run

- The dry run uses the order of the retail game. A trigger that another trigger starts becomes active after the actions of the pass, smallest game id first. The OpenLancer game runs the triggers with its own rules. In some cases they differ.
- When a trigger starts, the game tests its condition once. The dry run does this test for Cnd_True, Cnd_Timer, Cnd_WatchTrigger and Cnd_SystemEnter. It tests a fact of the world, for example Cnd_InSpace, at the next heartbeat. The game tests some of these facts at once, so the trigger can run one heartbeat earlier in the game.
- Start mission and Load checkpoint start a new run. The steps before them go. The facts and the facts about the player stay.
- A step of many heartbeats gives one row to a change that repeats, with a count. The row has the place of the last time of the change, so the last row of a trigger tells its state.
- After the mission ends or the run stops, a step does nothing. "Run trigger", "Heartbeat", "Next timer" and "Happens" are off. Click "Start mission" or "Load checkpoint" to start a new run.
- The dry run follows each change of the script, also Undo and Redo. A rename moves the steps to the new name. A step whose trigger the script does not have now shows "Missing: {name}".
- One event of the game can match two lines, for example Cnd_BaseEnter of a base and Cnd_BaseEnter ANY. The dry run lists them as two events.
- A chain of starts that does not end stops the run with a message. A trigger without a condition stops the run too, as it stops the game.
- An agent can run the same dry run with the agent tool `mission_dry_run`. The same inputs give the same steps as in the Run panel.

## Play from a trigger

Play from a trigger starts the mission in the OpenLancer game at a point of the script. So you do not fly the whole mission to test one part. Each mission has one play start. It is the mission start until you set another start.

| Start | Which triggers | What the game gets |
|---|---|---|
| Mission start | The start triggers | The mission starts as a new mission. Exact. |
| Checkpoint {trigger} | The restore trigger of an `Act_Save` line | The triggers of a saved game at that checkpoint. Exact for the triggers. A saved game also keeps the objective, the dock and lane rules, the reputations and other state from before the save. The start save does not keep them, and the Play start page warns about it. |
| {trigger} (approximate) | Each other trigger | The triggers that wait at that point, and two setup triggers that rebuild the state of the path. Some state can differ, for example damage or the cargo of a ship. |

### Set the play start and play

1. Open a mission.
2. Right-click a trigger in the graph.
3. Click "Play from here". The trigger becomes the play start, and the game starts. To set the play start without a play, click "Set as play start". Click it again to make the mission start the play start again.
4. Press F5 in the Mission tab to play from the play start again.

The trigger of the play start has the chip "Play start". The toolbar chip "Play start: {start}" names the start. A click on it shows the Play start page in the Inspector, and the graph selects and frames the trigger of the play start.

The play start is a setting of the editor for each profile and script. It is not data of the mod, so a change of it has no Undo entry, and the mod does not change. When you rename the trigger, the play start follows the rename, also its Undo and its Redo. The start of a running mission play follows the rename too. When the script does not have the trigger any more, F5 plays the mission start. The play start then becomes the mission start, and the Console tells it.

The Play menu has the submenu "Play mission". It is available while a mission is open in the Mission tab. Its items are "From the play start", "From the mission start" and one item for each checkpoint. The item of the play start shows F5 while F5 runs it. The mission start and the checkpoints start at their suggested start places. While a mission play runs, the submenu also has "Restart the mission". Only "Play from here" and "Set as play start" change the play start.

In the Scene and Cutscene tabs, and in the Mission tab without an open mission, F5 stays Play from camera.

### The Play start page

| Field | What it shows |
|---|---|
| Start | The point where Play starts the mission. Pick the mission start, a checkpoint or the trigger of the play start. |
| Story slot | The story slot of the mission. The start save names its mission. |
| Story stage | The story stage in the start save. After the accept, it is the accept stage of the slot. |
| Mission accepted | Yes when the start comes after the player accepts the mission. |
| Start place | Where the player starts: the suggestion, a base that you pick, or the Scene camera. The suggestion is where the player is at this trigger in the game (see below). A base or a system that the game data does not have shows its reason under the field. The Scene camera without an open system, and a suggested Scene camera in a system that the Scene does not show, also show their reason. |
| Source of the place | The line of the script that gives the suggested place, for example "Act_SetInitialPlayerPos of key_save_x.". When another line gives the system or the point, the text names that line too. Click "Select" to select the trigger of the line in the graph. The row shows only for the suggested place. |
| Ship | The ship package of the Play menu. |
| Triggers that wait at the start | The triggers that are active when the play starts. Click a row to select the trigger. |
| Advanced > Setup lines | The setup triggers of an approximate start, as read-only raw INI with colors. They are in the test layer of the play, not in the mod. |
| Play from the play start | Plays the mission from this start in OpenLancer, as F5 in the Mission tab does. The button is off while the start or its start place cannot start, and its tooltip gives the reason. |

A warning shows when no start trigger and no checkpoint leads to the trigger. The play then starts only this trigger. A warning also shows for a checkpoint start and for a start whose path starts at a checkpoint. The start save keeps only the triggers of a saved game. When the start cannot start, the page shows the reason, for example for a script without a story slot.

Two warnings are about the suggested place. A trigger that waits for the end of the launch does not run after a start in space, so the page names it. Pick a base to run it. A trigger that waits for the player to enter space in another system runs in OpenLancer, but not in the linked game.

### How the editor suggests the start place

The suggestion is where the player is at the trigger in the game. The editor reads the path to the trigger from the newest trigger back. The newest line that tells where the player is gives the kind of the start:

- A bar visit, a base entry or a forced landing docks the player at that base.
- A launch, a jump into a system, a line that moves the player, or a condition about the player in space puts the player in space.
- A trigger that waits at a base for the launch, or for the end of the launch, starts docked at the base of the launch. The launch then runs it.
- A trigger that waits for a jump starts before the jump, so the jump runs it.

The point of a start in space comes from a line that moves the player, a checkpoint, or a debug trigger of the script that moves the player to the same step. Else it comes from a point where a condition waits for the player, or it is 1,000 units from a ship that the path spawns. The system comes from the same lines, the base of the launch or a system hint. When no line gives a point after a launch, the start is docked at the base of the launch. When no line gives a point after a jump, the start is at the Scene camera in that system. Open that system in the Scene, or pick a base.

The section "Play a mission" of [PLAY.md](PLAY.md) tells what Play does with the start. It tells about the save, the start save, the test layer and the restart after a save.

## The live view

While a mission play runs, the graph shows the state of the triggers in the OpenLancer game. It uses the chips of the dry run: Waits, Ran, Has run and Stopped. The Run panel shows the Run log of the game, with the time of each change, for example "1:05.3". The toggle "Dry run" is off while the play runs.

| Item | Meaning |
|---|---|
| Waits {m} min | The trigger is active for 10 minutes or more. When its condition has no effect in OpenLancer, the tooltip tells it, because the trigger never runs here. The Console also gets a stall note for each trigger that waits for 10 minutes. |
| Rules differ | OpenLancer runs this trigger with another rule than the retail game. The tooltip gives the reason. |
| Play setup: {trigger} | A row of the Run log about a setup trigger of the test layer. |
| Not in the graph: {trigger} | The game runs a trigger that the graph does not have, for example after an edit that you did not save. |
| The game dropped {n} changes. | The game sent the changes too fast. The graph can miss some of them. |

The banner above the graph names the play and has a Stop button. The button is Force stop when the game does not respond, as in the Play menu. When the graph shows another mission than the game runs, the banner has a button that opens the mission of the game. While a mission play runs, a second banner tells that OpenLancer runs the triggers with its own rules. The dry run shows the retail rules.

The OpenLancer game differs from the retail game in some trigger rules. They are about two conditions, lines before the nickname, repeated nicknames, triggers without a condition and the order of starts. They are also about restarts, repeatable triggers, Cnd_SystemEnter, Cnd_SpaceEnter with a system and the COMPLETE state.

After the game exits, the live view stays with the time of the exit until the next play. The next play, also a play from the camera, starts a new live view. Click "Clear" in the Run panel to remove it. While the toggle "Dry run" is on, the graph shows the dry run. When you turn the dry run off, the live view comes back.

The Console gets only the notes of the play: the start, the end of the mission, the stall notes and the restart. The trigger changes go to the Run log only.

## The Hierarchy and the Inspector

In Mission mode, the Hierarchy lists the items of the open mission in groups. The groups are Logic, Cast, Labels, Orders, Objectives, Conversations and Links. Then come Random encounters, Behavior prefabs, Placed near the player, System unknown and Other sections.

The Inspector shows the page of the selected item. Each page has the same parts:

1. The header with the glyph, the name, the kind and the info icon. The info icon tells what the item is and what it writes to the game files.
2. The completeness panel, for example "4 of 5 steps complete". Each missing step tells why it is necessary. "Fix" makes the change when it needs no choice.
3. The sections with named fields, for example "Starts when" and one section for each group of actions.
4. The INI block of the section, with the marks of the checks.

A field that the active mod sets has a blue bar at the left of its label. A prefab override has a violet bar and a bold label (see "Behavior prefabs").

## Edit a mission

The studio takes edits when the profile has an active mod. Each gesture is one entry of the edit history. Undo and Redo work as in the Scene. A save writes the patch of each changed script and the editor files of the mod.

| Where | Gesture |
|---|---|
| Graph | Drag from the "+" pin of a trigger to the Start pin or the Stop pin of another trigger. Drag from an output pin to an empty place, then pick an item in quick add. The new trigger links to the pin. A prefab places a new instance, and the pin links to its first trigger. |
| Graph | Right-click a link to insert a trigger on it or to remove it. Right-click a node for "Starts with the mission", "Repeats", "Add action...", "Set condition...", Rename, Duplicate and Delete. |
| Graph | Drag nodes to move them. Right-click the background for "New act from selection". |
| Library | Drag an item of the strip at the left of the graph onto a trigger or onto an empty place. An item is a prefab, an event, an action or a condition. A double-click adds it to the selected trigger, or at the center of the view. |
| Quick add | Press Space, type a part of the name, then press Enter. |
| Inspector | Change a field. A name has a picker, a word has a dropdown and a list has the buttons Add and Remove. "Pick in Scene" sets a position with a click in the Scene. |
| Inspector | Click "Fix" on a problem or on a missing step. |
| Hierarchy | Use the "+" menu to add an item. Drag a ship from the Assets panel onto the group Cast to add a mission ship. F2, Ctrl+D and Delete work on the selected row. On a prefab instance, Ctrl+D and Delete work. |
| Raw INI | Edit the text of the script or of the INI block of a page. |

A delete of a trigger keeps each line that names it, and the line shows "Missing". A rename changes each line that names the trigger, in the same entry of the history.

The save gate stops a save when a script has an error that the edit made, for example a trigger without a condition. A problem that the base game has too is a warning. The Console lists the problems after each edit.

## Behavior prefabs

A behavior prefab is a group of triggers with named fields, for example "Spawn wave" with the fields Ships, "Spawn when", Orders and Sides. The studio knows three forms:

- A **matched instance** is a group of retail triggers that has the pattern of a prefab. The graph shows it in a frame. Its fields read the lines of its triggers. A field with a write rule writes those lines.
- A **linked instance** is a group of triggers that the editor made from a prefab. The graph file of the mod records its prefab, its field values and its triggers. A change of the prefab changes each linked instance.
- A **prefab asset** is a prefab file of a mod, `prefabs/missions/<id>.ini`. The editor also has 17 built-in prefabs. They are Checkpoint, Offer mission, Play space cutscene, Branch, Ambush, Spawn wave, Destroy targets, Conversation and Mission success. Then come Mission fail, Keep applying, Set reputation, Lock or unlock gate, Objective, Watched state, Escort and Patrol.

### Place a prefab

1. Make sure that the profile has an active mod.
2. Open a mission.
3. Open the group "Prefabs" of the library strip. The built-in prefabs come first, then the prefabs of the active mod, then the prefabs of the lower mods with a lock.
4. Drag a prefab onto an empty place of the graph. Or drag a tile of the category "Mission prefabs" of the Assets panel.
5. Select the new frame. The Inspector shows the fields and the completeness panel of the instance.
6. Set the fields. Each edit compiles the triggers of the instance again.
7. Link the outputs of the instance (see "Outputs").

### Outputs

A prefab can have outputs, for example "When all are destroyed" of "Spawn wave". An output is a port of the prefab. It starts triggers of the mission when its trigger in the prefab runs. So a placed instance continues the mission through its outputs.

- The section "Outputs" of the instance page has a trigger list for each output. Add a row, and pick a trigger of the mission.
- In the graph, drag from the "+" pin of an instance trigger to the Start pin of another trigger. When the prefab has an output for that instance trigger, the link goes to the output.
- Remove link on such a link removes the trigger from the output.

A trigger of the instance without an output for the link refuses the drag, with the reason.

### Overrides

A field of a linked instance that differs from the prefab is an override. Its label is bold with a violet bar. An override that is not saved also has the blue bar. A field that each instance sets, for example the ships of a spawn wave, never shows as an override.

The menu of an override has:

- **Revert**: use the value of the prefab.
- **Apply to prefab '{name}'**: make the value the value of the prefab. Each instance of the prefab in the active mod without an override of the field gets it.
- **Make a variant with this value**: for a built-in prefab or a prefab of a lower mod, which cannot change. The editor copies the prefab into the active mod with the value, and the instance uses the copy.
- **Show in raw INI**: go to the line of the field in the Raw INI view. A field whose value is in a line has this item.

The page menu "Overrides ({n})" lists each override with Revert and Apply. It also has "Revert all" and "Apply all", as the Overrides list of Unity does. The info icon of an override tells what it is and what it writes.

A trigger of a linked instance takes no line edits, because the prefab gives its lines. Change a field of the instance, open the prefab, or unpack the instance. The Raw INI view shows its lines dim. Hover a line to see its prefab. An edit of such a line stops, and the Console names the line.

A delete of such a trigger is allowed. The section "Triggers in this prefab" of the instance page then shows the trigger as "Removed", with the violet bar. The other triggers of the instance lose each line that names it, as Unity sets a reference to a removed child to None. Revert adds the trigger again. "Apply to prefab" removes it from the prefab and from each instance.

### Save as prefab and unpack

- **Save as prefab...** in the menu of a matched instance makes a prefab asset from its triggers. Each value that a field reads becomes a field of the prefab, with the value of the instance as its default. A start, stop or checkpoint line to a trigger outside the instance becomes an output. The instance becomes a linked instance of the new prefab, with no override, and it stays selected.
- **Unpack** removes the link of a linked instance. The triggers stay, and the studio can find them again as a matched instance. The first trigger of the instance becomes the selection.

The dialog "Save as prefab" has two fields:

| Field | Content |
|---|---|
| Name | The name in the library. |
| File name | The name of the prefab file. Use lowercase letters, digits, `_` and `-`, with at most 47 characters. No prefab of the library can have the same file name. |

### Prefab mode

Prefab mode edits a prefab asset of the active mod. A change there changes each linked instance of the prefab in the active mod.

1. Click "Open prefab" in the menu of a frame or on the page of an instance. Or double-click a tile of "Mission prefabs" in the Assets panel. Or right-click a prefab of the library strip, then click "Open prefab".
2. Read the bar at the top of the Mission tab. It names the mission and the prefab, and it tells how many instances change. For example: "Changes here change 3 instances in 2 missions of mod my-mod."
3. Edit the triggers of the prefab in the graph and in the Inspector, as in a mission. The Scene part closes, because a prefab has no world.
4. To make a value a new field, open the menu of the value. Then click "New field from this value".
5. To make a whole condition or line a new field, open its menu. Then click "New field from this line".
6. To use a field that exists, click "Field: {label}" in the menu of the value. "Fixed value" gives the value one fixed text again.
7. To see the Prefab page, select the prefab row of the Hierarchy.
8. On the Prefab page, change a default, the name of the prefab, or remove a field.
9. To go back to the mission, press Esc or click the back arrow.

The Prefab page shows the steps of the prefab, the fields with their kind, "Per instance" and default, the ports and the sections. A field that no line holds is a missing step. The page also shows the text of the prefab file, read-only, with a mark on each line with a problem.

Each edit in prefab mode and the compile of all instances are one entry of the history. The Console says how many instances changed, and names each instance that did not change.

A built-in prefab and a prefab of a lower mod cannot change. They open read-only, with the button "Make variant". "Make variant" copies the prefab into the active mod with a new file name and name, and opens the copy in prefab mode. You can also make a variant in the library strip. Right-click a built-in prefab, then click "Make variant".

### The Assets panel

The category "Mission prefabs" of the Assets panel shows the prefab assets of the active mod and of the lower mods. A prefab of a lower mod has a lock.

- F2 or Rename changes the name of a prefab of the active mod in the library.
- "Rename file..." changes the file name of a prefab of the active mod. Its id and each instance record of the active mod change too.
- Delete removes a prefab of the active mod. The editor asks first when instances use it. The instances keep their lines, show "Missing prefab" and get the warning P02. Undo restores the prefab.
- A drag of a tile into the graph places the prefab.

### Warnings of linked instances

| Rule | Cause | Fix |
|---|---|---|
| P01 | The lines of the instance differ from the last compile, for example after an edit of the patch with a text editor. A change of the prefab does not change this instance. | "Compile again" or "Unpack" |
| P02 | No mod has the prefab, or the prefab file has a problem. The triggers stay as they are. | "Unpack", or add the prefab again |
| P03 | The prefab changed after the last compile of the instance. | "Compile again" |

## Files of the studio

The editor keeps the places of the nodes, the acts and the records of the linked instances in a graph file of the mod. It keeps each prefab asset in a prefab file. The game does not read these files. See "Editor files of missions" in [MOD_FORMAT.md](MOD_FORMAT.md).
