# Troubleshooting

This page tells what to do when OpenLancer or the game does not start. It also tells what to do when Play in the game cannot make its play copy. The last parts tell where the log files are and how to report a problem. [GETTING_STARTED.md](GETTING_STARTED.md) gives the first steps with the release zip.

## Windows protected your PC

Windows can show "Windows protected your PC" when you start `OpenLancer.Editor.exe`. Microsoft Defender SmartScreen shows this message for a program that it does not know, for example a program without a digital signature. This version of OpenLancer has no digital signature.

1. Click **More info**.
2. Click **Run anyway**.

Download OpenLancer only from the [release page](https://github.com/spool1/openlancer/releases) of the project.

## Smart App Control blocks OpenLancer

On Windows 11, Smart App Control can block a program that has no digital signature. Windows then shows no "Run anyway" button, and OpenLancer cannot start. Windows 10 does not have Smart App Control.

This version of OpenLancer has no digital signature, so this limit applies to it. OpenLancer cannot change what Smart App Control does. The limit stays until a version of OpenLancer has a digital signature. To get help, ask on the [OpenLancer Discord server](https://discord.gg/BUh3nucfmP).

## Failed to create OpenGL context

The editor and the game of F5 draw with OpenGL 3.2 or later. When the graphics driver does not support OpenGL 3.2, the program shows "Failed to create OpenGL context" and closes.

1. Get the newest driver for your graphics card from the maker of the card.
2. Install the driver.
3. Start `OpenLancer.Editor.exe` again.

Play in the game (Ctrl+F5) starts your own game program. That program does not use the OpenGL renderer of OpenLancer.

## A game in Program Files

OpenLancer only reads the files of your game. So you can edit, check and export a mod for a game in `Program Files`, and F5 works too. Only Play in the game (Ctrl+F5) writes files for the game, and it writes them into the play copy (see [The play copy](#the-play-copy)).

Play in the game makes the play copy next to the game folder when it can. First it checks the folder with a probe. The probe makes a hard link to a game file, and a test file of its own. The probe can fail, for example because the game is in `Program Files` or the drive has no hard links.

When the probe fails, the editor shows the question "OpenLancer cannot make the play copy next to the game. Select a folder on drive ...".

1. Click **Select folder...**.
2. Select an empty folder on the drive of the game. The folder must be outside each game folder, each mod folder and the workspace folder, by default `Documents\OpenLancer`.

OpenLancer makes hard links in that folder, so the folder must be on the same drive as the game. The editor keeps the folder that you selected for the game.

When the probe fails in that folder too, the editor asks "Make a full copy?" with the size of the copy. A full copy holds real copies of all the game files. It needs the disk space of the whole game. A change in a full copy does not change your game.

To make a full copy, click **Copy**.

"Where the play copy goes" in [GAME_LINK.md](GAME_LINK.md#where-the-play-copy-goes) tells the rules of the probe and of the folder.

## The play copy

Play in the game never installs mods into your game folder. It installs them into a play copy: a folder with the layout of the game. By default, the play copy is next to the game folder, with the name `<game folder> - OpenLancer play`. Each linked game has one play copy.

Many files of a play copy are hard links. A hard link is a second name of the same file on the disk. It takes no disk space. So a change of a linked file also changes your game.

Do not change the files of a play copy with other programs. Do not extract mods into a play copy, and do not install mods into it with other programs. The file `OPENLANCER PLAY COPY - READ ME.txt` in the play copy gives the same warning. To test an exported drop-in zip or FLMM package, use a full copy of the game. "oltool install" in [GAME_LINK.md](GAME_LINK.md#oltool-install) tells how to make one.

The card of the game in **File > Game link...** shows the play copy: its folder, its size on disk and the number of links. When the play copy exists, the card has these buttons:

- **Open play copy** opens the folder of the play copy.
- **Check play copy** checks the links and the installed files. The Console lists each difference.
- **Uninstall mods** gives each file that Play in the game installed its version of the game again.
- **Remove play copy** deletes the play copy and the saves in it, after a question. Your game folder does not change.

These buttons, except Open play copy, are not available while the game or the test server of the play copy runs.

After each exit of the game, OpenLancer checks the links of the play copy. When a linked file changed, the Console says that the file "changed after OpenLancer made the play copy". Your game then has the change too. Repair your game, for example with its installer. "Play copies" in [GAME_LINK.md](GAME_LINK.md#play-copies) tells more.

## The game does not start

First make sure that OpenLancer supports your game:

| Game | Play the mod in the game (Ctrl+F5) |
|---|---|
| Freelancer 1.1 (retail) | Yes |
| Freelancer: HD Edition 0.7.1 | Yes |
| Freelancer 1.0 (retail) | Patch the game to 1.1 first. The 1.0 program does not start on Windows 10 and 11. |
| A source build of the Freelancer-Decomp project that you build yourself | Yes, with **Play > Play in source build** |

F5 plays the mod in the OpenLancer engine with the data of any linked game. OpenLancer contains no code of the Freelancer-Decomp project. [Supported games](GETTING_STARTED.md#supported-games) gives the full table.

### Play in OpenLancer (F5)

The game of F5 is `OpenLancer.Play.exe`, next to the editor.

- The Console of the editor shows the lines of this game with the source "Play".
- Validation errors stop Play, and the Console lists them. Correct each error, then press F5 again.
- When the game stops by itself, the Console gives the cause, the exit code and the path of the game log. "When the game stops by itself" in [PLAY.md](PLAY.md#when-the-game-stops-by-itself) gives the meaning of each exit code.
- The game log is `OpenLancer.Play.log.txt` (see [The log files](#the-log-files)). A crash writes the whole exception to that log.
- When the game window does not open, see [Failed to create OpenGL context](#failed-to-create-opengl-context).

### Play in the game (Ctrl+F5)

The game of Ctrl+F5 is the game program of the play copy.

- The Console of the editor shows the lines of Play in the game with the source "Play in the game". When Play does not start, the line starts with "Play did not start." and gives the reason.
- Play in the game needs a linked game folder with its game program. Without such a folder, the Console says "Link a game folder with its game program in File > Game link."
- Make sure that your game starts from its own folder, without OpenLancer. The play copy starts the same game program. When the game does not start without OpenLancer, repair the game first, for example with its installer.
- When the game stops with an error, the Console says "The game of the play copy stopped with exit code" and the number.
- To find a problem in the play copy, click **Check play copy** (see [The play copy](#the-play-copy)).
- To find a problem in the game folder, check it (see [Check the game folder](#check-the-game-folder)).

### Check the game folder

To check the game folder in the editor:

1. Click **File > Game link...**.
2. On the card of the game, click **Check again**.
3. Open **Details** to see each line of the check.

To check the game folder with `oltool`:

1. In File Explorer, open the OpenLancer folder.
2. Click the address bar of the folder.
3. Type `cmd`.
4. Press Enter. A command prompt opens in the OpenLancer folder.
5. Type `.\oltool game "<your game folder>"`.
6. Press Enter.

The command also works in PowerShell, for example after **Open in Terminal**. PowerShell needs the `.\` before `oltool`.

The output names the game, its version, its folder and the changed INI files. It contains no game content. "oltool game" in [GAME_LINK.md](GAME_LINK.md#oltool-game) tells more.

## The log files

Click **Help > Open log folder**. The editor opens the folder `%LOCALAPPDATA%\OpenLancer\logs`. It holds the log files of the editor and of the game of F5.

| File | Content |
|---|---|
| `OpenLancer.Editor.log.txt` | The log of the editor. |
| `OpenLancer.Play.log.txt` | The log of the game of F5. |
| `OpenLancer.Editor.log.previous.txt` | The log of the last run of the editor before this run. |
| `OpenLancer.Play.log.previous.txt` | The log of the last game of F5 before this game. |
| The files that end in `.log.old.txt` and `.log.previous.old.txt` | The older lines of a long log. |

When the editor or the game starts, it moves the log of its last run to the file that ends in `.log.previous.txt`. So the first start after a crash keeps the log of the crash in that file. The second start replaces it. Copy the log of a crash before the second start.

A program that keeps a log open, for example a log viewer, stops the move. Then the new run writes its log to a file with a number, for example `OpenLancer.Play.log.0.txt`. Close the log viewer before you start the program again.

## Report a problem

1. In the editor, click **Help > Report a problem**. The editor copies a report text and opens the bug report form in your browser.
2. Paste the report text into the first field of the form.
3. Write what you did, what you expected and what occurred.
4. Click **Help > Open log folder**.
5. Attach `OpenLancer.Editor.log.txt`.
6. For a problem in Play, attach `OpenLancer.Play.log.txt`.
7. If the editor crashed and you started it again, attach `OpenLancer.Editor.log.previous.txt`.
8. If the game of Play crashed and you started Play again, attach `OpenLancer.Play.log.previous.txt`.
9. For a problem with your game folder, attach the output of `oltool game` (see [Check the game folder](#check-the-game-folder)).

The report text gives the version, the commit, the linked game and the Windows version.

The log files and the output of `oltool game` show the paths of your folders. A path can contain your Windows user name. Before you attach a file, remove the text that you do not want to share.

Do not attach game files. This rule includes a changed copy of a game file.

When the editor does not start, open the [bug report form](https://github.com/spool1/openlancer/issues/new?template=bug_report.yml) in your browser. Write the version from the name of the zip file. **Help > About OpenLancer...** also shows the version, the commit and the engine commit, and **Copy** copies them.

Do not send OpenLancer problems to the Librelancer project. To ask for help, join the [OpenLancer Discord server](https://discord.gg/BUh3nucfmP).
