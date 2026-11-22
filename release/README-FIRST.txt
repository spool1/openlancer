OpenLancer @VERSION_TITLE@
@TITLE_RULE@

OpenLancer is a mod editor for Freelancer (2003). It is an independent fan
project. It is not affiliated with, endorsed by or sponsored by Microsoft,
Digital Anvil or the Librelancer project. OpenLancer uses the name
"Freelancer" only to identify the game. All trademarks belong to their
owners.

@STAGE_NOTE@


YOU NEED YOUR OWN COPY OF THE GAME
----------------------------------
This download contains no game files. OpenLancer reads the game data from
your own installation of Freelancer. OpenLancer does not write into your
game folder.


SYSTEM REQUIREMENTS
-------------------
- Windows 10 or Windows 11, 64-bit.
- A graphics card and a driver with OpenGL 3.2 or later.
- An installed copy of a retail game of the list SUPPORTED GAMES below:
  Freelancer 1.0, Freelancer 1.1 or Freelancer: HD Edition 0.7.1.
- About 160 MB of disk space.
You do not need to install .NET.


SUPPORTED GAMES
---------------
For each game, the list tells what OpenLancer can do with it.

Freelancer 1.1 (retail)
  Edit and check mods: Yes
  Export (drop-in zip and FLMM package): Yes
  Play the mod in the game (Ctrl+F5): Yes

Freelancer: HD Edition 0.7.1
  Edit and check mods: Yes
  Export (drop-in zip and FLMM package): Yes
  Play the mod in the game (Ctrl+F5): Yes

Freelancer 1.0 (retail)
  Edit and check mods: Yes. It has the same INI files as 1.1.
  Export (drop-in zip and FLMM package): Yes, for 1.1
  Play the mod in the game (Ctrl+F5): Patch the game to 1.1 first. The
    1.0 program does not start on Windows 10 and 11.

A source build of the Freelancer-Decomp project that you build yourself
  Edit and check mods: Yes, with its INI files of 1.1
  Export (drop-in zip and FLMM package): Yes, for 1.1
  Play the mod in the game (Ctrl+F5): Yes, with Play > Play in source build

F5 plays the mod in the OpenLancer engine with the data of any linked game.
OpenLancer contains no code of the Freelancer-Decomp project.

A source build is a play target only. Make the profile with a retail game
first.


GETTING STARTED
---------------
The next two parts tell how to start the editor and play a first change.
The help pages of this version give all the steps. They also tell how to
play the mod in your own game (Ctrl+F5) and how to export it:
https://github.com/spool1/openlancer/blob/@DOCS_REF@/docs/GETTING_STARTED.md
When OpenLancer or the game does not start, read the troubleshooting page:
https://github.com/spool1/openlancer/blob/@DOCS_REF@/docs/TROUBLESHOOTING.md


START THE EDITOR
----------------
1. Extract the zip file to a folder, for example C:\Games.
2. Open the folder OpenLancer-v@VERSION@.
3. Double-click OpenLancer.Editor.exe. Its icon is a ship and a ring on a
   dark blue square.
4. If Windows shows "Windows protected your PC", click "More info". Then
   click "Run anyway". OpenLancer has no digital signature.
5. The editor shows "Link your Freelancer game". Click
   "Select game folder...".
6. Select the folder of your game. This folder contains the EXE and DATA
   folders. You can also drag the folder to the editor window.
7. Make sure that the editor shows "Game found" or "Game found, with
   warnings", and the name of your game, for example "Freelancer 1.1".
   A warning does not stop the profile.
8. Click "Create profile and open". The editor makes a profile and an
   empty mod, my-mod, in Documents\OpenLancer. Then the Scene shows a star
   system in 3D.

On Windows 11, Smart App Control can block a program that has no digital
signature. Windows then shows no "Run anyway" button, and OpenLancer
cannot start.


MAKE A CHANGE AND PLAY IT
-------------------------
1. Click an object in the Scene. Drag the arrows to move it.
2. Press Ctrl+S. The editor saves your change in the mod my-mod.
3. Press F5. The game starts at the camera of the Scene, with your change.
4. In the editor window, click the Stop button. It is in the middle of
   the menu bar.
To make one more mod, click Mod > New mod.


WHAT WORKS IN THIS VERSION
--------------------------
- Link your game folder. The editor shows the game and its version. It
  identifies Freelancer 1.0 and 1.1, with or without JFLP, and
  Freelancer: HD Edition.
- The Game link window (File > Game link...) shows each linked game and
  the result of its check. It also makes one more profile for a game.
- Look at the star systems in a 3D Scene, with a Hierarchy and an
  Inspector.
- Look at the star systems, ships, goods and equipment in the Database.
- Change values in the Inspector. Lists show the valid values. Color
  pickers and drags on the X, Y and Z labels help.
- Move and rotate objects, and change the size of zones, with snap and
  undo.
- Select nebulae and asteroid fields in the Scene and in the Hierarchy,
  also when the camera is inside them. To select the next item under the
  mouse, click again at the same place, or Alt+click.
- Create, duplicate, rename and delete objects, zones and lights. Use the
  "+" button of the Hierarchy, its right-click menu, or the keys Ctrl+D,
  F2 and Delete. A delete fixes or marks the references to the item. A
  rename changes each reference that the editor finds.
- The Assets panel shows the assets of the game and of your mod, with
  images. Drag an asset into the Scene to place it. Ships and asteroids
  can become static props.
- Save an object and its child objects as a prop group. Then place the
  group again in any system.
- Save your work as a mod. The editor writes only your changes. Before a
  save, the editor checks the mod. When it finds a value that stops or
  crashes the game, it stops the save and tells you why. It does not find
  all such values.
- Test your mod with F5 (Play). A save reloads the running game.
- Play the mod in your own game with Ctrl+F5. The editor installs the
  mods into a play copy next to the game. Your game folder does not
  change.
- Change the missions of the game in the Mission tab. The mission editor
  shows a mission as a graph of triggers. It adds and changes triggers,
  checks the script and runs a dry run. Play from a trigger starts Play,
  the test run of the editor, at a trigger of the mission. It does not
  start the original game.
- Export your mod for Freelancer 1.1 and for HD Edition 0.7.1, as a
  drop-in zip and as an FLMM package, with Mod > Export mod... The
  export checks the mod before it writes the packages. Nobody checked the
  FLMM package with FLMM yet.
- Look at the cutscenes of the game, and preview them. Change the values of
  a cutscene in your mod.
- Let an AI agent edit your mod with Agent > Start agent server. The
  agent uses the same edits, checks, undo and save as you. Its tool
  system_create makes a new star system, and connection_create connects
  two systems with a jump gate pair or a jump hole pair. The tools
  trade_lane_create, trade_lane_insert_ring, trade_lane_repair and
  trade_lane_delete make and change trade lanes.
- A new system shows at once in the Database, in the Inspector and in
  the Universe panel. The Scene shows a card with a Save button. After
  the save, the Scene opens the new system.
- The Universe panel shows the systems on the nav map grid, with their
  jump connections. It is read-only.
- The Inspector of a trade lane ring has the buttons Insert ring after,
  Repair lane and Delete ring. A delete of a trade lane ring links the
  lane again.
- When your mods change the jump connections, the export and Play in
  your own game (Ctrl+F5) make new route tables for the game.
- oltool checks your game folder, and checks, merges and shows mods from
  the command line.


IF YOU MOVE THE GAME FOLDER
---------------------------
When the editor cannot find the game folder, your profile does not open.
1. On the File menu, click "Game link...".
2. On the card of the game, click "Relink...".
3. Select the new folder of the game.
The editor changes each profile of the game to the new folder. When the
new folder has another version of the game, the editor asks you first.


WHAT DOES NOT WORK YET
----------------------
- Play (F5) is a test run of the editor, not a release of a game. It uses
  the OpenLancer runtime, a fork of the Librelancer engine. It is not the
  original game program. It does not play the whole story. Some missions
  stop, ships have fewer AI behaviors, and some effects and cutscenes look
  different.
- Play (F5) does not run the HD Edition plugins.
- OpenLancer does not support total conversions such as Discovery. The
  editor can open their data, with a warning.
- The Create menu and Duplicate cannot make jump gates, jump holes and
  trade lane rings. The agent tools make jump gate pairs, jump hole pairs
  and trade lanes. A delete of a jump gate or a jump hole deletes its
  pair. You cannot delete airlock gates yet.
- You cannot make new bases, nebulae or asteroid fields yet.
- You can select only one item at a time.
- A Play start at a checkpoint restores only the triggers of the
  checkpoint. It does not restore the objective, the dock and lane rules
  and the reputations of a saved game.
- You can make a new cutscene, but nobody checked yet that the retail
  games play it.
- There is no dialogue editor yet.


WHERE OPENLANCER KEEPS FILES
----------------------------
- Your profiles and mods: Documents\OpenLancer\profiles and
  Documents\OpenLancer\mods. You can select another folder at the first
  start.
- Prop groups: the folder props in the folder of each mod.
- Images of the Assets panel: %LOCALAPPDATA%\OpenLancer\thumbnails
- Play settings and saves: the folder <profile name>.play next to each
  profile.
- Editor settings and the list of linked games:
  %LOCALAPPDATA%\OpenLancer\editor.json
- Log files: %LOCALAPPDATA%\OpenLancer\logs. Help > Open log folder opens
  this folder. When the editor or the game starts, it moves the log of
  its last run to the file that ends in .log.previous.txt. The older
  lines of a long log are in the files that end in .log.old.txt and
  .log.previous.old.txt.
- Saves of the game main menu: Documents\SavedGames\OpenLancer
To remove OpenLancer, delete its folder, the folder
%LOCALAPPDATA%\OpenLancer and the folder Documents\SavedGames\OpenLancer.
Your mods are in Documents\OpenLancer. Delete that folder only if you do
not want to keep your mods.


REPORT A PROBLEM
----------------
1. In the editor, click Help > Report a problem. The editor copies a
   short text with the version, the commit, the linked game and the
   Windows version. Then it opens the bug report form in your browser.
2. Paste the text into the first field of the form.
3. Tell us what you did, what you expected, and what happened.
4. Click Help > Open log folder.
5. Attach OpenLancer.Editor.log.txt. For a problem in Play, also attach
   OpenLancer.Play.log.txt.
6. If the editor crashed and you started it again, attach
   OpenLancer.Editor.log.previous.txt. It has the log of the run that
   crashed.
7. If the game of Play crashed and you started Play again, attach
   OpenLancer.Play.log.previous.txt. Until the next Play, the log of
   the crash stays in OpenLancer.Play.log.txt.
Help > About OpenLancer... shows the version, the commit and the engine
commit. Click Copy to copy them.
The form is at
https://github.com/spool1/openlancer/issues/new?template=bug_report.yml
The log files show the paths of your folders. A path can contain your
Windows user name. Remove the text that you do not want to share.
To tell us about your game folder, open the OpenLancer folder in File
Explorer. Type cmd in the address bar of the folder, and press Enter. In
the command prompt, run: .\oltool game "<your game folder>"
Attach the output. It names the game, its version, its folder and the
changed INI files. It contains no game content.
Do not attach game files.
Do not send OpenLancer problems to the Librelancer project.


LICENSES
--------
OpenLancer is free software under the MIT License (LICENSE.txt). It
contains third-party software. NOTICE.txt and the folder "licenses" name
each part and its license.
x64\soft_oal.dll is OpenAL Soft. It is under the GNU Library General Public
License version 2. You can replace this file with your own build of
OpenAL Soft. Its source code is the file openal-soft-1.25.2.tar.gz on the
release page.
