# Play in the editor

Play starts the game from the editor with the edits of the active mod. The game reads the profile through the mod overlay, as the editor does. It needs no deploy. While the game runs, a save of the editor reloads the game, and the player continues at the same place.

The game is a second program, `OpenLancer.Play.exe`, next to the editor. The build of the editor builds it too.

Play in the game plays the mods in your own copy of the retail game, and the test server hosts them on FLServer. See "Play in the game" and "The test server" at the end of this page.

## The Play controls

The main menu bar has a Play menu after the Mod menu. Three buttons in the middle of the bar do the same as the menu: Play, an arrow that opens the Play menu, and Stop.

| Control | Key | Effect |
|---|---|---|
| Play button, or Play > Play from camera | F5 | Plays at the Scene camera. While a game runs, the game closes and a new game starts. In the Mission tab with an open mission, F5 and the Play button play the mission from its play start (see "Play a mission"). |
| Play > Play at base | | Plays docked at the base that you click. |
| Play > Play mission | | While a mission is open in the Mission tab: plays the mission from its play start, from the mission start or from a checkpoint. "Restart the mission" starts a running mission play again (see "Play a mission"). |
| Play > Ship: ... | | Shows the ship of the profile. It is not a command. |
| Play > Use the default ship | | Sets the ship of the profile back to the default. |
| Play > Play in HD Edition 0.7.1, and one item for each other linked game with its game program | Ctrl+F5 | Plays the profile in the play copy of that game (see "Play in the game"). Ctrl+F5 plays in the game of the last Play in the game, or in the first game. |
| Play > Host test server | | Starts FLServer of the play copy of a game with the mods (see "The test server"). |
| Play > Join test server | | Starts the game of the play copy of the test server. |
| Play > Stop test server | | Stops FLServer. It becomes Force stop test server after the first click. |
| Play > Reload game | | Reloads the running game without a save. |
| Stop button, or Play > Stop | Shift+F5 | Stops the game. Stop becomes Force stop when the game does not close. |

F5, Ctrl+F5 and Shift+F5 do not work while a text field or a dialog is open. A click on the Play button works also while a field of the Inspector is open: the click ends the field, and the save of Play has its edit. The tooltip of a control that is not available tells why.

## Play from camera

1. Open a profile and a system.
2. Move the Scene camera to the start place. The ship starts at the camera and points where the camera looks.
3. Press F5, or click the Play button in the middle of the main menu bar. The Play menu has the same command: Play from camera.

Play then does these steps:

1. Play saves the edits of the active mod. The Console shows the save.
2. The game window opens at once, and the game loads its data. At the same time, the editor validates the save.
3. When the validation finds no errors, the ship spawns. The Console shows the place, the ship and the time from Play to flight, for example "Playing in New York (Li01) with Starflier ge_fighter. From Play to flight: 3.9 s."

Validation errors stop Play. Warnings do not stop it. Only the problems that the active mod adds count. A problem that vanilla or a lower mod has does not stop Play. When the validation cannot run, Play goes on with a warning.

Play loads the base data and the mods up to and with the active mod. These are the mods that the editor shows. When the profile has mods after the active mod, the Console names them. Without an active mod, Play loads all mods.

### What Play saves

| Case | What Play does |
|---|---|
| The active mod has unsaved edits | Play saves them, with all checks of Ctrl+S. When the save does not complete, Play stops. |
| No mod is active | Play saves nothing and plays the saved files of all mods. |
| Another editor has the mod open, or a recovery file waits | Play saves nothing and plays the saved files of the mod. The Console tells why. |
| The mod is in the OpenLancer repo, and you did not allow its edits | Play saves nothing and shows no dialog. It plays the saved files of the mod. |

### The start check

The camera can be inside a planet, a station or a death zone. A death zone is a zone with 1,000 or more damage per second, for example the zone around a sun. Then the start moves along the view axis to the nearer exit, plus 100 units. The Console tells how far the start moved, ahead of the camera or behind it.

A zone with less damage, for example a radiation cloud, does not move the start. The Console warns when the ship starts in such a zone.

The check knows the objects and the zones of the system. It does not know mines, asteroids or other ships.

The check reads the Scene. So Play first lets the Scene show each edit, also while another tab shows. A new model of an edit can wait for the load of the game data. Then Play waits for the end of the load, and the Console tells why.

## Play at base

Open the Play menu and select Play at base. The list shows the bases of the open system first. The other bases are in one submenu for each system. Click a base. The game starts with the ship docked at that base. Play at base needs no open system.

## The ship

The default ship is the ship of a new multiplayer character. In vanilla, this is the Starflier (`ge_fighter`) with the loadout `MP_ge_fighter` and 2,000 credits.

To fly another ship:

1. Open the Database and click Goods.
2. Right-click a row of category `ship`. Such a row is a ship package.
3. Click Use for Play.

The Play menu shows the ship of the profile, for example "Ship: Starrunner". To go back to the default ship, click Use the default ship in the Play menu. Each profile keeps its own ship.

The equipment of the package goes on its hardpoints. An item with an amount above 1 goes into the hold. A good that the engine cannot mount, for example a battery, also goes into the hold.

## In the game

A character of a play is an admin. These chat commands work:

| Command | Effect |
|---|---|
| `/pos` | Shows the position of the ship. |
| `/warp x y z` | Moves the ship to the position. |
| `/base <nickname>` | Docks the ship at the base. |
| `/godmode` | Makes the ship take no damage. |

The game window shows the debug view. Its Debug window shows the position of the player.

## The status of the game

The status bar of the editor shows the state of the game:

| Text | Meaning |
|---|---|
| Play: starting... | The game loads its data, or the ship spawns. |
| Play: validating save 2... | The game waits for the validation of the save. |
| Playing: New York (Li01), save 2 | The player is in the game. The game shows the files of save 2. |
| Playing: New York (Li01), parts of save 3 | Save 3 came while the game read the mod files. The game can show parts of save 3 and parts of the save before it. A reload follows when the validation of save 3 passes. |
| Play: reloading save 3... | The game loads the files of save 3 again. |
| Play: the game does not respond | The game did not load its data in 60 s, or it did not end a reload in 30 s. |
| Play: stopping... | The game closes. |

## Stop

Press Shift+F5, or click the Stop button. The game closes. The Stop button of the banner of a mission play does the same. When no OpenLancer game runs, Stop stops the game of the play copy (see "Play in the game").

When the game does not close in 5 s, the Stop button becomes Force stop. Force stop ends the game process at once. When the game does not respond, Stop is Force stop at once.

Press F5 while the game runs to play again. The game closes, and a new game starts at the new camera place.

The game also closes in these cases:

- You close the profile.
- You change the active mod, or you make a new mod.
- You close the editor. The game gets 2 s to close, then Windows ends it.
- The editor ends in another way, for example by a crash. Windows then ends the game too.

## The reload

A save of the editor reloads the running game:

1. Press Ctrl+S.
2. The editor validates the save, as after each save.
3. When the validation finds no errors, the game shows its loading screen for less than 1 s.
4. The player continues in the same system, at the same place, with the same heading. At a base, the player is docked at the same base.

The Console then says, for example, "The running game shows save 3 (reload 0.6 s, 3.4 s from the save)." The first time is the time of the reload in the game. The second time is the time from the save.

When the validation finds errors, the game does not reload. The Console says which save the game keeps, for example "The running game keeps save 2. The validation of save 3 found 1 error."

When the validation cannot run, the reload goes on with a warning, as Play does.

A reload runs only while the player is in space or at a base. A save before the spawn reloads the game after the spawn.

One reload runs at a time. Saves during a reload wait. When the reload ends, the game reloads the last save. The older saves drop.

### Reload game

Click Play > Reload game to reload the game without a save. Use it after you change a mod file outside the editor. Reload game loads the last save also when its validation found errors.

Reload game is not available while the last save is not complete, because the game cannot load the files of such a save. Correct the problem of the file and save again. That save reloads the game.

### When a reload fails

The game keeps its data and continues at the same place. The Console gives the reason. These are the usual reasons:

- The new data does not have the system, the base or the ship of the player. For example, you renamed the ship of the player in the Database. Correct the data and save again, or press F5 to play again.
- A save of the mod is not complete. Correct the problem of the file and save again.
- The game left the play, for example with Quit to menu or Load game. Stop the game and press Play.

The editor does not send a failed reload again by itself. The next save, or Reload game, sends a reload.

### Saves while the game reads the mod files

The game reads the mod files while it loads and while it reloads. A save in that time can give the game parts of two saves. The editor knows this. After the load or the reload, the game reloads the last save when its validation passes. When the validation finds errors, the Console says "The running game can show parts of save 3. The validation of save 3 found 1 error."

## What needs Stop and Play

A reload loads all INI data and the texts of the mods again. Some data loads only at the start of the game. For a change to such data, stop the game and press Play.

| Data | After a reload |
|---|---|
| INI data: system objects, zones, lights, ships, equipment, loadouts, goods, prices, markets, factions, encounters and missions | New. A new price shows in the shops after the reload. |
| Texts of mods (`strings.json`) | New, also in the user interface of the game. |
| A changed cutscene (`.thn`) file that the mod had at the last load or reload | New, also without a reload. The game reads the file each time it plays the cutscene. |
| A cutscene (`.thn`) file that is new in the mod, also the first change of a vanilla cutscene | New after a reload. The game makes the list of the mod files when it loads them. |
| Models, textures and `.sur` files | Old. Stop and Play. |
| Sounds | Old for a sound that the game played before. Stop and Play. |
| Fonts, the key map, the cursors, effect shapes and character animations | Old. Stop and Play. |
| The interface code, its stylesheet and its resources | Old. Stop and Play. |

The editor writes only INI patches, cutscene (`.thn`) files and `strings.json`. So each save of the editor needs only a reload.

A reload keeps what a save keeps: the position, the cargo, the reputation, the story and the active mission triggers. Ships that a mission made come again from the triggers, so they can differ from the ships before the reload. A trade lane or a jump does not continue after a reload.

Equipment of the player that the new data does not have goes away without a message. When the new data makes a mounted good combinable, that good goes into the hold, because the engine cannot mount it.

## The play folder

Each profile has a play folder next to the profile file: `<profile folder>\<profile name>.play\`.

| File or folder | Content |
|---|---|
| `lancer.ini` | The game settings of the play. The game options write it, so the volumes and the graphics options stay between plays. The game always starts in a window with the debug view. |
| `saves\` | The saves of the player, `AutoSave.fl` of the reloads, and the files of the scripts. |
| `start\start.fl` | The start save of the last mission play (see "Play a mission"). |
| `start\layer\` | The test layer of the last mission play: a mod folder with the setup triggers of an approximate start. It is not a mod of the profile, and an export never holds it. |

The main menu of the game is not part of a play. When you quit to the menu, the menu lists the saves of `Documents\SavedGames\OpenLancer`, and New Game saves there. The key map is in that folder too.

## Play a mission

A mission play starts a story mission at a point of its script: the mission start, a checkpoint or any trigger. The Mission tab sets the point. [MISSIONS.md](MISSIONS.md) tells how, in "Play from a trigger", and it also tells about the live view of the graph. This section tells what Play does.

Play saves the edits of the active mod first, as F5 does. Then the editor makes the start plan from the saved script. It writes the start save `start\start.fl` and the test layer `start\layer\` into the play folder. The game loads the start save, and the mission starts at the point of the start. The other steps are the same as for Play from camera.

The player starts at the start place of the Play start page. The suggestion is where the player is at that point of the mission in the game. That is a base or a point in space. "How the editor suggests the start place" in [MISSIONS.md](MISSIONS.md) tells the rules.

For an approximate start, the setup triggers make only the world of the start system. The ships that travel with the player start next to the player. The setup starts the triggers that wait at the start after it makes the world. So their lines find the ships of the world. "How the setup builds the world" in [MISSIONS.md](MISSIONS.md) tells the rules.

The Console then names the play, for example "OpenLancer plays M02 from Checkpoint t_restore." For an approximate start, a second line says that setup triggers rebuild the state of the path. When the start cannot start, the Console says why. An example is "Play from here is not available: This mission script has no story slot."

A start in space at a point of the plan gets the start check of Play from camera when the Scene shows its system. The start moves out of a planet or a station, and the Console warns about a death zone. When the Scene shows another system, Play checks the start point only for the damage zones of the game data. So the start also moves out of a death zone. The Console then warns that a planet or a station can hold the start point.

OpenLancer can start only the scripts of the 14 story slots. A start in the linked game (Ctrl+F5) comes with a later version.

### A save during a mission play

During a mission play, a save whose validation passes starts the mission again at the start of the play. The game does not reload at the place of the player. So you can change a mission and test it again from the same point.

1. Press Ctrl+S.

The editor validates the save, as after each save. Then it makes the start plan again from the new script. The game loads the data of the save and starts the mission again. The process stays, so this takes about the time of a reload.

The Console says, for example, "Save 4 changed the files of the mission play. The game starts M02 again from Checkpoint t_restore." The start of the play changes only when the game started the mission again.

When the editor cannot make the start again, for example because a new edit breaks the path of an approximate start, the editor sends nothing. The running game keeps its save and its mission. The Console says "The game cannot start M02 again." with the reason.

When the game cannot start the mission again, it goes on with its old data from its autosave. In space, the autosave keeps only the last checkpoint of the mission. So the mission goes back to its last checkpoint, or to the mission start when no checkpoint ran. The Console tells it.

F5 during a mission play starts the game again at the play start of the open mission. When that start cannot start, the save of F5 still starts the running play again. A play from the camera or at a base still closes the game first.

Play > Play mission > Restart the mission starts the game again at the start of the running play. It does not save, so the game uses the saved files.

Play > Reload game during a mission play loads the mod files and the test layer again. The game then continues at the place of the player, as after each reload.

When you rename the trigger of the start, the start of the running play follows the rename, also its Undo and its Redo. When the trigger of the start is not in the script any more, the game plays the mission start, and the Console tells it. Only F5, the Play button of a node and "Play from here" then make the play start the mission start again.

### Agent Play

An agent of the agent server can start Play with the tool `play`, at a point in space or at a base. The tool `play_stop` stops each kind of play, as Shift+F5 does. A later version of the agent server adds the tool `mission_play` for a mission play. [MCP.md](MCP.md) tells about the tools. A request of an agent never saves on its own:

| Case | What the editor does |
|---|---|
| The active mod has no unsaved edits | Play starts at once. |
| The active mod has unsaved edits | The dialog "An agent wants to start Play. Mod '{id}' has unsaved edits. Save them and continue?" opens. "Save and continue" saves and plays. "Cancel" gives the agent the error "The user did not save. Play did not start." Cancel is the default button, so Enter does not save. |
| The setting "Auto-save and continue when an MCP agent starts Play" is on | Play saves and starts without the dialog. The setting is in Agent > Agent settings. It is off by default. |
| The active mod gets unsaved edits after the request, before the save of Play | Play does not save and does not start. The agent gets an error, and a new request opens the dialog. |
| Another play runs, for example a play from the camera | Play does not start. The agent must stop the other play first with `play_stop`. The editor checks it again after "Save and continue". |
| A mission play runs | A mission play of an agent starts the game again at the start of the request. `play` does not start, and the agent must stop the mission play first. |
| The start cannot start | Play does not start. The agent gets the reason of the start plan of a mission play, or of the checks of the Play menu. |
| The call of the agent ends before you answer, for example at its time limit | The dialog closes as Cancel does. A click after the end of the call does not start Play. |
| The dialog of another agent request is open | Play does not start. The agent gets an error and waits for the result of the other request. |
| You clicked "Save and continue" for another agent request, and Play did not end that request yet | Play does not start. The agent gets an error and waits for the result of the other request. |

An agent request plays the start of the request. It does not change the play start of the mission. Your own F5 and Ctrl+F5 always save first, as before.

## When the game stops by itself

The Console tells why the game stopped:

| Exit code | Meaning |
|---|---|
| 0 | The game ended normally, for example with Exit in the game. The Console says "The game closed." |
| 1 | An error ended the game. |
| 2 | The editor gave wrong arguments. Report this as a bug. |
| 3 | The data cannot start the play: merge errors, a save that is not complete, or an unknown mod, system, base or ship package. |
| 4 | The save or the server stop of a reload did not end in 10 s. |
| 5 | The game did not start, for example because the game window did not open. |
| other | A crash. |

For an exit code other than 0 or 3, the Console gives the cause and the path of the game log, `%LOCALAPPDATA%\OpenLancer\logs\OpenLancer.Play.log.txt`. A crash writes the whole exception to that log.

When the validation finds errors and the game closes during its load, the Console names the validation errors as the cause.

The warnings and errors of the game log also go to the Console with the source "Play". The Console adds a line only when it does not show the same line yet. The Console hides the lines that the base game without mods also gives, as it does for the lines of the editor. Turn on the base-game toggle of the Console to show them. When the game server is slow, the Console shows one info entry for the Play run. The Play log has each slow update.

A line of the game about a setup trigger of the test layer of a mission play has the origin OpenLancer. Its text ends with "(play start)", because the mod does not have the setup triggers.

The Console counts the lines of each load of the game: the start, each reload, and each start of a mission or a system. A line that a load gives more often than the base game shows. This is also true when the line has the same text as a base-game line, for example a line of a binary file, which has no line number.

## Play in the game

Play in the game plays the profile in your own copy of the retail game. The game can be Freelancer: HD Edition 0.7.1, Freelancer 1.0 or 1.1, or a source build (see [GAME_LINK.md](GAME_LINK.md)). The editor installs the mods into a play copy of the game, and starts the game program of the play copy. Your game folder does not change.

1. Link the game folder in File > Game link. The folder must contain the game program.
2. Click Play > Play in HD Edition 0.7.1, or the item of another game. Or press Ctrl+F5.

Play in the game then does these steps:

1. It checks each mod of the profile. A tier-2 mod, or a mod whose `mod.json` does not name the target of the game, stops Play.
2. It saves the active mod and waits for the validation of the save. Validation errors stop Play. When the validation cannot run, Play goes on with a warning.
3. It merges all mods of the profile on the files of the game, with the rules of the retail game. The format checks of the export and the content checks of the retail game run on the result. See "Export" and "Content checks of the retail game" in [MOD_FORMAT.md](MOD_FORMAT.md). Errors stop Play, and the Console lists them.
4. It makes the play copy of the game when it does not exist. The first make takes some seconds. Then it installs the merged files into the play copy. An install writes only the files that changed.
5. It starts the game program of the play copy, with the program folder of the play copy as its working folder.

The Console then says, for example, "Play in HD Edition 0.7.1: OpenLancer installed 12 files. The game starts." Start a new game or load a save in the main menu of the game.

Play in the game differs from Play in OpenLancer:

- It installs all mods of the profile, also the mods after the active mod.
- The retail game reads its files only at its start. A save while the game runs gives the note "The game does not load changes while it runs. Stop the game and play again."
- The game starts at its main menu. It has no start at the camera and no admin commands.

### Saves

| Game | Where the saves go |
|---|---|
| HD Edition | Into the folder `SAVE` of the play copy. The play copy sets `save_in_directory = true` in its own copy of `EXE\flplusplus.ini`. |
| Freelancer 1.0 or 1.1, and a source build | Into `Documents\My Games\Freelancer`, the folder of your normal saves. The Console tells it once. Load such a save only with the mod. |

### Stop the game

Press Shift+F5, or click the Stop button. The editor closes the main window of the game, as its close button does. When the game does not close in 5 s, Stop becomes Force stop. Force stop ends the game process at once. When you close the editor, the game ends too.

After the game closes, the editor checks the links of the play copy through handles. When a linked file changed, the Console tells which file of your game changed (see "Play copies" in [GAME_LINK.md](GAME_LINK.md)).

### Export and play

Mod > Export mod has the button Export and play. It exports the mod first. Then it installs the export of the first checked retail target into the play copy of its base, and starts the game. The play copy then holds this mod alone, with the text DLL of the export. The status bar shows "Playing the export of ol-sample in HD Edition 0.7.1 (play copy)", for example. The next Play in the game installs the mods of the profile again.

### Play one mod alone

In the Mods window, pick **Play > Play this mod alone in HD Edition 0.7.1**, or the item of another game. The editor merges this mod alone for the game, as the export does, and installs the result into the play copy. It writes no package. For the active mod with unsaved edits, the editor asks to save first, because the play uses the saved files. The status bar shows "Playing Starrunner alone in HD Edition 0.7.1 (play copy)", for example.

### Test plays

A game of a play copy that runs 60 s or more records a test play. The publish history of each mod that the play installed gets the record, with the content of the mod at the start of the install. The play counts only when the game ends without Force stop and without a crash code. The test server records nothing, but a game that joins it records a play. The publish checklist of the Mods window counts a test play of the current content. When you test the mod outside OpenLancer, click **Mark as tested** in the checklist.

### The status of the game of the play copy

| Text | Meaning |
|---|---|
| Play in HD Edition 0.7.1: validating save 2... | Play waits for the validation of the save. |
| Play in HD Edition 0.7.1: installing the mods... | Play merges the mods, and makes or updates the play copy. |
| Playing in HD Edition 0.7.1 (play copy) | The game of the play copy runs. |
| Playing Starrunner alone in HD Edition 0.7.1 (play copy) | The game of the play copy runs with one mod of the Mods window. |
| Stopping the game of the play copy | The editor closed the main window of the game, and the game did not end yet. |

## The test server

The test server is FLServer of a play copy, with the mods of the profile. Use it to test a mod in multiplayer on your own computer.

1. Click Play > Host test server, then the game.
2. Play in the game does its steps 1 to 4. Then FLServer of the play copy starts with `/c` in the program folder of the play copy. With `/c`, FLServer uses its saved settings.
3. At the first start, FLServer shows its dialog. Type a server name and click OK.
4. Click Play > Join test server. The game of the same play copy starts.
5. In the game, open Multiplayer, and join the server.

The status bar shows "Test server: HD Edition 0.7.1", for example. While the test server runs, Play in the game does not start, because an install would change the files of the server.

FLServer keeps `FLServer.cfg` and the multiplayer characters in `Documents\My Games\Freelancer`. The characters go to `Accts\MultiPlayer`, the folder of your normal server. Delete the test characters after the test, because a normal server cannot load a character with items of the mod.

Notes:

- HD Edition loads FLHook and FLSharp in the server. FLHook writes its files into `EXE` of the play copy.
- Windows can ask for the DirectPlay feature or for a firewall rule at the first start. You answer it. OpenLancer does not change Windows settings.
- The FLServer of a source build needs the Visual C++ runtime with MFC on the computer.

### Stop the test server

Click Play > Stop test server. The editor closes the main window of FLServer. FLServer saves its characters and stops its server thread, which can take up to 60 s. The status bar shows "Stopping the test server". The item is then Force stop test server, which ends FLServer at once. After 60 s, the editor ends FLServer itself.

When you close the editor while the test server runs, the editor asks: "The test server runs. Stop it and close OpenLancer?" Stop and close stops FLServer as above, and closes the editor when FLServer ended.
