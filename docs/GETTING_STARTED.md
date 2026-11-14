# Getting started

This page tells how to make your first mod with the release zip of OpenLancer. You link your own copy of the game, make a change, save it, play it and export it. The zip has no game files.

## What you need

- Windows 10 or Windows 11, 64-bit.
- A graphics card and a driver with OpenGL 3.2 or later.
- Your own installed copy of a game of the table [Supported games](#supported-games).

You do not need to install .NET. `README-FIRST.txt` in the zip gives all the system requirements. In the editor, **Help > Getting started** opens that file.

## Install OpenLancer

1. Download the zip file of OpenLancer from the [release page](https://github.com/spool1/openlancer/releases).
2. Extract the zip file to a folder, for example `C:\Games`.
3. Open the folder `OpenLancer-v<version>`, for example `OpenLancer-v1.0.0`.

OpenLancer has no installer. "WHERE OPENLANCER KEEPS FILES" in `README-FIRST.txt` tells how to remove OpenLancer.

## Start the editor and link your game

1. Double-click `OpenLancer.Editor.exe`.
2. If Windows shows "Windows protected your PC", do the steps of [Windows protected your PC](TROUBLESHOOTING.md#windows-protected-your-pc).
3. On the card "Link your Freelancer game", click **Select game folder...**.
4. Select the folder of your game. This folder contains the `EXE` and `DATA` folders.
5. Make sure that the editor shows "Game found" and the name of your game, for example "Freelancer 1.1".
6. Click **Create profile and open**.

Instead of steps 3 and 4, you can drag the game folder to the editor window.

The editor makes a profile for your game and an empty mod, `my-mod`, in `Documents\OpenLancer`. Then it opens the profile, and the Scene shows a star system in 3D. Your edits go into `my-mod`.

OpenLancer reads the files of the game folder. It never changes them. When the editor shows "No game in this folder", click **Select another folder**. Then select the folder that contains the `EXE` and `DATA` folders of the game. [GAME_LINK.md](GAME_LINK.md) tells more about the game link.

## Make your first change

1. Click an object in the Scene, for example a planet or a station.
2. Drag an arrow of the gizmo to move the object.

The Inspector shows the values of the selected object. You can also change a value there. Ctrl+Z undoes the last edit.

## Save the change

Press Ctrl+S. The editor saves your change in the mod `my-mod`. Before the save, the editor checks the mod. When it finds a value that stops or crashes the game, it stops the save. The Console then tells why.

## Play the change in OpenLancer (F5)

1. Press F5. The game starts at the camera of the Scene, with your change.
2. Fly and look at your change.
3. In the editor window, click the **Stop** button.

The Stop button is in the middle of the menu bar of the editor. Shift+F5 also stops the game, but only while the editor window is active. The game window does not use the keys of the editor.

F5 plays the mod in the OpenLancer engine, not in the original game program. It installs nothing. While the game runs, each save of the editor reloads the game. [PLAY.md](PLAY.md) tells more about Play.

## Play the change in your game (Ctrl+F5)

For Freelancer 1.0, patch the game to 1.1 first. The 1.0 program does not start on Windows 10 and 11. See [Supported games](#supported-games).

1. Press Ctrl+F5.
2. In the main menu of the game, start a new game or load a save.
3. In the editor window, click the **Stop** button.

Ctrl+F5 plays in the game of your last Play in the game. The first time, it plays in the first linked game that has its game program. To play in another linked game, click its item in the Play menu, for example **Play > Play in HD Edition 0.7.1**.

The editor saves the mod and installs the mods into a play copy of the game. A play copy is a folder next to the game folder. Then the editor starts the game program of the play copy. Your game folder does not change. The first play copy takes some seconds to make.

The game reads its files only when it starts. So after a save, stop the game and play again. Freelancer 1.1 keeps the saves of a play in `Documents\My Games\Freelancer`, with your normal saves. Load such a save only with the mod. HD Edition keeps the saves of a play in the play copy.

When the editor cannot make the play copy next to the game, it asks for a folder. This can occur for a game in `Program Files`. See [A game in Program Files](TROUBLESHOOTING.md#a-game-in-program-files) and [The play copy](TROUBLESHOOTING.md#the-play-copy).

## Export the mod

1. Click **Mod > Export mod...**.
2. In the Export dialog, select the targets, for example **HD Edition 0.7.1** and **Freelancer 1.1**.
3. Select the packages: **Drop-in zip for Nexus** and **FLMM package (preview)**.
4. Click **Export**.
5. Click **Open folder** to see the packages.

The export makes a drop-in zip and an FLMM package for each retail target that you select. A retail target needs a linked game as its base: HD Edition 0.7.1 for HD Edition 0.7.1, and Freelancer 1.0 or 1.1 for Freelancer 1.1. The packages go to `Documents\OpenLancer\exports\my-mod`, in a folder for the version of the mod. They hold only the changes of your mod. The export never writes into a game folder. "Export" in [MOD_FORMAT.md](MOD_FORMAT.md) tells more about the packages.

## Supported games

| Game | Edit and check mods | Export (drop-in zip and FLMM package) | Play the mod in the game (Ctrl+F5) |
|---|---|---|---|
| Freelancer 1.1 (retail) | Yes | Yes | Yes |
| Freelancer: HD Edition 0.7.1 | Yes | Yes | Yes |
| Freelancer 1.0 (retail) | Yes. It has the same INI files as 1.1. | Yes, for 1.1 | Patch the game to 1.1 first. The 1.0 program does not start on Windows 10 and 11. |
| A source build of the Freelancer-Decomp project that you build yourself | Yes, with its INI files of 1.1 | Yes, for 1.1 | Yes, with **Play > Play in source build** |

F5 plays the mod in the OpenLancer engine with the data of any linked game. OpenLancer contains no code of the Freelancer-Decomp project.

A source build is a play target only. Make the profile with a retail game first. Then link the source build in **File > Game link...** with **Link a game folder...**. "Source builds" in [GAME_LINK.md](GAME_LINK.md#source-builds) tells the rules.

## More help

- [MISSIONS.md](MISSIONS.md): the mission studio.
- [CUTSCENES.md](CUTSCENES.md): the cutscene editor.
- [PLAY.md](PLAY.md): Play in OpenLancer, Play in the game and the test server.
- [GAME_LINK.md](GAME_LINK.md): the game link, the Game link window and the play copies.
- [MOD_FORMAT.md](MOD_FORMAT.md): the files of a mod, the save gate and the export.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md): what to do when something does not work, and how to report a problem.

To ask for help, join the [OpenLancer Discord server](https://discord.gg/BUh3nucfmP). In the editor, **Help > Discord** opens the invite.
