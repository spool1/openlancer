# The cutscene editor

The editor opens each cutscene script (`.thn`) of the game data: the retail scripts and the scripts of the mods. It shows the entities and the events of a script on a timeline. It previews the script as the game plays it, and it shows each value in the Inspector. With an active mod, it also edits the script. A save writes the changes into the mod as a THN patch. The format of a THN patch is in [MOD_FORMAT.md](MOD_FORMAT.md), section "Cutscenes".

Without an active mod, the editor shows each cutscene and takes no edits. So you can study how the retail cutscenes work, and no mod changes.

This version has these limits:

- The preview plays the sounds of the open script in 2D only. It does not play the position of a sound, START_AUDIO_PROP_ANIM, or the sounds of the other scripts of a room.
- A new cutscene plays only in OpenLancer until the M11 check that the retail engine reads text cutscenes.
- The editor cannot change a whole cutscene file of a mod into a patch.
- The validation reports only the errors that stop a cutscene. It does not report warnings, for example an event after the end of the script.
- The save gate checks a cutscene without the game data. So it does not find a template or a costume that does not exist.

## Browse and preview the retail cutscenes

1. Run `start.bat` to build and start the editor.
2. Click File > Open profile, then click a profile. A profile without mods shows only the retail data.
3. Click Mod > None (read-only). The editor then shows all mods of the profile and takes no edits.
4. Click the Cutscenes tab at the bottom of the window. A scan reads each script one time. Until the scan ends, the toolbar of the tab shows "reading" and the count of the scripts that it read.
5. Type a part of the name or of the path in the filter field, for example `s002xe`. To show one category or group of scripts, select it in the group menu next to the filter, for example Main story or Mission 1.
6. Double-click a row. The script opens in the Cutscene tab, beside the Scene tab, and in the Timeline tab, beside the Cutscenes tab.
7. In the toolbar of the Timeline, click the play button after the label Preview. Or click in the Timeline and press Space.
8. To go to a time, drag on the ruler of the Timeline or on the scrub bar below the view. Or type the time in the time field at the right end of the scrub bar.
9. Click an event in the Timeline, or the name of an entity in the Timeline or in the Hierarchy. The Inspector shows its values. The event links in the Inspector page of an entity select its events.
10. To look through a camera from the free camera, select the CAMERA entity. Then click Align view to camera, the eye button of the Cutscene toolbar. The view changes to Free mode, with the pose and the field of view of the camera at the playhead.
11. To see the shot as the game shows it again, click Shot, the video camera button of the Cutscene toolbar.

The question mark button at the end of the Cutscene toolbar lists the keys of the preview and of Free mode.

## The Cutscenes tab

The Cutscenes tab is at the bottom, beside the Database, the Console and the Timeline. It lists each cutscene script (`.thn`) of the edit view: the base data, the mods before the active mod, and the active mod. It works also in a profile without an active mod.

| Column | Content |
|---|---|
| Name, Folder | The file name, and the folder group of the path: Bases, Story, GCS, Missions, Extras, Vendors or Intro, Mods for the new cutscenes under `DATA/MODS/`, and Other. The group menu uses the categories of the cutscene list (see "The cutscene list"), not the folder groups. |
| Duration, Entities, Events | The `duration` global and the number of entities and events. A scan reads each script once, a part in each frame, so the counts fill in during the first seconds. The tooltip of a script that the scan cannot read tells why. |
| Used by | The INI lines that name the script, for example `M000_s002xe_Li01_01_nrml.ini [CharacterEncounter] action`. The editor reads each value below `DATA`, with either separator and in any case, so `missions\\m01a\\x.thn` and `Missions/M01a/x.thn` name one script. A click on the line shows its section in the Inspector. The tooltip and the menu of the row list all lines. |
| State | Retail (no mod changes the script), Changed by a lower mod (a mod before the active mod adds it, patches it or replaces it), Patched here (the active mod has a THN patch), Replaced here (the active mod has a whole file for a lower script) or New here (the active mod adds the script). A dot after the state marks unsaved edits. |

Filter the list by name or path, or by a category or group of the group menu. The group menu has "All cutscenes", then each category of the cutscene list with its groups below it, and the number of scripts of each. A new cutscene that is not saved is in Mod cutscenes. A click on a column header sorts the list. The commands are in the toolbar and in the menu of a row:

- **New cutscene** (the plus button of the toolbar) makes a new script in the active mod (see "Make a new cutscene").
- **Open** (or a double-click) opens the script in the Cutscene tab and in the Timeline. One cutscene is open at a time.
- **Revert** puts back the script as the mods before the active mod leave it. The next save then deletes the patch of the active mod. Revert is one entry of the edit history. It is not available for a whole file of the active mod, and not when the active mod does not change the script.
- **Copy path** copies the install path, for example `DATA\Scripts\Story\s002xe_nrml_sinclair_li_01_Cityscape_01.thn`.
- **Copy INI value** copies the path below `DATA`, for example `Scripts\Story\s002xe_nrml_sinclair_li_01_Cityscape_01.thn`. An INI line, for example `start_script`, names a cutscene in this form.

A new cutscene shows in the list at once, with the state New here and a dot, also before its save. After the save, its row comes from the game data, as the other rows do.

## Make a new cutscene

1. Click Mod, then click the mod that gets the cutscene. The mod must not be in the OpenLancer repo.
2. In the toolbar of the Cutscenes tab, click **New cutscene**.
3. Type a name. Use only letters, digits, `_` and `-`. The popup shows the install path, for example `DATA\MODS\my-mod\CUTSCENES\bar_shot.thn`.
4. Click **Create**, or press Enter. When Create is off, its tooltip tells why.

The new script is 10 s long. It has a SCENE, a MONITOR and a CAMERA at the origin with the median lens of the retail cameras of the bases, and a cut to the camera at 0. It opens in the Cutscene tab in the context Script only, because no INI line names it yet. New cutscene is one entry of the edit history. An undo takes the cutscene back and closes it. A save writes the script as a whole Lua text file into the `files` folder of the mod. A save after the undo deletes that file.

To play the cutscene in a room, give a room line its INI value: copy it with **Copy INI value**, or pick the cutscene in the picker of the line (see "Data lists and pickers" in [MOD_FORMAT.md](MOD_FORMAT.md)). After the save and the reload of the game data, the context list of the Cutscene tab has the room.

The Console and the engine log get one line for each open, with the counts of the Timeline, for example "2 monitor lanes, 35 cuts (13 on the main monitor)", and the cameras that only an ignored monitor uses.

## The Cutscene tab

The Cutscene tab is beside the Scene tab. It previews the open cutscene script, also in a profile without an active mod. The cutscene list of its toolbar opens a script too (see "The cutscene list"). The preview reads the script with the unsaved edits of the session.

The context list sets how the preview plays the script. The editor suggests the context in which the game plays it:

| Context | Scripts | What the preview builds |
|---|---|---|
| Room | the set, scene, cart, start, landing and launching scripts of a room, the story cutscenes of `[CharacterEncounter]` at its `location`, and the mission ambients of `Act_AddAmbient` | The room as the game builds it: the set script, then the scene scripts of the room, then the script. Each script plays one time. When the script is the set script, a scene script or an ambient, the room plays without a cutscene: the view looks through the `[Camera]` of the room, and the player ship is on the `[PlayerShipPlacement]` marker. Else the script plays as the cutscene, with the fixtures of the room that play its characters. |
| Fidget | the scripts of `[MRoom] fixture`, `[Char] fidget` and the fidget segment of `[GenericScripts]` | A stand-in character at the spot of the fixture plays the `START_MOTION` events of the script, as the game plays a fidget. Without a room, the stand-in stands at the origin. |
| Space | the scripts of `Act_CallThorn` | The script in the star system of its mission line, with the starsphere, the planets, the stations and the lights of the system. The `system` key of the trigger of the line gives the system. Else a condition of the trigger that names a system, a base or a system object gives it. Else the triggers that start the trigger give it. Each system is one row under "In space". The row uses the first line of that system that gives a start, and the tooltip of the row names its trigger. The game makes none of the script objects in space and shows the mission objects in their place. The preview shows the script objects as stand-ins, and the **Script objects** toggle hides them. A `main_object` marker at the origin stands at the start of the main object in the mission data. The static objects near it follow it. A line of the trigger, or of a trigger that starts it, that moves or spawns the main object gives the start. Else the first line of the mission file that spawns it gives the start. Else its `[MsnShip]`, `[MsnSolar]` or `[MsnFormation]` gives it. A ship of a formation starts at the spawn position of the formation. Shot mode uses the planes of the space camera of the game, 3 and 250,000. When no rule finds a system, the preview plays the script alone, and a banner tells how to fix the mission line. The Console tells when the system loads. |
| Script only | each script | The script alone, with the player ship of the Play package. A script with `$terrain_*` templates needs a room, because only the context of a room fills them in. A banner then names the room. |

The player ship is the ship of the Play package of the profile (see [PLAY.md](PLAY.md)).

The view has two modes, the two camera buttons of the toolbar:

- **Shot** shows the active THN camera with the letterbox of the game: the viewport is 1/1.39 of the height of the screen rectangle while a cutscene plays, and the whole screen rectangle when a room plays without a cutscene. The aspect list sets the ratio of the screen rectangle: the ratio of the panel, 4:3 or 16:9.
- **Free** shows the scene through the fly camera of the editor, in the whole panel. The controls are the controls of the Scene, at the scale of a room: 5 m/s at the start and a near plane of 0.05 m. In a space context, the starsphere of the system draws behind all objects with a sky camera at the pose of the fly camera, as in the Scene. Over the scene, a glyph marks each entity of the script, a frustum shows each camera (the active camera of the shot in yellow), and a line shows each motion path. A click selects the entity of a glyph, and a click on empty space selects the cutscene. `F` frames the selected entity. The Names button shows the names of all entities. A script without a camera opens in Free mode.

**Align view to camera** (the eye button) puts the free camera at the pose and the field of view of the selected camera, or of the active camera when no camera is selected, at the playhead. Then Free mode shows what the camera shows, across the width of the view. The frustum of that camera then shows the frame of the shot.

**Set camera from view** (the camera button beside it) writes the pose of the free camera into the start pose of the selected CAMERA (`spatialprops.pos` and `orient`), in one edit. Select a CAMERA entity and use Free mode first. Align view to camera and Set camera from view are the two camera commands of Unity.

In Free mode, the **gizmo** moves and turns the item of the selection, with the tools, the Local and World switch and the snap of the Scene gizmo. The move step of the snap is in meters (0.01 m to 10 m). A cutscene entity has no size, so the scale tool is off. Each drag is one edit, and the drag ends when you release the mouse button.

| Selection | What the gizmo changes |
|---|---|
| An entity | Its start pose: `spatialprops.pos` and `spatialprops.orient`. A MONITOR has no place in the scene. The engine does not make an entity with the REFERENCE flag: it uses the object with the same name that another script of the scene makes, for example the set script of the room. So the gizmo and Set camera from view do not change such an entity, and its Transform rows say why. |
| A START_SPATIAL_PROP_ANIM with one target | The target pose of the event: `spatialprops.pos` and `q_orient` of its props. A rotate always writes `q_orient`, because the engine cannot load the `orient` key of this event. |
| A START_SPATIAL_PROP_ANIM with a second target | The start pose of the entity of the second target. The engine moves the entity to the pose of that target in each frame, so the values of the event only choose which parts move. |
| A MOTION_PATH with a key selected in the Inspector | The key, in the frame of the path entity. The edit writes `path_data` again. |

Events can move an entity later, so the gizmo edits the start pose that W gives, not the pose of the shown frame. While the playhead is not at 0, a ring labeled "start pose" marks that pose, and a line joins it to the entity in the shown frame. The ring follows each frame of a drag.

The play button below the view plays and pauses the preview. The Preview controls are in the toolbar of the Timeline. They have circle icons, so they do not look like Play of the game: play, pause, stop, one frame back and forward, loop, pre-roll and the speed (0.25x to 2x). The loop plays the whole script, or the loop range of the ruler of the Timeline. With a loop range, a play from a playhead outside the range starts at the start of the range. The keys work while the Cutscene or the Timeline panel has the focus: Space plays or pauses, Home and End go to the start and the end, Left and Right go one frame (1/60 s), and Shift with Left or Right goes one second. F5 and Shift+F5 stay Play and Stop of the game. The preview runs while the Cutscene tab or the Timeline shows.

The preview steps at exactly 1/60 s, so a play to a time and a jump to that time give the same picture. A jump forward is fast. A jump back builds the scene again and runs it to the playhead. Until then, the view keeps the last frame, and the scrub bar shows the progress. A play from a scrubbed time starts with a pre-roll of 3 s at four times the speed, so that the particle effects that started before the playhead show as in the game. A jump to the start of the loop range starts with a pre-roll too. The scrub bar shows the first 600 s of a long script. The time field takes any time.

A play waits while the scene builds again or runs to the playhead, for example at the start of a pre-roll or of a loop. Then the play goes on from the frame that the view shows.

The preview plays the START_SOUND events of the open script in 2D, as the Unity Game view plays audio. The **Sound** toggle after the speed list turns the sound on and off, as Mute Audio of Unity does. The editor keeps the toggle in its settings, and it is on by default. The sound plays only while the preview plays forward: not during a pre-roll, a catch-up or a drag of the playhead. A play from the middle of a sound starts the sound at that time, also after a pause. The pitch follows the speed, so the sound stays in time with the picture.

A START_SOUND without `props.duration` plays nothing, as in the game. A voice line (`VoiceProfile_<line>` with a `userprops.speaker`) plays with the voice of the speaker in the scene. When the scene gives the speaker no voice, the preview takes the first voice of the game data that has the line, then `trent_voice` for the player, then `rvp146`. The Console tells which voice the preview took, and it names each sound that the game data does not have, one time.

When the script has no camera, the view shows only "This script has no camera". A problem of a build shows as a banner with the message of the engine, and the view keeps the last frame.

The readout button shows the build time, the step time and the steps of each frame. The editor option `--bench-cutscene <install path>...` opens the profile of the command line without an active mod, previews each script to its end (at most 600 s), and then jumps back to the middle of that time. The first build loads the models, and the build of the jump back finds them loaded, as each jump back and each edit does. The bench writes the times of both builds and catch-ups and the active camera to the log, and closes. A wrong command line of the bench gives the exit code 2, and a script that fails gives the exit code 1. The bench does not change the editor settings. As each start of the editor, it replaces the engine log of the editor, `%LOCALAPPDATA%\OpenLancer\logs\OpenLancer.Editor.log.txt`, so copy that file first when you need the log of your last session. While another editor runs, the bench writes `OpenLancer.Editor.log.0.txt`.

A wrong command line of a normal start, for example two profile files, opens the editor without a profile. The Welcome screen shows the problem.

### The cutscene list

The cutscene list at the left of the Cutscene toolbar puts each script in one category and one group. The categories and groups come from the paths and from the INI files of the game data:

| Category | Scripts | Groups |
|---|---|---|
| Intro scenes | The scenes behind the main menu: `DATA\SCRIPTS\INTRO` and the scene scripts of the start rooms of the intro bases. | None. |
| Main story | `DATA\SCRIPTS\STORY` and the folders of the story missions. | One group for each story mission, in story order. A script goes to the first mission whose folder has an INI file that names it, else to the mission of its folder. The rows follow the triggers of the mission file, so Mission 1 starts with the game opening. |
| Bases and rooms | `DATA\SCRIPTS\BASES`. | The region of the bases that play the script, for example Liberty. A tie goes to the region whose prefix starts the file name. Also "Three or more regions" and "Not used by a room". |
| Bar conversations | `DATA\SCRIPTS\GCS`. | The gender and the posture of `genericscripts.ini`, for example "Male, sitting", and "Not in the conversation list". |
| Characters | `DATA\SCRIPTS\EXTRAS` and `DATA\SCRIPTS\VENDORS`. | Generic fidgets and Vendor fidgets. |
| Random missions | `DATA\RANDOMMISSIONS`. | None. |
| Mod cutscenes | The new cutscenes of the mods in `DATA\MODS`, also the new cutscenes that are not saved. | One group for each mod. |
| Other | All other scripts. | The first folder below `DATA`. |

1. Click the cutscene list. The list opens with the open cutscene highlighted in its open group. The first time, Intro scenes, Main story and Mission 1 are open.
2. To open or close a category or a group, click its header. Each header shows the number of its scripts.
3. To read what a category holds, put the mouse on its header or on its info icon.
4. To find a script, type in the filter field. The filter searches all categories. It matches the file name, the path, the bases and the systems that play the script. It also matches the labels and the details of its category and group, for example `liberty`, `opening` or `menu`. The headers then show the number of matches and the total, for example "3 / 205".
5. To move the highlight, press Up or Down.
6. To open the highlighted script, or to open or close the highlighted header, press Enter.
7. To close the list, press Escape.

The filter looks for each word of the text by itself. So `mission 3` also finds the rows of other missions that have a 3 in their path.

A row with a dim glyph and the dim detail "not used" is a script that no INI line names. A row with the detail "new" is a new cutscene that is not saved. A click on the open cutscene only closes the list, so the preview keeps its context and its playhead.

## The Timeline tab

The Timeline tab shows the events of the open cutscene on the time of the script. With an active mod, it also edits them. When the cutscene takes no edits, for example without an active mod, the toolbar of the Timeline shows "Read-only", and its tooltip tells why.

- The **ruler** shows the seconds and the end of the script. A drag on the ruler moves the playhead. A drag back builds the scene again when you release the mouse button. A thin bar shows the progress of the build. `Shift` and a drag on the ruler set the **loop range** and turn the loop on. Then the loop plays only that range. `Shift` and a click on the ruler clear the range.
- The **Cameras** group has one lane for each MONITOR. The engine sorts the monitors by `userprops.Priority` and uses only the first one. The lane of that monitor is the main lane: each cut is a diamond, and each shot is a segment with the name of its camera. The other lanes are grey, because the engine ignores their cuts. A camera that only an ignored monitor cuts to has an information mark, unless another event names it. For example, in s002xe a START_SPATIAL_PROP_ANIM moves `Cam_02_Intro_Trent_A` to the pose of `Cam_02_Intro_Trent_B`, so the pose of `Cam_02_Intro_Trent_B` matters, and it has no mark.
- Each **entity** has one row, in the group of its type: Cameras, Characters, Props, Lights, Effects, Sounds, Markers, Paths, Scene and Other. An event is on the row of its first target. An event whose first target names no entity of the script is in the group "Not in this script". The filter button hides the rows without events. A click on a group header opens or closes it.
- An event with a length is a **bar**, with its type and its second target. The points of its `param_curve` are ticks at the bottom of the bar. An event without a length is a **diamond**. The tooltip of an event gives its times, its targets and its notes.
- When bars of one row overlap, the row gets **sub-rows**, so no bar hides another bar. A bar takes the first sub-row where no bar overlaps it. Bars that only touch share a sub-row. An edit, an undo or a redo moves only the bars that it changes. During a drag, each bar stays in its sub-row. When you release the mouse button, the bars pack again. The diamonds stay on the first sub-row. Diamonds that overlap at the zoom of the view show as one diamond with a count. Its tooltip lists all of them, a click selects the last one in the file, and a box selects all of them.
- An event that starts **after the end** of the script has the warning color. Its tooltip gives the gap and whether a steady 60 frames per second runs it: the game runs an event only when its last frame passes the time of the event. The preview stops at the end, so it does not run such an event.
- An event that the game does not run in the context of the preview is **grey**: the events of a set script, the events of a fidget other than START_MOTION, and the cuts of an ignored monitor.

A click on an event selects it, and a double-click also moves the playhead to it. `Shift` and a click add an event to the selection or remove it. A drag on empty track space selects the events in the box. A click on a row selects its entity. `F` shows the selection on the timeline. `Ctrl` with the wheel zooms, and `Shift` with the wheel or the middle mouse button moves in time. The timeline shows at most 600 s at once.

The edits of the Timeline:

- A **drag of a bar or a diamond** moves the selected events in time. A drag of the **start of a bar** moves its time and keeps its end. A drag of the **end of a bar** changes its length (`props.duration`). A drag of the **end handle** of the ruler changes the `duration` of the script. Each change is a whole number of milliseconds, the grid of all retail times. With `Ctrl`, a moved edge snaps to the playhead and to the start and the end of the other events. `Esc` cancels a drag. Each drag is one edit, and it ends when you release the mouse button.
- `Delete` deletes the selected events. `Ctrl+C` copies them. `Ctrl+V` pastes them at the playhead: the earliest event starts at the playhead, and the others keep their offset. When an entity is selected, the events of the row of the copy move to that entity. The pasted times are on the 1 ms grid. `Ctrl+D` adds a copy of the selected events right after them, as Duplicate of Unity does: the earliest copy starts at the end of the event that ends last, and the copies keep their offsets. Events without a length move their copies one frame (17 ms) later. The copies take the selection, so that a drag moves them.
- The menu of a row (a right click) has **Add event** with the event types whose first target fits the entity, at the time of the click. A new event has the usual props of its type, for example START_MOTION with `animation`, `duration` and `time_scale = 1`. A new animation of props, for example START_SPATIAL_PROP_ANIM or START_CAMERA_PROP_ANIM, has an empty record of its props. The engine changes only the values that such an event has, so the new animation changes nothing until you set a value. An event that needs a second target gets the first entity that fits it, for example the first CAMERA for a SET_CAMERA on a monitor lane. The second target is never the entity of the row, an entity that a later entity with the same name hides, a MONITOR or the SCENE entity. Retail uses a MONITOR and the SCENE entity only as the first target of SET_CAMERA and START_FOG_PROP_ANIM. The menu also has **Paste here** and **Delete entity...**. When events name the entity, a dialog lists them and offers **Delete with events** or **Cancel**.
- The menu of an event has Copy, Duplicate, Delete and **Move before**. The engine runs the events of one time in the order of the file. Move before puts the event before another event of the same time. In a patch, the events after it become a delete and a new event, so Move before is off, with the reason, when such an event has a key that repeats in the lower cutscene.

While the Cutscene tab shows, the Hierarchy shows the open cutscene: the cutscene row, then the entities in the groups of the Timeline. A `t:` word of the search selects a type, for example `t:camera`. A double-click on an entity frames it in Free mode. The Scene tab and the Cutscene tab each keep their own selection.

## The Inspector of a cutscene

The Inspector shows the selected entity or event, the selected events, or the cutscene page when the cutscene is selected: the state, the duration, the counts, the monitors, the lines that use the script and the scripts of the preview. With an active mod, its rows edit W, and each edit is one entry of the edit history. A blue label and a bar mark a value that differs from the lower cutscene. The menu of a label has **Revert to the lower cutscene**, **Remove** and **Copy value**. The tooltip of a label names the engine lines that read the key.

- An entity shows the rows of its type, in the groups of its records: Transform, Camera, Light, Sound, Character, Path, Effect and User props. An event shows its time, length, type and targets, then the rows of its type. The time and the length change as a drag on the Timeline changes them: on the 1 ms grid, and not below 0. A key that the editor does not know shows in **Other keys** as Lua text, and the evaluator reads a new text. A key that the engine does not read has a note, for example `No_Fog`. The engine reads the `userprops` keys without letter case, so a row edits the spelling that the entity has.
- A number shows the shortest text that gives the same float. A text that is not a number, or a number that is not finite, keeps the last valid value and shows the error. A position has an X, a Y and a Z field. A rotation matrix (`orient`) shows as Euler angles in degrees.
- A drag on the X, Y or Z label of a vector or of an `orient` row changes that value, as in the rows of a star system (see "Edits in the Inspector" in [MOD_FORMAT.md](MOD_FORMAT.md)). The step for each point comes from the largest value of the vector: `pos = { 12.5, 3.2, -40 }` gives 0.1. The `atten` of a light takes the step from the dragged value, and an angle of `orient` has steps of 0.5 degree. `Shift` gives steps of 0.1 times the step, and `Ctrl` gives steps of 10 times the step. One drag is one entry of the edit history, and `Esc` or the right mouse button cancels it. The point of a key of a motion path has the drag too. A quaternion (`q_orient`, the orientation of a path key) and the points of a curve have no label drag. A row needs a value of three numbers for a drag.
- Seven color rows have a swatch at the right of their fields. A click on it opens the color picker. The `diffuse` and `ambient` of a LIGHT and of a START_LIGHT_PROP_ANIM keep numbers from 0 to 1 with 3 decimals, because the engine applies them as they are. The `ambient` and the `fogcolor` of a SCENE, and the `fogcolor` of a START_FOG_PROP_ANIM, keep whole numbers from 0 to 255, because the engine divides them by 255. The engine does not read the `fogcolor` of a SCENE, so a pick there does not change the preview. One opening of the picker is one entry of the edit history. A click outside the picker keeps the color, and `Esc` puts back the color of the opening. A channel that you do not change keeps its value, for example `0.784314`.
- **Flags** of an entity are checkboxes: REFERENCE, LIT_DYNAMIC, LIT_AMBIENT and HIDDEN, and SPATIAL in place of LIT_DYNAMIC on a SOUND. A stored number stays a number, one flag from a name or from no key is the name of the flag, and else the value is the number that the engine reads. The flags of ATTACH_ENTITY, START_PATH_ANIMATION and START_SOUND, and the `event_flags` of START_MOTION and START_IK, are checkboxes with the same rules. An ATTACH_ENTITY without `flags` runs with POSITION and ORIENTATION, so its checkboxes show these two, and a click adds a bit to them or removes one. A THN name, for example `target_type`, is a list. A target of an event is a list of the entities that fit its role, with a button that selects the entity.
- START_CAMERA_PROP_ANIM reads its keys by place, not by name. So the rows say what each key sets: `aspect` sets the near plane, `near` the far plane, and `far` the aspect. When the second target of the event is a CAMERA, the engine takes the end values from that camera, and a note says so. A START_SPATIAL_PROP_ANIM with a second target has a note and a link that selects that target. The engine cannot load the `orient` key of a START_SPATIAL_PROP_ANIM, so a script with it does not play. Its row says so. Use `q_orient`.
- The **curve** of an event shows as a graph of its value over the length of the event, as the engine computes it. A drag of a point of the graph is one edit. The time of a point stays between the times of its neighbors. A row for each point edits its time, value and slopes.
- The **path keys** of a MOTION_PATH show as a row for each key, with its point and its orientation. An edit writes the whole `path_data` again. A path that no edit changed keeps its text. **Select for the gizmo** gives a key to the gizmo of the Cutscene view.
- A new **name** renames the entity, the targets of the events that name it and the `userprops.speaker` values that name it, in this file. When other data uses the name, a dialog lists the uses first and offers **Rename in this file** or **Cancel**: the `[Camera]`, `[CharacterPlacement]`, `[PlayerShipPlacement]` and `[ForSaleShipPlacement]` names of the rooms whose set or scene script is this file, the `[MRoom] fixture` spots of those rooms, and the REFERENCE entities with the name in the other scripts of those rooms. A rename never changes other files.
- Two or more selected events show "N events selected", a time shift and **Delete the events**.
- A SOUND entity has the row **Preview**. Play plays the sound of its template once from its start, in 2D, with the voice rule of the preview. Stop stops it.
- The cutscene page has the section **Problems** when the script has errors that the lower cutscene does not have. The game crashes when it plays such a script, so these errors stop Save and Play. A click on an error selects its element. The state of a new cutscene says "OpenLancer only until the M11 check".

### The animation of a character

When the first target of a START_MOTION event is a character (a DEFORMABLE entity), the `animation` row has a list of the animation clips. The clips are the scripts of the `.anm` files that the `[Animations]` lines of bodyparts.ini name. For a name that two files have, the list has the clip of the last file. The game also plays the clip of the last file.

A COMPOUND plays the scripts of its own model, so the row of a COMPOUND keeps its text field. A target that names no entity of the script also keeps the text field. When the editor cannot read the clips, the row keeps its text field, and the note of the row gives the reason.

The list shows the clips of the skeletons of the costume of the target first, under "Skeletons of" and the name of the target. The other clips follow under "Other skeletons". The skeletons come from the `[PetalAnimations]` and `[Skeleton]` sections of bodyparts.ini. A clip name that two files have belongs to the skeletons of both files.

The search finds a clip by its name or by its label. **Body part** shows only the clips of the body, the head or the hand skeletons. Its value "Other" shows the clips of the files that no skeleton lists.

A label, for example "Walk", comes from a word in the clip name. A word counts only at the start of a part of the name in capitals, so a label can be missing or wrong. Look at each clip in the clip preview before you use it.

The clip preview at the right of the list plays a clip in a loop. It plays the last clip that the mouse was on, or the clip that Up or Down selected. The character is a copy of a character of the cutscene preview, so the clip preview needs the cutscene preview. **Preview on** selects the character. At first, it selects the target of the event. A head or a hand clip plays over a standing idle of the body, so the body does not keep its bind pose.

The clip preview has the controls of the Unity animation preview. A drag with the left or the middle mouse button pans the view. A drag with the right mouse button, or Alt with a left drag, orbits the view. The wheel, or Alt with a right drag, zooms. Shift makes the orbit and the zoom faster. F puts back the pan and the zoom, and **Reset view** also puts back the orbit.

To change the animation of a character:

1. Select the START_MOTION event.
2. Click the `animation` field. The list opens, and the search field is active.
3. To see a clip in the clip preview, put the mouse on it.
4. Click a clip, or press Up or Down. The event gets the clip at once, and the cutscene preview shows it.
5. To keep the clip, press Enter or click outside the list. To put back the clip of the opening, press Esc.

One opening of the list is one entry of the edit history. A click on the current clip makes no change. While **Body part** or **Preview on** is open, Esc closes only that dropdown. **Preview** plays the cutscene preview from the time of the event. It also works when the row is read-only.

When the event names a clip that the game does not have, the field shows "Missing (Animation)" in the warning color. The warning below the field says: "The game has no animation with this name. The character does not move."

## Edit a cutscene

1. Click Mod, then click the mod that gets the edits. The editor writes only into this mod.
2. Open the cutscene in the Cutscenes tab.
3. Change the cutscene: drag an event on the Timeline, type a new value in the Inspector, or move an entity with the gizmo in Free mode. Each change is one entry of the edit history.
4. Press Ctrl+S. The Console shows the save, for example "Saved mod 'my-mod' (save 1): 1 file written, 0 files deleted." and "Wrote patches\DATA\Scripts\Story\s002xe_nrml_sinclair_li_01_Cityscape_01.thn".
   - If an edit gives an error that crashes the game, the save writes nothing. The Console line starts with "The game crashes when it plays this cutscene." and names the element and the key. Click the line to select the element. Correct the value or undo the edit, then save again.
5. Wait for the validation. The Console shows "Validation OK for save 1: no errors, 0 warnings that the mod adds.", or the errors of the save.

The patch holds only the values that the edits change. A save keeps the bytes of a patch that the edits do not change, and it deletes a patch that changes nothing after the edits. "What stays in a patch file" in [MOD_FORMAT.md](MOD_FORMAT.md) gives the rules. The game plays the patched cutscene after `oltool deploy`. Play shows it without a deploy (see [PLAY.md](PLAY.md)).

## Keys

| Key | Where | Effect |
|---|---|---|
| Space | Timeline or Cutscene tab | Play or pause the preview. |
| Home, End | Timeline or Cutscene tab | Go to the start or the end of the script. |
| Left, Right | Timeline or Cutscene tab | Go one frame (1/60 s) back or forward. |
| Shift+Left, Shift+Right | Timeline or Cutscene tab | Go one second back or forward. |
| F | Timeline | Show the selected events. |
| F | Cutscene tab, Free mode | Frame the selected entity. |
| Delete, Ctrl+C, Ctrl+V, Ctrl+D | Timeline | Delete, copy, paste at the playhead, and duplicate the selected events. |
| Ctrl+wheel, Shift+wheel | Timeline | Zoom, and move in time. |
| Esc | During a drag on the Timeline or of the gizmo | Cancel the drag. |
| Drag a label, with Shift or Ctrl | X, Y or Z label of a row of the Inspector | Change the value: normal, fine or large steps. Esc or the right mouse button cancels the drag. |
| Esc | Color picker of the Inspector | Put back the color of the opening of the picker. |
| Up, Down, Enter, Esc | Clip list of the `animation` row | Set the previous or the next clip, keep the clip and close the list, or put back the clip of the opening. |
| Drag, middle drag | Clip preview | Pan the view. |
| Right drag, Alt+drag | Clip preview | Orbit the view. |
| Wheel, Alt+right drag | Clip preview | Zoom the view. |
| Shift | Clip preview, during an orbit or a zoom | Orbit 3 times faster, or zoom 4 times faster. |
| F | Clip preview, when the mouse is on it and no text field is active | Put back the pan and the zoom. The orbit stays. |
| Right mouse button with W, A, S, D, Q and E | Cutscene tab, Free mode | Fly the free camera. |
| W, E, X | Cutscene tab, Free mode | Select the move or the rotate tool of the gizmo, and change between Local and World axes. |
| Ctrl+Z, Ctrl+Y, Ctrl+S | Each panel | Undo, redo and save. |
| F5, Shift+F5 | Each panel | Play and Stop of the game, not of the preview. |

The keys of the preview work only while the Timeline or the Cutscene tab has the focus and no text field is active.

## How the preview differs from the game

| Preview | Game |
|---|---|
| Steps at exactly 1/60 s. So a play to a time and a jump to that time give the same picture. | Steps with the time of each frame. So two plays in the game can differ a little. |
| Plays the sounds and the voice lines of the open script in 2D. A voice line without a voice in the scene takes the first voice of the game data that has the line. | Plays the sounds at their positions, with the sounds of the other scripts of the room. A voice line takes the voice of the NPC that the game binds to the speaker. |
| Shows no patrons in a room. | Puts random patrons into a room. |
| Builds a room without the ambient scripts of a mission (`Act_AddAmbient`), unless the open script is one of them. | Adds the ambient scripts of the active mission to the room. |
| Plays the script of a mission trigger (`Act_CallThorn`) in the saved star system of the mission line, and makes its entities as stand-ins for the mission objects. The main object stands still at its start in the mission data. Banners tell the differences. | Plays the script in space with the objects of the mission. The main object moves with its mission orders. |
| After a jump, a particle effect that started before the playhead shows its first state, and the tag "Effects start at the playhead" shows. A play with pre-roll shows the effect as the game shows it. | Shows each effect at its age. |
| Stops at the end of the script, so it does not run an event after the end. | Runs such an event only when the time of its last frame passes the time of the event. |
| Plays the goods cart scripts and the GCS scripts other than the fidgets. | Does not play them. So the game cannot show an edit of such a script. |
