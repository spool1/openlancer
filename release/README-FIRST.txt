OpenLancer @VERSION_TITLE@
@TITLE_RULE@

OpenLancer is a mod editor for Freelancer (2003). It is an independent fan
project. It is not affiliated with, endorsed by or sponsored by Microsoft,
Digital Anvil or the Librelancer project. OpenLancer uses the name
"Freelancer" only to identify the game. All trademarks belong to their owners.

This is a preview. Expect bugs. Keep a backup copy of your mods.


YOU NEED YOUR OWN COPY OF THE GAME
----------------------------------
This download contains no game files. OpenLancer reads the game data from
your own installation of Freelancer. OpenLancer does not write into your
game folder.


SYSTEM REQUIREMENTS
-------------------
- Windows 10 or Windows 11, 64-bit.
- A graphics card and a driver with OpenGL 3.2 or later.
- An installed copy of Freelancer. We tested this version with the English
  Freelancer 1.1 and with Freelancer: HD Edition 0.7.1.
- About 130 MB of disk space.
You do not need to install .NET.


START THE EDITOR
----------------
1. Extract the zip file to a folder, for example C:\Games.
2. Open the folder OpenLancer-v@VERSION@.
3. Double-click OpenLancer.Editor.exe. Its icon is a ship and a ring on a
   dark blue square.
4. If Windows shows "Windows protected your PC", click "More info". Then
   click "Run anyway". The preview has no digital signature.
5. The editor shows "Link your Freelancer game". Click
   "Select game folder...".
6. Select the folder of your game. This folder contains the EXE and DATA
   folders. You can also drag the folder to the editor window.
7. Make sure that the editor shows "Game found" and the name of your game,
   for example "Freelancer 1.1".
8. Click "Create profile and open". The editor makes a profile and an
   empty mod, my-mod, in Documents\OpenLancer. Then the Scene shows a star
   system in 3D.

On Windows 11, Smart App Control can block a program that has no digital
signature. Windows then shows no "Run anyway" button, and this preview
cannot start.


MAKE A CHANGE AND PLAY IT
-------------------------
1. Click an object in the Scene. Drag the arrows to move it.
2. Press Ctrl+S. The editor saves your change in the mod my-mod.
3. Press F5. The game starts at the camera of the Scene, with your change.
4. Press Shift+F5 to stop the game.
To make one more mod, click Mod > New mod.


WHAT WORKS IN THIS PREVIEW
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
- Change the missions of the game in the Mission tab. The mission editor
  shows a mission as a graph of triggers. It adds and changes triggers,
  checks the script and runs a dry run. Play from a trigger starts Play,
  the test run of the editor, at a trigger of the mission. It does not
  start the original game.
- Export your mod for Freelancer 1.1 and for HD Edition 0.7.1, as a
  drop-in zip and as an FLMM package, with Mod > Export mod... The
  export checks the mod before it writes the packages.
- Look at the cutscenes of the game, and preview them. Change the values of
  a cutscene in your mod.
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
- Play is a test run of the editor, not a release of a game. It uses the
  OpenLancer runtime, a fork of the Librelancer engine. It is not the
  original game program. It does not play the whole story. Some missions
  stop, ships have fewer AI behaviors, and some effects and cutscenes look
  different.
- Play does not run the HD Edition plugins.
- OpenLancer does not support total conversions such as Discovery. The
  editor can open their data, with a warning.
- You cannot create or delete jump gates, jump holes and trade lane
  rings yet. You cannot make new bases, nebulae or asteroid fields yet.
- You can select only one item at a time.
- The nebula and field picks, Create, Duplicate, Rename, Delete and the
  Assets panel are the newest parts of this preview. We tested them
  mostly with automatic tests.
- There is no dialogue editor yet.
- The mod format can change before version 1.0.


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
To tell us about your game folder, open a command prompt in the
OpenLancer folder. Run: oltool game "<your game folder>"
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
