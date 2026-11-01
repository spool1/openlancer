# Game link

OpenLancer does not include game files. Each user links their own copy of Freelancer (2003). The editor reads the files in the linked game folder. It never changes them.

## Link your game

At the first start, the Welcome page shows "Link your Freelancer game".

1. Click **Select game folder...**. Select the folder that contains the `EXE` and `DATA` folders of the game, for example `C:\Program Files (x86)\Microsoft Games\Freelancer`. You can also drag the folder to the editor window.
2. The editor checks the folder and shows the game that it found, with its notes. **Details** shows a table with each line of the check. Point to a short SHA-256 value to see all of it, or click the copy button after it.
3. Make sure that the profile name is correct. Click **Create profile and open**.

The editor makes a profile for the game and an empty mod, `my-mod`, for your changes. Then it opens the profile. Your first edit goes into `my-mod`.

When you select a folder inside the game, for example `EXE` or `DATA`, the editor finds the game folder. When you select the folder above it, for example `Microsoft Games`, the editor finds the game in it. When you select the installation disc or a copy of it, the editor tells you to install the game first.

**Skip** shows the normal Welcome page for this start. **Open a profile file...** opens a profile that you have already.

The normal Welcome page says that the editor makes mods. It names the first action. Without a linked game, link your game with **Game link...**, or click **Open profile...**. With a linked game, click **Open profile...**, or make a profile with **New profile...**. Sometimes no linked game can be the base of a profile, for example because its folder is not there. Then check the game with **Game link...**. **New profile...** is off, and its tooltip tells why. The page also lists the linked games, the recent profiles and the other profiles of the workspace.

## What the editor finds

| Game | How the editor knows it | Export target of mods |
|---|---|---|
| Freelancer 1.0 | The five code files (`Common.dll`, `Server.dll`, `DALib.dll`, `FLServer.exe`, `Content.dll`) have the hashes of the English retail disc. Else the file version of `Common.dll` is 1.0.1223.11. | `vanilla-1.1` |
| Freelancer 1.1 | The five code files have the hashes of the English patch 1.1. Else the file version of `Common.dll` is after 1.0.1223.11. | `vanilla-1.1` |
| With JFLP | `EXE\dacom.ini` names `jflp.dll`, and the `EXE` folder contains it. | the same as without JFLP |
| Freelancer: HD Edition | `EXE\freelancer.ini` names `Controls.dll` (0.7.1) or `HDEResources.dll` (after 0.7.1) as a resource DLL. The version comes from a text of that DLL. | `hde-0.7.1` for 0.7.1 only |
| Discovery | The game folder contains `DSLauncher.exe`. OpenLancer does not support it. | none |
| Unknown version | The code files have no known hash and no known version. You can use the folder. Some checks can be wrong. | none |
| Source build | The folder contains `bin\freelancer.exe` and `bin\freelancer.ini` (see "Source builds"). It is a play target only. | `vanilla-1.1` when its `DATA` has the retail INI files |

The editor also compares the INI files of the game with the file list of its version. The Game link window shows "INI files: same as retail", "INI files: same as HD Edition 0.7.1" or the number of changed files. OpenLancer uses the files in the game folder, also when they are changed.

OpenLancer has the file lists of the English retail 1.0 disc, of the English patch 1.1 and of HD Edition 0.7.1. Patch 1.1 changes only code files. So 1.0 and 1.1 have the same INI files, and the editor compares the INI files of both with the retail file list. The card of a 1.1 game tells this in a note. The options of the HD Edition installer change some files, for example `EXE\freelancer.ini` and `EXE\dacom.ini`. So an HD Edition install with other options shows changed files. The export of mods warns when the base of a target has changed files (see "Export" in [MOD_FORMAT.md](MOD_FORMAT.md)).

A folder without `Freelancer.exe` contains game data only. You can make mods with it.

## Where your files go

The editor puts new profiles and mods in the workspace folder, by default `Documents\OpenLancer`:

```
Documents\OpenLancer\
  profiles\freelancer-1-1.json     names the game folder and your mods
  profiles\freelancer-1-1.play\    the settings and saves of Play
  mods\my-mod\                     your mod
```

**Change...** on the profile screen selects another workspace folder. The new folder applies to new profiles only. A workspace folder in a game folder is not possible, because OpenLancer does not write into the game folder.

A developer who keeps the `GameData` layout can set the workspace folder to `GameData`. The editor also lists the profiles of `GameData\profiles`.

## The Game link window

**File > Game link...**, the game segment of the status bar and the Welcome page open the Game link window. It shows one card for each linked game:

- the game and the state of its INI files,
- its folder, with the link **Open folder**, which opens the folder in the file manager,
- the export target of its mods, the game program and the profiles that use the game,
- the notes of the check,
- **Details** with a table of the check: the file or the rule in the column Check, and what the check found in the column Result.

The buttons of a card:

- **New profile...** makes one more profile with an empty mod for the game, and opens it. On the card of a source build, New profile is not available. Its tooltip tells why (see "Source builds").
- **Check again** reads the game folder again.
- **Relink...** selects the new folder of the game, for example after you moved it.
- **Remove link** removes the game from the list. It deletes no file. A profile that uses the folder links it again when it opens.

The card of a game with its game program also shows its play copy: the folder, the size on disk and the number of links. When the play copy exists, the card has these buttons too (see "Play copies"):

- **Open play copy** opens the folder of the play copy in the file manager.
- **Check play copy** checks the links and the installed files. The Console lists each difference.
- **Uninstall mods** gives each file that Play in the game installed its version of the game again.
- **Remove play copy** deletes the play copy, after a question. Your game folder does not change. The saves in the play copy go too.

Check play copy, Uninstall mods and Remove play copy are not available while the game or the test server of the play copy runs.

**Link a game folder...** at the top right of the window links one more game. You can also drag a game folder to the editor window.

## A game folder that moved

When the game folder moved or its drive is not there, the card shows "Not found", and a profile of the game does not open. The Welcome page then shows "OpenLancer cannot find the game folder of profile ...".

1. Click **Open Game link**.
2. On the card of the game, click **Relink...**. Select the new folder. You can also drag the folder to the card.

When the new folder contains the same version of the game, the editor changes the `base` of each profile of the old folder. When the version is different, the editor asks first. The Console lists each profile that changed. The editor does not change a profile file in a game folder, a profile file in the OpenLancer repo, or a profile file with a JSON comment.

## oltool game

```
oltool game <folder> [--json] [--hashes] [--write-manifest <file>]
```

The command prints the same check as the editor. `--json` writes the full report with the evidence as JSON. `--hashes` adds the file list lines of the code files and of `Controls.dll`, `HDEResources.dll` and `jflp.dll`. The lines contain paths, sizes and hashes only, no game content.

`--write-manifest` writes the file list of the whole game folder to a file outside each game folder. The file has one line for each file, in the format of `data-manifest`. The list leaves out the uninstaller of the installer (`unins*` in the root, in any letter case), the `SAVE` folder and the `*.log` files. The command refuses an OpenLancer play copy.

The exit code is 0 for a game, 1 for a folder without a game, and 2 for a folder that does not exist or a wrong command line.

If you have a version of Freelancer that OpenLancer does not know, please run `oltool game "<game folder>" --hashes` and send the output. OpenLancer then gets the hashes of that version.

## Source builds

A source build is a game that you built from source code, for example Freelancer-Decomp. Its folder has the program and its DLLs in `bin`, and the game data in `DATA`:

```
<source build>\
  bin\freelancer.exe      the program
  bin\freelancer.ini      its configuration, with data path = ..\DATA
  bin\FLServer.exe        the server, if the build makes it
  DATA\                   the game data
```

The editor links such a folder as a play target. Select the folder, `bin` or a folder in `DATA`. The editor finds the root. The card shows "Source build (<folder name>)".

- `bin\freelancer.ini` must have `data path = ..\DATA`, in any letter case, and the `DATA` folder must exist. Else the editor says "The source build reads its data from <path>. OpenLancer can play a source build only with data path = ..\DATA."
- The editor compares the INI files of `DATA` with the file lists of the known builds. A source build is a rebuild of the 1.1 client, and patch 1.1 changes only code files. So with the retail INI files, the build is `source-vanilla-1.1`, and the export target is `vanilla-1.1`. The card shows "INI files: same as retail" and the note "The DATA folder has the retail INI files without changes. OpenLancer plays this source build as Freelancer 1.1." Then Play > Play in source build installs the mods into a play copy of the build.
- A source build is a play target only. Its `bin\freelancer.ini` is a development copy with other lines than the retail file. So a source build is never the base of a profile or of an export. New profile (as its tooltip), the first-run screen and Relink of a profile refuse it: "A source build is a play target only. It cannot be the base of a profile. Use a retail game folder as the base of the profile. Then play the profile in this build with Play > Play in source build."
- Play merges the mods on the files of the build itself: an install path under `EXE` goes to `bin`.

## Play copies

Play in the game never installs mods into your game folder. It installs them into a play copy: a folder with the layout of the game, next to the game folder. The default folder is `<game folder> - OpenLancer play`, for example `Freelancer HD Edition - OpenLancer play` next to `Freelancer HD Edition`. Each linked game has one play copy.

A play copy holds hard links and real copies. A hard link is a second name of the same file on the disk. It takes no space, and a play copy with links takes some seconds to make.

| Files | Kind in the play copy | Reason |
|---|---|---|
| `.ini`, `.thn`, `.txt` and `.xml` files of `DATA` | real copy | A text tool that saves such a file in place then changes only the copy. |
| the other files of `DATA`, and `DLLS` | hard link | They are large, and the game does not write into them. |
| `EXE` | real copy | The game, FLServer, FLHook and the HD Edition wrappers write into it. |
| `SAVE` and the other files and folders of the root, for example `EBUEula.dll` and `EULA.RTF` | real copy | FLServer and the game read the EULA files. HD Edition saves into `SAVE` of the play copy. |
| the uninstaller of the installer, `unins*` in the root, in any letter case | not in the play copy | An uninstaller removes the files at the path that it recorded. |
| each file that an install writes | real file | It replaces the link or the copy in the play copy only. |

A play copy of a source build holds `bin` without the files that only a build makes (`.pdb`, `.lib`, `.exp`, `.ilk`, `.iobj`, `.ipdb`). It also holds `DATA` as above, and `DATA_EXTRA`, `EXE`, `EBUEula.dll` and `EULA.RTF`. It holds no source code.

The play copy also has these files of OpenLancer:

| File | Content |
|---|---|
| `.openlancer-playcopy.json` | The record: the game folder, the kind and the stamps of each file, the files of the last install and the own edits. |
| `.openlancer-playcopy.lock` | The lock. It stays open while OpenLancer changes or checks the play copy, and while its game or test server runs. A second editor or `oltool` then gets "Another OpenLancer window uses the play copy <folder>." |
| `OPENLANCER PLAY COPY - READ ME.txt` | A warning: do not change, extract or install files in this folder with other programs. |

Do not change the files of a play copy with other programs, and do not extract mods into it. A change of a linked file also changes your game. For a test of FLMM or of a drop-in zip, make a full copy with `oltool install --full-copy` (see below).

### How OpenLancer protects your game

- OpenLancer never opens a file of a play copy for write. It writes each new file to a new temporary file next to it, and renames that file over the name. A rename replaces the name in the play copy only, so your game file keeps its bytes, its attributes and its times. OpenLancer deletes the names of a play copy one by one, and never follows a link to a folder.
- After each exit of the game of the play copy, and before each install, OpenLancer checks each link through a handle. When a linked file changed, the Console says "<path> changed after OpenLancer made the play copy. It is a hard link to <original>, so your game has the change too." Repair your game in that case, for example with its installer.
- When a program replaced a linked file in the play copy, your game did not change. OpenLancer makes the link again. When the game has a new version of a file, for example after an update, OpenLancer links the play copy to the new file.
- When an installed file changed in the play copy, OpenLancer keeps that version in `.openlancer-playcopy\changed\` and installs the file of the mods again.
- The game link refuses a play copy: "This folder is an OpenLancer play copy of <game>. Link the game folder <game>."

### Where the play copy goes

OpenLancer makes the play copy next to the game when it can. First it checks the folder with a probe. The probe makes a hard link to a game file. It also makes a read-only file of its own, and links, replaces and deletes it. The probe can fail, for example because the game is in `Program Files` or the drive has no hard links. Then the editor asks for a folder on the drive of the game. When the probe fails there too, the editor offers a full copy with its size. A full copy holds real copies of all files.

The folder of the play copy must not overlap the game folder, another game folder, the workspace, a mod folder or the OpenLancer repo. The editor keeps the folder that you selected for the game.

### oltool install

```
oltool install <profile.json> --game <folder> [--into <play copy>] [--full-copy]
oltool uninstall <play copy>
oltool playcopy check <play copy>
oltool playcopy remove <play copy>
```

- `install` merges all mods of the profile on the files of the game and runs the format checks. Then it makes or updates the play copy and installs the files. It prints the play copy and its game program. It never starts a program. `--into` selects the folder of the play copy. That folder must not overlap the folder of the profile file, a mod folder or the OpenLancer repo. `--full-copy` makes real copies of all files. FLMM and a drop-in zip write into game files, so test them only in a full copy.
- `uninstall` gives each installed file of the play copy its kind of the make again.
- `playcopy check` prints each difference and the numbers of links and real files. The exit code is 1 when it finds a difference.
- `playcopy remove` deletes the play copy. It refuses a folder without a valid record of a play copy.

The exit code is 0 when the command did its work, and 1 for a refusal or an error of the data. It is 2 when the command could not run, for example for a wrong command line or a locked play copy.

## What the check reads

The check reads `EXE\freelancer.ini`, `EXE\dacom.ini`, the five code files, the marker DLLs and each INI file under `DATA` and `EXE`. For a source build, it reads `bin\freelancer.ini` and the INI files under `DATA`. It reads the version resource of a DLL from its bytes.

The check never starts a program of the game and never loads a game DLL as code. It never writes, renames or deletes a file in the game folder. It does not read the registry, does not search the disk and does not use the network.
