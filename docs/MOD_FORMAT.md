# OpenLancer mod format (version 2)

An OpenLancer mod changes the game data without a change to the base files. A profile lists the mods. OpenLancer merges them over the game data in load order: the folder of your linked game, or the extracted game data of a developer workspace. The result is a merged view of the data. The base folder never changes.

Format version 2 adds the tier and the targets of a mod to `mod.json`. The patch rules are the same for both versions. OpenLancer also reads mods of format version 1.

## Mod folder

```
<mod>/
  mod.json                     required
  strings.json                 optional: text for names and infocards
  patches/<install path>.ini   entry-level changes to an INI file
  patches/<install path>.thn   changes to a cutscene script (see "Cutscenes")
  files/<install path>         a file that the mod adds or replaces
  props/<nickname>.ini         a prop group of the editor (see "Prop groups"). The merge ignores it.
  props/<nickname>.strings.json  the text of a prop group. The merge ignores it.
  graphs/<install path>        the graph file of a mission script of the editor: node places, acts and prefab
                               instances (see "Editor files of missions"). The merge ignores it.
  prefabs/missions/<id>.ini    a mission prefab of the editor (see "Editor files of missions"). The merge ignores it.
  publish.json                 optional: the publish settings (see "Publish settings"). The merge ignores it.
  publish/screenshots/         the screenshots of the Nexus page. The merge ignores it.
  .olsave/                     the editor's save folder (see "The editor"). The merge ignores it.
```

An install path is relative to the game root, for example `DATA/EQUIPMENT/goods.ini` or `EXE/freelancer.ini`. Letter case does not matter.

### mod.json

```json
{
  "id": "my-mod",
  "name": "My mod",
  "version": "1.0.0",
  "formatVersion": 2,
  "tier": 1,
  "targets": [ "hde-0.7.1", "vanilla-1.1", "openlancer" ],
  "dependsOn": [ "other-mod" ],
  "loadAfter": [ "optional-mod" ]
}
```

| Field | Rule |
|---|---|
| `id` | Lowercase letters, digits, `-` and `_`. It starts with a letter. |
| `formatVersion` | `2`, or `1` for a mod without `tier` and `targets`. |
| `tier` | Optional. `1` for a data mod: INI changes, text and cutscenes, which the retail game and OpenLancer run. `2` for a runtime mod, which only OpenLancer runs. Without `tier`, the mod is a tier-1 mod. |
| `targets` | Optional. The games that the mod is for, from this list: `hde-0.7.1` (HD Edition 0.7.1), `vanilla-1.1` (the retail game with patch 1.1, with or without JFLP) and `openlancer`. Without `targets`, a tier-1 mod is for all three, and a tier-2 mod is for `openlancer`. A tier-2 mod can have only the target `openlancer`. |
| `dependsOn` | Mods that must be in the profile. They load before this mod. |
| `loadAfter` | Mods that load before this mod when they are in the profile. |

A mod of format version 1 is a tier-1 mod for all three targets. It cannot have `tier` or `targets`. New mod in the editor writes format version 2 with `"tier": 1` and no `targets`.

The `name` is the display name: players see it on Nexus, in FLMM and in the README. The `id` is the internal name. It names the folder, the package files and the text DLL `ol_<id>.dll`.

OpenLancer does not load a mod with an unknown format version, tier or target, and the error tells why. For example: "OpenLancer cannot read formatVersion 3. It reads formatVersion 1 and 2." or "OpenLancer cannot read tier 3. Use tier 1 for a data mod, or tier 2 for a mod that only OpenLancer runs." An older OpenLancer refuses a mod of format version 2: "formatVersion must be 1." Thus it does not load a tier-2 mod as a data mod.

### Publish settings

`publish.json` is optional. It holds the texts of the Nexus page and of the README of each package. The merge never reads it, so a bad value in it never stops the load of a profile. An older OpenLancer ignores the file and the folder `publish/`. The format version of `mod.json` stays 2.

```json
{
  "formatVersion": 1,
  "summary": "A fast light fighter for sale at the Manhattan ship dealer.",
  "description": "# What it changes\n- New ship: the Starrunner.\n\nMore on [the forum thread](https://example.org/thread).",
  "authors": [ "Example Modder" ],
  "credits": [ { "name": "Example Tester", "for": "Test flights" } ],
  "permissions": "You can use the files of this mod in other free mods. Give credit to the authors.",
  "category": "Miscellaneous",
  "tags": [ "Ships", "Gameplay" ],
  "requirements": [ { "name": "JFLP", "url": "https://example.org/jflp", "note": "Optional, for Freelancer 1.1" } ],
  "pageUrl": "",
  "screenshots": [ { "file": "publish/screenshots/manhattan-dealer.png", "caption": "The Starrunner at the ship dealer" } ],
  "changelog": [ { "version": "1.0.0", "changes": [ "First release." ] } ]
}
```

| Field | Rule |
|---|---|
| `formatVersion` | `1`. OpenLancer shows a file of another version read-only. |
| `summary` | One line. A summary of more than 350 characters gives a warning, because Nexus can cut it. |
| `description` | The text format below. At most 64 KB. |
| `authors`, `tags` | Lists of texts. A tag cannot have a comma. |
| `credits` | Rows with `name` and `for`. |
| `permissions` | Free text. |
| `category` | The Nexus category, `Miscellaneous` by default. |
| `requirements` | Rows with `name`, `url` and `note`. The export adds the requirement of each target game itself. |
| `pageUrl` | Empty, or a link that starts with `https://`. The README and the FLMM script use it. The texts of an export leave out a link that does not start with `https://`, and so do the requirements. |
| `screenshots` | Rows with `file` and `caption`. Each `file` is below `publish/screenshots/` (see "Screenshots"). |
| `changelog` | One entry for each version, newest first, with a list of changes. The texts of an export hold the entries of the exported version and the lower versions. |

A file with a JSON comment loads, and the export uses its texts. OpenLancer cannot write the file and keep the comment, so the publish settings are read-only. Each text loses its control characters. Only the description, the changes and the permissions keep their line ends. The export gives a warning for each field that breaks a rule of this table.

The description uses a small Markdown subset:

| Source | Nexus BBCode | README and FLMM text |
|---|---|---|
| `# Heading` | `[size=5][b]Heading[/b][/size]` | The line, then an empty line |
| `## Heading` | `[size=4][b]Heading[/b][/size]` | The line |
| `- item` or `* item` | `[list]` `[*]item` `[/list]` | `- item` |
| `1. item` | `[list=1]` `[*]item` `[/list]` | `1. item` |
| `**bold**` | `[b]bold[/b]` | `bold` |
| `[text](https://link)` | `[url=https://link]text[/url]` | `text (https://link)` |
| A BBCode tag such as `[color=red]` | The tag | The text without the tag |

A link that does not start with `http://` or `https://` stays text, and so does a `**` without its pair. The FLMM script is ASCII: quotes, dashes, the ellipsis and the no-break space get their ASCII form. Each other character outside ASCII becomes `?`, and the export gives a warning that names it.

### Versions

A version has the form `MAJOR.MINOR.PATCH` of Semantic Versioning 2.0, with an optional pre-release such as `1.0.0-beta.1`. A pre-release has lowercase letters, digits, `-` and `.` only, because Windows uses the same file name for uppercase and lowercase letters. A version has no build metadata (`+`) and no leading zeros.

The mod loader accepts any version. The export needs a version that is safe in a file name: 1 to 64 characters from `0-9`, `A-Z`, `a-z`, `.` and `-`, that starts with a letter or a digit and has no `..`. It does not end with `.`, and its part before the first `.` is not a device name of Windows, such as `con`, `nul` or `com1`. The export refuses an unsafe version before it makes a file name or a folder. An old version such as `1.0` is safe, and it gives a warning only.

### Screenshots

A screenshot is a PNG or JPEG image in `publish/screenshots/` of the mod. The import, Apply and the export run the same checks:

- The path is relative, below `publish/screenshots/`, and has no `..`.
- The real path, after links, is in the mod folder and not in a game folder.
- The file starts with the bytes of a PNG or JPEG image.
- The image has 1 to 8,192 pixels on each side.
- The file does not have the bytes of a file of a build manifest (`data-manifest`).

A screenshot that fails is an error. The export then copies no screenshot and refuses the package of the target OpenLancer. The retail packages still write, because they hold no screenshot.

## Patches

A patch of an INI file is a text INI file. Each section in it changes one section of the target file. For a patch of a cutscene script, see "Cutscenes".

### How a patch section finds its target

Most sections are found by their `nickname` entry:

```ini
[Good]
nickname = le_hull
price = 9000
```

Some section types use another key:

| Section | Key |
|---|---|
| `[FactionProps]` | `affiliation` |
| `[fuse]` | `name` |
| `[RepChangeEffects]` | `group` |
| `[Nebula]`, `[Asteroids]` | `zone` |
| `[SystemInfo]`, `[Music]`, `[Dust]`, `[Ambient]`, `[Background]`, `[TexturePanels]` | none: the file has one of each |
| `[BaseGood]` | none: patch sections are always added (see below) |
| Nebula files: `[Fog]`, `[Exterior]`, `[NebulaLight]`, `[Clouds]`, `[BackgroundLightning]`, `[DynamicLightning]`, `[properties]`, `[Exclusion Zones]` | none: the file has one of each |
| Asteroid field files: `[Field]`, `[Cube]`, `[Band]`, `[ExclusionBand]`, `[AsteroidBillboards]`, `[properties]`, `[Exclusion Zones]` | none: the file has one of each |
| `[DynamicAsteroids]` | `asteroid` |
| `[LootableZone]` | `zone`, or none for the section without a `zone` line (see below) |
| `[locked_gates]` of `DATA\InitialWorld.ini` | none: the file has one |
| `[Mission]`, `[Constants]` of a file under `DATA\MISSIONS` | none: the file has one of each (see "Mission files") |

If the key matches no section, or more than one section, the merge fails with an error.

#### Nebula and asteroid field files

A `[Nebula]` or an `[Asteroids]` section of a system file names a nebula file or an asteroid field file with its `file` key, for example `solar\nebula\Bw05_Crow_nebula.ini`. The engine keeps one section of most types in these files: the last one, or the first one for `[NebulaLight]` and `[Clouds]`. So a patch section of such a type finds the one section of its type. The ring and black hole files of `DATA\SOLAR` use `[Fog]`, `[Exterior]` and `[Clouds]` in the same way.

Two retail nebula files have two `[NebulaLight]` sections: `Bw06_Kunashir_edge_nebula.ini` and `Hi01_Siniestre_edge_nebula.ini`. A patch cannot find one of them, so the merge fails with an error. Use a whole-file override for these two sections.

The engine keeps each `[DynamicAsteroids]` and each `[LootableZone]` section. A `[LootableZone]` with a `zone` line gives the loot of that zone. The `[LootableZone]` without a `zone` line gives the loot of the whole field, and a file has one such section at most. A patch section without a `zone` line finds it:

```ini
[LootableZone]
dynamic_loot_count = 2, 4
```

The `[Exclusion Zones]` section lists the holes of the nebula or the field. Its lines belong together (see "Lines that belong together").

### What a patch section does

- The patch's entries replace all target entries with the same names. For example, three `addon` lines replace all `addon` lines of the target. So list every line of a name that you change.
- The patch's entries are written as one block, in patch order, at the position of the first entry that they replace. When the target has none of the names, the block goes at the end of the section.
- Entries that the patch does not name stay as they are.

### Lines that belong together

The engine attaches some lines to the line before them. For example, each `faction` and `faction_weight` line of a `[Zone]` belongs to the `encounter` line before it. When a patch changes one name of such a group, it must list all lines of all names in the group, in the right order. Otherwise the merge fails with an error.

| Section | Group |
|---|---|
| `[Zone]` | `encounter`, `faction`, `faction_weight` |
| `[GF_NPC]` | `know`, `knowdb` |
| `[GF_NPC]` | `rumor`, `rumor_type2`, `rumorknowdb` |
| `[EncounterFormation]` | `ship_by_class`, `ship_by_npc_arch`, `pilot_job`, `make_class` |
| `[Exclusion Zones]` | `exclusion`, `exclude`, `fog_far`, `fog_near`, `zone_shell`, `shell_scalar`, `max_alpha`, `color`, `exclusion_tint`, `exclude_billboards`, `exclude_dynamic_asteroids`, `empty_cube_frequency`, `billboard_count` |

Each `exclusion` or `exclude` line of an `[Exclusion Zones]` section starts a hole: the zone that it names. The lines after it belong to that hole, up to the next `exclusion` or `exclude` line. So a patch that changes one hole lists all lines of the section, in order. To remove a hole, leave out its `exclusion` line and the lines after it. This patch of `DATA/SOLAR/NEBULA/Bw05_Crow_nebula.ini` gives the first hole of the Crow nebula a fog distance of 7500, and the nebula keeps only its first two holes:

```ini
[Exclusion Zones]
exclusion = Zone_Bw05_01_exclusion
fog_far = 7500
zone_shell = solar\nebula\crow_exclusion.3db
shell_scalar = 1
max_alpha = 0.5
exclusion_tint = 255, 255, 255
exclusion = Zone_Bw05_yanagi_debris_field
fog_far = 2000
```

### Value types in binary files

Most retail INI files are binary. A binary file stores each value as a boolean, an integer (32 bits), a float or text. A patch is text, so the merge gets the type of a patch value from its text: `5` is an integer, `5.0` is a float, `true` is a boolean and `li_gun` is text.

In a patch section, the first line of a name replaces the first line of that name in the target. The second line replaces the second line, and so on. Each value replaces the value at the same position in its line. A value that replaces no value, for example in a new section, keeps the type of its text. The exceptions are `true`, `false` and large numbers (see below).

The retail binary files store no booleans. They store `true` and `false` as text. So the merge stores `true` and `false` as text in these cases:

- `true` or `false` that replaces text stays text. When the old text is `true` or `false` in any letter case and the truth value does not change, the old text stays, for example `TRUE`. Other text, for example `yes`, becomes `true` or `false`.
- `true` or `false` that replaces no value becomes the text `true` or `false`, in lowercase.

An integer is a whole number from -2147483648 to 2147483647. The value that an integer replaces can change its type:

- An integer that replaces a float becomes a float when it is from -16777216 to 16777216. A float holds every whole number in this range exactly. Other integers stay integers, for example a hash such as `-1980013182`.

A whole number outside the integer range is a large number. A binary file has no type that holds every large number. So the value that it replaces sets its type:

- A large number from 2147483648 to 4294967295 that replaces an integer is a hash, for example `2314954114`. The file stores the integer with the same 32 bits.
- A large number that replaces a float becomes a float. When the float cannot hold the number exactly, the merge reports a warning.
- In all other cases, the merge fails with an error. For example, `4294967296` that replaces an integer is an error, and so is `4000000000` in a new section.

To write a hash, use its signed form: `-1980013182` for `2314954114`. The signed form is an integer, so the engine reads the same hash in all cases. To write a large number as a float, write it with a dot: `4000000000.0`.

A line of a binary file holds at most 255 values. When a patch line has more, the merge reports an error and keeps the first 255 values.

`true` or `false` cannot replace a number. The retail game reads the text `true` as 0 in a number key, and a binary file has no boolean type. So the merge reports an error, for example "price: write 1 or 0. The game reads true as 0 in a number key." Write `1` or `0`.

### Directives

Directives start with `ol.`. Do not use `@`: the INI parser ignores lines that start with `@`.

| Directive | Effect |
|---|---|
| `ol.new = true` | Add this section to the file. A section with the same key must not exist. |
| `ol.delete = true` | Remove the target section and its child sections. The patch section has only the key and this directive. |
| `ol.remove = name1, name2` | Remove all target entries with these names. |
| `ol.parent = <key>` | With `ol.new`, add a child section to the parent section with this key (see below). |
| `ol.rename = <new key>` | Give the target section a new key. The section keeps its place (see "Renames"). |
| `ol.append = true` | Only in a `[Data]` section of a patch of `EXE/freelancer.ini`: add the lines of the section to `[Data]` (see "freelancer.ini"). |
| `ol.replace = true` | Only in a mission file: replace all lines of the target section with the lines of the patch section, in order (see "Mission files"). |
| `ol.base = <hash>` | Only in a mission file, with `ol.replace` or `ol.delete`: the hash of the section when the editor wrote the patch (see "Mission files"). |
| `ol.before = <type>, <key>` | Only in a mission file, with `ol.new`: add the new section directly before the section with this type and key (see "Mission files"). |

An unknown `ol.` directive is an error.

### Renames

A patch section with `ol.rename` gives a section of a lower layer a new key. The patch section finds its target by the old key, as a change does. The other lines of the patch section change the target. Then the merge gives the key line the new key. The section keeps its place in the file, and its child sections stay after it:

```ini
[Object]
nickname = Li01_14_depot_water
ol.rename = Li01_14_depot_water_old
pos = -37675, 0, -27705
```

- After the rename, a later patch section finds the section only by its new key. A patch of the old key fails with "no section to patch".
- The merge refuses `ol.rename` with `ol.new`, `ol.delete` or `ol.parent`. It also refuses it on a section type without a key and on a child section. The patch section must have exactly one key line and one `ol.rename` line.
- The new key must differ from the old key in more than letter case. No other section of the type in the file can have the new key.
- The place counts, because the engine reads some sections in file order. For example, a player launches from the first object of a system that docks with the base. A delete and a new section would move the section to the end of the file.

### Mission files

A mission file is a file under `DATA\MISSIONS`: a mission script, the file of its ship builds, or a shared file of the missions. The game reads the lines of these mission section types in order: `[Trigger]`, `[ObjList]`, `[Dialog]`, `[MsnShip]`, `[MsnFormation]`, `[MsnRandEnc]` and `[MetaBehavior]`. For example, a trigger runs its actions in order, and the game reads its lines from the first `nickname` line. A patch by names puts its lines at the place of the first line that they replace. So such a patch cannot keep the order of these sections.

`ol.replace = true` replaces all lines of a section of these types:

- The patch section finds its target by its first `nickname` line, wherever that line is in the patch section.
- All lines of the target get the lines of the patch section, in patch order, without the `ol.` lines. The section keeps its place in the file. Lines before the `nickname` line stay before it.
- `ol.replace` is only for these types in a mission file. On another type, the merge fails with the error "ol.replace is only for sections whose line order counts."
- `ol.replace` cannot go with `ol.new`, `ol.delete`, `ol.remove` or `ol.parent`. With `ol.rename`, the merge replaces the lines first, then gives the `nickname` line the new key.
- The n-th line of a name gets the value types of the n-th line of the name in the target. A change does the same (see "Value types in binary files").

`ol.base = <hash>` holds a hash of the section that the patch replaces or deletes, as the editor read it from the lower layers. The hash is 16 hex digits of the SHA-256 of the section: each line with its key in lowercase and its values. A number gives its value, so `3` and `3.0` give the same hash. A text keeps its letter case, but `true` and `false` are in lowercase. So a binary section and the same section as text give the same hash. When the section of the lower layers has another hash, the merge gives a warning: "Mod '{mod}' replaces {section} of {file}. The base of this section changed after the edit, and its changes go away. Compare the two, then keep your lines or use the new base." The patch still applies. A later save keeps the hash of the patch, also when it saves another edit of the section. So the warning stays until you decide.

`ol.before = <type>, <key>` goes with `ol.new`. The merge puts the new section directly before the section with that type and key, not at the end of the file. The first `nickname` line of a section gives its key, as for `ol.replace`. When the file has no such section, or more than one, the merge fails with an error. The place of a ship and a formation counts: a formation names ships of the sections before it.

`[Mission]` and `[Constants]` are single sections of a mission file, as `[SystemInfo]` is in a system file. A patch section of the type finds the one section of that type. When the file has two, the merge fails with an error. A patch changes their lines by names, as for other sections.

This patch of `DATA/MISSIONS/OL_TEST/ol_test.ini` changes the condition of a trigger whose `system` line comes before its `nickname` line, and adds a ship before its formation:

```ini
[Trigger]
system = Li01
nickname = ol_wave2_spawn
ol.replace = true
ol.base = 5be0c1f27a9d4410
Cnd_Destroyed = ol_wave1, -1, EXPLODE
Act_SpawnFormation = ol_wave2_form
Act_ActTrig = ol_wave2_done

[MsnShip]
ol.new = true
ol.before = MsnFormation, ol_wave2_form
nickname = ol_wave2_ship_3
npc = ol_pirate_npc
```

### Child sections

The engine attaches some section types to the section of the parent type before them. OpenLancer treats the run of these sections after a parent as its children:

| Child section | Parent |
|---|---|
| `[CollisionGroup]` | `[Ship]`, `[Solar]` |
| `[LOD]` | equipment |
| `[start_effect]`, `[destroy_group]`, `[destroy_hp_attachment]`, `[start_cam_particles]`, `[ignite_fuse]`, `[impulse]`, `[destroy_root]`, `[tumble]`, `[damage_group]`, `[damage_root]`, `[damage_hp_attachment]`, `[make_invincible]`, `[dump_cargo]` | `[fuse]` |
| `[MVendor]`, `[MRoom]`, `[GF_NPC]`, `[BaseFaction]` | `[MBase]` |

- `ol.delete` on a parent also removes its child sections.
- Child sections that directly follow a new parent section (`ol.new`) in the patch belong to that parent.
- To add a child section to an existing parent, use `ol.new = true` and `ol.parent = <parent key>`. The section goes after the parent's other child sections. A child with a key must not repeat the key of another child of the same type under that parent.
- A child section with a key (for example `[GF_NPC] nickname = ...`) can be patched like any other section.

### Markets

The engine adds the goods of all `[BaseGood]` sections for one base. So a `[BaseGood]` patch section is always added as a new section. Use it to sell more goods at a base:

```ini
[BaseGood]
base = li01_01_base
marketgood = my_package, 0, -1, 1, 1, 0, 1, 1
```

The values of `marketgood` are: good, rank, reputation, min, max, transaction type, price multiplier. The transaction type is 0 when the base buys and sells the good, 1 when the base only buys it, and 2 when the base only sells it. The engine sells a ship only when min or max is not 0. A patch cannot change or remove a vanilla market line. Use a whole-file override for that.

### Two mods that change the same entry

The mod that loads later wins. The merge reports a warning that names both mods.

### freelancer.ini

A patch of `EXE/freelancer.ini` can only add `[Data]` lines, for the new data files of the mod. It needs format version 2. Each section of the patch is a `[Data]` section with `ol.append = true`:

```ini
[Data]
ol.append = true
equipment = MODS\my-mod\equipment.ini
goods = MODS\my-mod\goods.ini
```

- The game reads the `[Data]` lines in order, and the order is the load order. So each line goes after the last line of the same key, for example a new `solar` line stays before `universe`. A line with a key that `[Data]` does not have goes at the end of the section.
- Each path is relative to the data folder. The game or a mod must have the file, for example in `files/DATA/MODS/my-mod/`. Else the merge reports an error.
- A line that `[Data]` already has, with the same key and the same path in any letter case, is an error.
- Each other section or directive in a patch of `freelancer.ini` is an error. A mod of format version 1 cannot patch `freelancer.ini`.

For other changes, a mod can replace `freelancer.ini` as a whole file in `files/`. A whole file cannot use text placeholders. The export refuses it for a retail game (see "Export"). OpenLancer adds its text DLL to `[Resources]` of the file that results.

## Text: strings.json

```json
{
  "strings":   { "ship_name": "Starrunner" },
  "infocards": { "ship_info": "<?xml version=\"1.0\" encoding=\"UTF-16\"?><RDL><PUSH/><TEXT>...</TEXT><PARA/><POP/></RDL>" }
}
```

In a patch, or in an INI file in `files/`, write `$str:ship_name` for a string id (`ids_name`) and `$ifc:ship_info` for an infocard id (`ids_info`). OpenLancer puts the text of all mods into one generated DLL, `EXE/openlancer_strings.dll`, and adds it to `[Resources]` in `freelancer.ini`. The ids come from the load order and the keys. Refer to mod text only through placeholders, never through the numbers.

These keys hold a text id. The retail data confirms the kind of text of each key:

| Key | Placeholder |
|---|---|
| `ids_name`, `ids_short_name`, `strid_name`, `tradelane_space_name` | `$str:` (a string) |
| `ids_info`, `ids_info1`, `ids_info2`, `ids_info3` | `$ifc:` (an infocard) |
| `ids_info` of a `[KeyCmd]` | `$str:` (a string) |

Freelancer also shows a string id in `ids_info` as an infocard with plain text, and some retail solars, objects and commodities use that. Librelancer shows only infocards, so use `$ifc:` for `ids_info`.

## Whole files

A file in `files/` adds a new file, or replaces the file at the same install path. Patches of later mods apply to the replaced file. A replacement discards the changes of the mods that load before it, and the merge reports a warning that names them. Put new assets under `DATA/MODS/<mod id>/` so that they do not collide with other mods.

The editor edits a whole file of the active mod under `DATA/MISSIONS`. Examples are a test script, a replaced slot script and a file of ship builds. Each section of such a file is editable, also a section whose key another section has. The save writes the whole file as text. A text file keeps its comments and the lines that do not change. A binary file becomes text, and the save tells it. When the active mod is in the OpenLancer repo, the save refuses a whole mission file. The content scan cannot tell such a file from a copy of a retail script. The editor shows the other whole files of the active mod, but it does not change them.

When the active mod has a whole mission file and a patch for the same path, the merge applies the patch to the whole file. The editor shows the file as the game gets it, but it does not change it. Move the lines of the patch into the whole file, then delete the patch.

### Editor files of missions

The editor keeps the graph of each mission script in `graphs/<install path>`, and each mission prefab of the mod in `prefabs/missions/<id>.ini`. The game, the merge, `oltool deploy`, the validation and the packages ignore these folders. The editor writes them at the save of the mod, in the same save as the patches. Undo and the recovery file of unsaved edits cover them. A graph file without content is not written, and the save deletes an old one with the empty folders below `graphs/`. The editor files of the lower mods are read-only.

A graph file holds a `[Node]` record for each trigger node that you moved or placed. A linked prefab instance gets a `[Node]` record for each member trigger, and the graph places the frame of the instance around these nodes. The other nodes take the automatic layout. It holds the `[Act]` records after your first edit of an act. That edit writes all acts that the editor proposed, so you start from them. A proposed act has no `name` line, so its title stays "Act {n}: {system name}". A trigger is in one act. A trigger that no act names takes the act that the editor proposes for it. The collapse state of the frames and the lens are not in the file. The editor does not change a graph file of another format.

```ini
; OpenLancer mission graph, format 1. The game does not read this file.
[Graph]
script = DATA\MISSIONS\OL_TEST\ol_test.ini
format = 1

[Node]
trigger = ol_wave_spawn
pos = 1200, 340

[Act]
nickname = act_1
name = The ambush
text = The escort meets the player at the gate.
member = ol_launch
member = ol_wave_spawn
```

When the script has no trigger with the name of a record, the editor tells it when the mission opens. The editor removes that record when it writes the file again.

A graph file also holds an `[Instance]` record for each linked prefab instance of the script (see "Behavior prefabs" in [MISSIONS.md](MISSIONS.md)):

```ini
[Instance]
id = ol_w1
prefab = mod:ol_gate
prefab_hash = 6d2a91c03b8e4f17
member = spawn, ol_w1_spawn
member = end, ol_w1_end
value = formation, ol_form
override = delay, 7
lines_hash = 0b47e3f5a1c96d28
```

| Key | Meaning |
|---|---|
| `id` | The id of the instance. The nicknames of its sections start with it. |
| `prefab` | The prefab: `builtin:<id>` for a prefab of the editor, or `mod:<id>` for a prefab file of a mod. |
| `prefab_hash` | The hash of the prefab at the last compile, 16 hex digits. When the prefab changes, the instance gets the warning P03. |
| `member = <role>, <nickname>` | The section of each role of the prefab. |
| `value = <field>, <row>` | The value of a field that each instance sets. A field with several rows has one line for each row. |
| `override = <field>, <row>` | The value of a field that differs from the prefab. |
| `removed = <role>` | A member that the user deleted. The prefab keeps it until the user applies the delete. |
| `port = <port>, <trigger>` | A trigger of the mission that a port of the prefab starts. |
| `lines_hash` | The hash of the compiled lines, 16 hex digits. Other lines of the members are a hand edit, and the instance gets the warning P01. |

A prefab file is plain INI with the comment of the editor at the top. The editor reads format 1. This example is the prefab of the record above:

```ini
; OpenLancer mission prefab, format 1. The game does not read this file.
[Prefab]
id = ol_gate
name = Gate ambush
kind = beat
format = 1

[Field]
id = formation
label = Enemy formation
kind = formation
per_instance = true
default = none

[Field]
id = delay
label = Spawn after
kind = number
unit = s
default = 5

[Port]
id = done
label = When all are destroyed
role = end

[Trigger]
role = spawn
nickname = {instance}_spawn
Cnd_Timer = {delay}
Act_SpawnFormation = {formation}
Act_ActTrig = {instance}_end

[Trigger]
role = end
nickname = {instance}_end
Cnd_Destroyed = {formation}, -1
Act_ActTrig = {port:done}
```

- `[Prefab]` is the first section. `id` is the file name, with letters, digits, `_` and `-`. The editor gives a new prefab a file name with lowercase letters only. `kind` is `beat` or `order`. `help` and `writes` are the texts of the info icon. `variant_of` names the prefab that a variant copies, as `builtin:<id>` or `mod:<id>`.
- Each `[Field]` has an `id`, a `label` and a `kind`. A kind is a value kind of the mission schema, for example `number`, `wholeNumber` or `formation`, or `list`, `triggers`, `condition` or `lines`. A word field has a `tokens` line with the id of a word set. A list field has one `column = <id>, <kind>, <label>` line for each column. A condition field and a lines field can limit their keys with `key` lines. `default` gives the value of a new instance, with one line for each row. `per_instance = true` makes a field that each instance sets, and `optional = true` lets a value field have no value.
- Each `[Port]` has an `id`, a `label` and the `role` of its section.
- Each other section is a template section of a mission section type. Its first line is `role`, and its `nickname` line uses `{instance}`.
- A slot puts a value into a line. `{field}` is the value of a field, and `{field.column}` is a column of a list field. `{port:id}` is the triggers of a port, and `{instance}` is the id of the instance. A line `{field} =` takes the line of a condition field, or the lines of a lines field.

The editor checks each prefab file and shows each problem with its line. A prefab with a problem does not place, and its instances get the warning P02.

## Prop groups

A prop group is a set of separate objects, zones and lights. The editor saves it in the `props/` folder of a mod, as Unity saves a prefab asset. The editor places a group again in any system, and each member becomes a separate item. The merge, the game, `oltool deploy` and the validation ignore the folder. So a group changes no game data until you place it.

A group file is plain INI with the comment of the editor at the top:

```ini
; OpenLancer prop group, format 1. The game does not read this file.
; The editor places each member as a separate object, zone or light.
[PropGroup]
nickname = transport_trio
name = Three transports
format = 1

[Object]
member = large_transport_2
pos = -285, 0, 20
archetype = large_transport
```

- `[PropGroup]` is the first section. `nickname` is the file name. It has lowercase letters, digits, `-` and `_`, and starts with a letter. `name` is ASCII text without `;`.
- The editor reads format 1. A file with a later format does not read: "OpenLancer cannot read prop group format 2. It reads format 1."
- Each member has the section type of the system file: `[Object]`, `[zone]` or `[LightSource]`. `member` replaces `nickname`, and each member id is unique in the file without letter case. A member id can hold a space, as some retail nicknames do.
- `pos` is relative to the anchor of the group. The other lines keep their text.
- `parent` and `ring` name a member id.
- A text placeholder `$str:<key>` or `$ifc:<key>` names a key of `props/<nickname>.strings.json`, in the format of `strings.json`.

The editor shows each problem of a group file with its line. The problems are: no `[PropGroup]` section first, a repeated member id, and a `parent` or a `ring` that names no member. Also an unknown section type, a `pos` that is not three numbers, and a text key that the file of the texts does not have. A group with a problem does not place.

### Save as prop group

"Save as prop group..." in the menu of a Hierarchy row saves the item and its `parent` children, again and again. You can also drag a Hierarchy row onto the category "Prop groups" of the Assets panel. This is how you drag a GameObject into the Unity Project window. The dialog shows the members with their positions, the lines that the group leaves out with their reasons, and the warnings.

- The member id is the nickname without its system, for example `large_transport_2` for `Li01_large_transport_2`. A repeated id takes the next number, as a copy does.
- The anchor is the center of the box that holds the member positions, in whole numbers.
- A `base` line goes, because a base has one object in space.
- A `parent` or a `ring` line that names an item outside the group goes. Such a line that names a member names its member id.
- A `dock_with` line stays in the file.
- A text of the active mod, and a text of a lower mod, go into the file of the texts. A numeric text id of the base data stays.
- A line that names an entry that the active mod did not save gets a warning. Save the mod, or the group names an entry that the mod does not have.
- The editor refuses connection objects, nebulae, asteroid fields, the zones that hold them, and items of more than one system file.

The editor writes the group at once, under the lock of the mod: the file of the texts first, then the group file. `Ctrl+S` does not save it, and Undo does not remove it, as in Unity. Rename (`F2`) moves both files and changes the `nickname` line. Delete asks first and removes both files. The placed copies stay.

The Assets panel shows the groups of the active mod and of the mods that load before it. Only a group of the active mod can change. The editor reads the folders again when its window gets the focus, so a group that another program wrote shows.

### Place a prop group

A drag of a group tile into the Scene places the group, and so does "Place at the camera pivot". The placement is one entry of the edit history, "Place <name>". One Undo removes all members.

- Each member gets the nickname `<system>_<member>` in lowercase. When an item has the nickname, the member takes the numbering of a copy. A light checks the lights of the file only.
- The position is the point of the drop plus the relative position, in whole numbers. Each member keeps its `rotate`.
- The members go to the end of the system file, the parents first. The first member is selected.
- A drop on an object row of the Hierarchy gives each root object `parent = <that object>`.
- A `dock_with` whose base is in another system goes, with a warning. Add the Dockable station component to pick a base.
- Each text of the group becomes a new text of the active mod.
- The copies keep no link to the group, as a Unity prefab that you unpack.

The placement checks each planned line with the data lists before the line goes into W. A line with an error never reaches the mod. The dialog lists each such line with what the game does with it, and offers "Place without these lines" and Cancel. A required line cannot go, so an unknown archetype refuses the whole placement: "<member>: the archetype <value> is not in this profile. The game drops this object. Add the mod that has <value> to the profile."

The game package of an export leaves `props/` out, because the game does not read it. The package of the target OpenLancer also leaves it out (see "Package of the target OpenLancer").

## Cutscenes

A cutscene script (`.thn`) is a Lua 3.2 script. The retail scripts are bytecode. The engine also reads Lua text, so a mod gives its cutscene changes as text. A mod changes a cutscene in one of two ways:

- `patches/<install path>.thn` is a THN patch. It holds only the changed values. Use it to change a retail cutscene or a cutscene of another mod.
- `files/<install path>` is a whole script as Lua text. Use it for a new cutscene, under `DATA/MODS/<mod id>/CUTSCENES/`. Do not copy a retail script into a mod.

New cutscene in the editor writes `files/DATA/MODS/<mod id>/CUTSCENES/<name>.thn`. An INI line names a cutscene with its path below `DATA`, for example `start_script = MODS\my-mod\CUTSCENES\bar_shot.thn`. The engine finds the file through the data folder of the merged files. A new cutscene plays in OpenLancer. A new cutscene is a text file, and nobody checked a new cutscene in the retail games yet. So the editor marks it "not checked in the retail games".

The merge reads the script as the mods before the patch leave it, and applies each THN patch in load order. Then it writes the result as canonical Lua text. `oltool thn` prints that text, and `oltool deploy` writes it into the deploy folder. The cutscene editor shows each cutscene, and it saves the edits of a lower cutscene as a THN patch (see [CUTSCENES.md](CUTSCENES.md)).

### Values

| Lua text | Value |
|---|---|
| `20`, `-1.5`, `1E-07` | A number. The engine stores it as a 32-bit float. |
| `"Cam_02"` | A text. Write `\"`, `\\` and `\n` for a quote, a backslash and a line end. |
| `Y`, `N` | True and false. |
| `CAMERA`, `SET_CAMERA`, `LIT_DYNAMIC` | A name of the THN environment. |
| `{ 1, 2, 3 }`, `{ fovh = 20 }` | A table: a list part, a record part, or both, for example `{ 1, 2; key = 3 }`. |

- THN content is ASCII. The merge refuses a patch with other characters.
- `-0` becomes `0`. A number that is not finite is an error, for example `1e39`, which the engine reads as infinity. NaN has no text.
- A name stays a name, and a number stays a number. The engine reads `type = 3` and `type = CAMERA` as the same type.

### The THN patch

```lua
-- My mod: a wider Trent shot, and a later cut to the medics.
ol_thn_patch = 1
entities = {
	{ ol_match = "Cam_02_Intro_Trent_A", ol_set = { ["cameraprops.fovh"] = 20 } }
}
events = {
	{ ol_match = { 25.75, SET_CAMERA, { "Cam_Monitor_s002xe", "Cam_03_ACOH_Trent_Medics_A" } }, ol_set = { time = 27 } }
}
```

This patch is `patches/DATA/SCRIPTS/STORY/s002xe_nrml_sinclair_li_01_Cityscape_01.thn`. It makes the first Trent shot of the Manhattan intro wider, and the cut to the medics 1.25 s later.

| Global | Meaning |
|---|---|
| `ol_thn_patch = 1` | Required. The version of the THN patch format. It is the first statement. Only comments (`--`) and empty lines can come before it. |
| `duration = <seconds>` | Optional. The new length of the script. |
| `entities = { ... }` | The entity entries, in order. |
| `events = { ... }` | The event entries, in order. |

| Entry | Meaning |
|---|---|
| `{ ol_match = <key>, ol_set = { [path] = value, ... }, ol_remove = { path, ... } }` | Changes one element. `ol_set` and `ol_remove` are optional. The removes run before the sets, so an entry can move a key to the end of its record. |
| `{ ol_match = <key>, ol_delete = Y }` | Deletes one element. |
| `{ ol_new = <table> }` | Adds an element at the end of its list. For an event, the table is `{ time, TYPE, { targets }, { props } }`. |
| `ol_nth = n, ol_count = m` | Use them in an entry whose key repeats in the script. The element is the n-th of the m elements with that key. Without them, the key must be unique. |

A key finds an element:

- An entity key is the entity name, for example `ol_match = "Cam_02"`. Letter case does not matter, because the engine compares names without it. An entity without a name has the key `""`.
- An event key is `{ time, type, { targets } }`, as the script has them. The time must be the same number. The type compares by its number, so `SET_CAMERA` and its number are the same type. The targets compare by their text and their order.

A path names a value of the element:

- A path is a text of record keys joined with `.`, for example `"cameraprops.fovh"` or `"spatialprops.pos"`.
- An event has four slots: `time`, `type`, `targets` and `props`, for example `"time"` or `"props.duration"`. Only `props` holds keys. A patch cannot remove `time`, `type` or `targets`.
- A path ends at a value. A list, for example `pos = { 1, 2, 3 }`, is one value. An empty table is a record, so a path can go into an empty `psysprops`.
- A set of a new key adds the key at the end of its record. A set also makes each missing record on its path. For example, `"props.duration"` makes the props of a `SET_CAMERA` event, which has none.

### How the merge applies a THN patch

1. All keys find their elements in the script before the patch. Then the removes, the sets and the deletes run. Then the new elements go at the end of their lists, in patch order. So a delete does not move a later match.
2. A key that finds no element is an error. So is a key whose number of elements differs from `ol_count`. The error names the file, the entry and the key. Any error stops the whole patch, and the script stays as the mods before it leave it.
3. A set into a value that is not a record is an error. A remove of a missing key is a warning.
4. When two mods change the same path, or one path is in the value of the other, the later mod wins. This is also true when two mods set `duration`. The merge reports a warning that names both mods.
5. A rename sets `entity_name`. The next patch in load order then uses the new name.
6. An unknown `ol_` key is an error. A key or a global that the format does not have is an error too.
7. A lower mod can delete or add an element with a key, or change the key of an element. A later entry with `ol_nth` for that key can then name another element, also when the count stays the same. The merge reports a warning that names both mods and the key, and the patch applies. Check the result.

An edit can give an event the time, the type and the targets of another event, for example a move of an event onto the start of the next motion of the same character. The key of these events then repeats. A patch of a later mod that names one of them without `ol_nth` then fails with rule 2, and that mod must save its patch again in the editor. Duplicate in the editor puts its copies after their sources, so a copy does not repeat the key of its source.

The engine runs the events of one time in their order in the script. A new event goes at the end of the list, so it runs after the other events of its time. To run an event before them, delete those events and add them again as new events after it. Do not do this when one of those events has a key that repeats, because the `ol_nth` of a later mod can then name another event.

A whole file of a later mod replaces the script and discards the patches before it, as for INI files. A tool that cannot run the engine VM cannot merge a THN patch. It reports "This tool cannot merge cutscene patches." THN patches are part of format version 1. An older OpenLancer refuses a mod with a THN patch: "A patch must be an .ini file."

### The validation of cutscenes

`oltool validate` checks each cutscene script that the mods add or change: each whole file and each merged patch. It reports the errors that the merged script has and that the lower data does not have. An error is a value that stops the engine, so the cutscene does not play, or a text that breaks the rules of this format:

- The script does not compile, or it holds a number that is not finite.
- A script text has a character outside ASCII, also in a comment. The engine runs the script, but its VM reads each text as ASCII.
- `duration` is not a number, or `entities` or `events` is not a table.
- An entity has no text `entity_name`, or a `type` that the engine cannot read.
- An event has no number time, no type with an event class of the engine, or no table of text targets. Or the props of an event are not a table.
- A key holds a value of another kind than the engine reads. An entity reads its keys with casts: `cameraprops.fovh = "20"` stops the engine, because the engine reads a number there. An event reads its props with conversions, which also take a text with a number or a THN name, for example `time_scale = "2"`.
- An entity with `lightprops` has no light type or no `spatialprops.pos`. A `path_data` text is not a path that the engine reads, or it holds more than `OPEN`, `CLOSED`, numbers and tables of numbers. The engine reads `pathprops.path_data` of an entity of each type, so the check reads each entity.
- An event has fewer targets than its type reads: two for SET_CAMERA, ATTACH_ENTITY and START_PATH_ANIMATION, and one for most other types. The second target of SET_CAMERA names an entity of the script that is not a CAMERA. The second target of START_PATH_ANIMATION names an entity of the script without `pathprops.path_data`.
- The scene cannot build an entity:
  - a LIGHT without `lightprops`, or a SCENE without an `ambient` color.
  - a COMPOUND with a template, but without a known `userprops.category`, or with a solar, a ship, a room or a cart that does not exist. A solar, a ship, an equipment or an asteroid without a model file also stops the engine. Such an item has no `DA_archetype` line, or the file of the line does not exist. The engine also makes the equipment of some section types without a model, for example `[Light]`, `[Commodity]`, and `[Munition]` without a `motor` line.
  - a DEFORMABLE without `userprops.Actor`, with a costume that does not exist.
- A START_MOTION event on a character has no `props.animation`. The engine then has no clip to look for, and it stops.

The check also reports a template that names no item as an error, where the engine does not stop. Such templates are the prop, the equipment or the asteroid of a COMPOUND, and the effect of a PSYS. The engine reads the template of a PSYS as it is, also a template that starts with `$`. The check compares these problems by the value and the text, without the entity. So a mod that renames an entity with such a problem of the lower data gets no new problem.

The check also reports the clip of a START_MOTION event on a character as an error when the game does not have the clip. No clip has an empty name. The character then does not move. The clips are the scripts of the `.anm` files that the `[Animations]` lines of bodyparts.ini name, as the engine loads them. The check reads these files once for each view, and only for a script with such an event. A clip on a COMPOUND names a script of its model, so it gets no check. An `.anm` file that does not read gives one warning, and the check keeps the clips of the other files. The game does not: when it cannot read such a file, it closes during its load.

The scene does not make a REFERENCE entity, or an entity that a later entity with the same name hides. So the checks of the scene skip these entities, and a target with the name of such an entity names an object of another script of the scene. `flags` gives REFERENCE also as a name of another group, because the engine casts the name to its number: `flags = CAMERA` is 3, which is REFERENCE and LIT_DYNAMIC. A room fills in the `$terrain_*` templates with the terrain of its base, so the check reads the template of each base that plays the script. The game makes no COMPOUND of a script that only `[Trigger] Act_CallThorn` lines name, because it plays such a script in space with the objects of the mission. So the checks of a COMPOUND skip such a script.

The check does not know the objects of the other scripts of a room. So a target that names no entity of the script is not an error, also for SET_CAMERA and START_PATH_ANIMATION. The engine stops when no script of the scene has that name.

A THN patch that does not apply is a merge error. Each problem of a cutscene has its place: the cutscene, the element with its key and the path of the value. A click on the problem in the Console of the editor opens the cutscene and selects the element. When an edit after the save moved the element, the key finds it. When the key names no element or more than one element, the click selects the cutscene.

## Rules

- INI content and THN content in a mod must be ASCII. Text in `strings.json` can use any characters.
- Do not copy vanilla files or vanilla sections into a mod. Change only the entries that you need.
- `tools/content_scan.py` rejects retail file types and files that are byte-identical to retail files. It cannot find decoded INI text or copied sections, so follow the rule above yourself.
- The scan accepts a `.thn` file only as a THN patch: ASCII text below the `patches/` folder of a mod (the `patches/` folder next to its `mod.json`) whose first statement after its comments is `ol_thn_patch = 1`. A `.thn` file below `files/` fails the scan, also in a folder with the name `patches`.

## Profiles

A profile is a JSON file:

```json
{ "base": "../base", "mods": [ "../mods/my-mod", "../mods/other-mod" ] }
```

Paths are relative to the profile file. `base` defaults to `../base`. `base` can also be an absolute path, for example the folder of your game: `{ "base": "C:\\Program Files (x86)\\Microsoft Games\\Freelancer", "mods": [ "../mods/my-mod" ] }`. The base folder is the folder that contains the `EXE` and `DATA` folders.

The editor makes new profiles in the workspace folder, by default `Documents\OpenLancer`: the profile goes to `profiles\<name>.json` with the absolute path of the game as its `base`, and its first mod, `my-mod`, goes to `mods\my-mod`. See [GAME_LINK.md](GAME_LINK.md). OpenLancer never writes into the base folder. So the editor refuses a workspace folder whose `profiles` or `mods` folder is in a game folder. A profile file or an active mod in its base folder opens read-only, and Play and Mod > New mod refuse such a profile.

## The editor

The editor saves each edit as a patch in the active mod. The patch holds only the difference from the data that the mods before the active mod give. The editor never writes the base data or another mod.

### The active mod

The Mod menu selects the active mod of the profile. The editor stores the choice for each profile. Without a choice, the last mod of the profile is the active mod. "None (read-only)" shows the whole profile and takes no edits. "New mod" makes the folder `<id>` with a `mod.json` in the `mods` folder next to the `profiles` folder, adds it to the end of the profile, and makes it the active mod.

The editor shows the edit view: the base data with the mods up to the active mod. The game also applies the mods that load after the active mod. When the profile has such mods, the Console and the Mod menu name them, and a warning glyph marks each value that one of them changes. The game uses the value of the last mod.

The files of a mod in the OpenLancer repo, for example `samples/mods/ol-sample`, are tracked files. For such a mod, the editor takes no edit, undo, redo or save until you allow the edits one time in each session: in the Inspector or in the Mod menu. A restore of the unsaved edits of an earlier session also allows them, and the restore dialog tells this.

### The Mods window

**Mod > Mods...** opens the Mods window. It lists the mods of the open profile in load order, then the other mods of the `mods` folder of the workspace. The workspace is the folder above the folder of the open profile. Without an open profile, it is the workspace folder of the editor. **Show** and the search field filter the list. The search finds the display name, the id and the tags.

A row shows the display name, the id and the version. A blue dot marks unsaved or unapplied changes. A yellow triangle marks a change after the last export. The last glyph shows the state of the checks. A mod whose `mod.json` does not load shows its error, and Open folder is its only action. The window reads the mods again when it gets the focus, after each of its actions, after a save, after an export and on **Refresh**.

The page of the selected mod has these parts:

- The header: the display name, the id, the version, the authors and the chips of the state. The tooltip of a chip tells more. **Export...** opens the Export dialog of the mod. **Play** plays the mod alone in a linked game (see "Play in the game" in [PLAY.md](PLAY.md)). The **...** menu has Make active mod, Duplicate..., Rename..., Add to profile or Remove from profile, Open folder, Open export folder and Show in Console.
- The publish checklist: for example "4 of 6 steps complete", and one row for each step. A missing step tells what is missing and why. Its buttons go to the field or fix the step. The recommended items do not count.
- The tab **Settings**: the display name, the version and the target games of `mod.json`, and the publish settings. Each list has Add, Remove, Move up, Move down and a drag handle. Increase version gives the next patch, minor or major version, and adds a changelog entry for it. **Add entry** adds a changelog entry at the top, because the changelog lists the newest version first. The entry of a version with the mark "published" is read-only: you cannot change, move or remove it.
- The tab **Preview**: the parts of the Nexus page in the order of the page, each with Copy. Copy copies the text of the field in `nexus-page.txt`. The README of each package, the header of the FLMM script and `CHECKLIST.txt` come from the last check. Click **Check now** to make them.
- The tab **History**: the exports of the publish history, newest first, with the state of their files, and the test plays. **Mark as published...** gives a version the mark "published".

| Step of the checklist | Complete when |
|---|---|
| Display name | The display name is not empty, not "My mod" and not the mod id. |
| Summary | The summary is not empty. |
| Version | The version follows the rule of "Versions", and it is higher than each version with the mark "published". |
| Checks for each target | The last check of each target has the current content, the current linked game and no error. |
| Test play in the game | The history has a test play of the current content in a retail game, or a mark "tested". A mod with only the target OpenLancer has no such step. |
| Export made | The exports of the current version with the current content and texts have a package for each target. |

The Settings tab edits a draft, as the import settings of Unity do. A changed field has a blue bar and a blue label until you click **Apply**. **Revert** drops the changes. A change of the selected mod or of the active mod asks first: Apply, Revert or Cancel. A close of the window, of the profile or of the editor asks the same. The title of the window has `*` while the draft has changes.

Apply checks that `mod.json` and `publish.json` did not change on disk after the window read them. When a file changed, Apply asks: Load again, Replace or Cancel. Apply copies the new screenshots first. Then it writes `publish.json` and `mod.json`. When an image in `publish/screenshots` is no longer in the list, Apply asks before it deletes the file. **Keep the files** is the default answer, because a delete is permanent. A change of the target games opens the profile again, because the save gate reads the targets when the profile opens.

The window never writes into a game folder, and it never writes a file of another mod. A mod in the OpenLancer repo needs "Allow edits of this repo mod" first, and Add images refuses it. The window does not write a mod that another editor has open. A `mod.json` with a comment is read-only, because OpenLancer cannot write the file and keep the comment.

A check runs the steps of the export of each target, but it writes no package. The steps are the merge, the format checks, the content checks, the FLMM script and the game file rule. The checks run on a worker thread, one mod at a time. The publish history records the result of each target. **Check all** checks each mod of the list, and the window checks the selected mod when its last check is old.

Rename gives the mod a new id and a new folder name. Before it changes a file, it shows a plan. The plan lists the folder, the profiles that name the folder, the history folder, the settings of the editor that change and the warnings. When another mod names the old id in `dependsOn`, Rename refuses. Rename waits while a game of a play copy runs, an export runs or a check of the mod runs. Rename the mod only before the first release, because players keep the files of the old id. Duplicate makes a copy with a new id and a new display name. The copy has no page link, and it is not in a profile.

### Edits in the Inspector

The Inspector edits ships, equipment, goods, the other sections of the item files, the market lines, and the objects, zones and lights of the open star system and the sections that set the fields of a system. The edits go into the active mod. A field shows each line of a key on its own text line, with the values separated by `, `. The editor reads the text as the merge reads a patch line, so a value gets the type that a save and a reload give it (see "Value types in binary files"). For example, in a binary file, `true` that replaces the text `TRUE` stays `TRUE`, and `200` that replaces a float is a float. An empty field removes the key.

The editor checks the text against the type that the engine reads: a number, a whole number, `true` or `false`, a name of a list, or the number of values of a vector or a color. The editor refuses text that a patch line cannot hold, for example a value with `;` or with characters outside ASCII.

Each field edit is one entry of the edit history (Edit > Undo, `Ctrl+Z`, and Redo, `Ctrl+Y` or `Ctrl+Shift+Z`). Each of the X, Y and Z fields of a vector is a field of its own. A field that ends with the text it started with is no edit. So it keeps the line of the patch. Escape ends a field and cancels its edit.

A vector row and the size row of a zone have a label in front of each value: X, Y and Z, or Radius, Height, Outer and Inner. Drag a label to the left or to the right to change its value. Each point of mouse motion changes the value by one step. `Shift` gives steps of 0.1 times the step, and `Ctrl` gives steps of 10 times the step. When you hold both keys, `Shift` wins. A key that you press during a drag changes the speed from that point on, and the value does not jump. The tooltip of a label gives the steps of its value.

| Value | Keys | Step for each point | `Shift` | `Ctrl` |
|---|---|---|---|---|
| Position | `pos` | 10 | 1 | 100 |
| Rotation | `rotate` | 0.5 degree | 0.05 degree | 5 degrees |
| Size of a zone | `size` of a `[Zone]` | 10 | 1 | 100 |
| Coefficient | `attenuation`, `emit_attenuation` | From the dragged value. 0.0000056 gives 0.00000001. A value of 0 takes the step of its line, as in the next row. | step x 0.1 | step x 10 |
| Other vector | for example `spin`, `direction`, `rotation_inertia` | From the largest value of the line. 2800 gives 10, 1 gives 0.01, and 0.002 gives 0.00001. A line of zeros gives 0.01. | step x 0.1 | step x 10 |

A step that comes from a value is its power of 10 divided by 100: 10^(e-2) for a value from 10^e to 10^(e+1). The step of the other vectors goes from 0.000001 to 10,000. The step of a coefficient goes from 0.000000001 to 10,000. The step stays the same from the start of a drag to its end. So after a drag from 950 to 1050, the next drag has a step 10 times larger. In the Scene, `Ctrl` reverses the snap. In the Inspector, `Ctrl` gives large steps.

One drag is one entry of the edit history. The Scene shows each change at once. `Esc` or the right mouse button cancels the drag, and the value goes back. A drag that ends where it started, or that comes back to its start, is no edit. The drag goes on when the mouse leaves the editor window, up to the edge of the screen. To go farther, release the button and drag again. A label does not drag when the row is read-only, or when a value of the line is not a number. Then its tooltip tells why. Correct the value in the field first. A single number, for example `edge_fraction`, has no label drag.

A color row with one line has a swatch in front of its text field. Click the swatch to open the color picker. The picker keeps the form of the key:

- Whole numbers from 0 to 255 for most color keys, for example `color` of a `[LightSource]`, `space_color`, `burn_color` and `property_fog_color`. The inputs of the picker show 0 to 255.
- Numbers from 0 to 1 with 3 decimals for a `FloatColor` key: `inner_color` and `outer_color` of a `[star_glow]` in `stararch.ini`. The inputs of the picker show 0 to 1. No Inspector page shows that file yet.

The picker changes the first three values of the line. A fourth value, for example the alpha `128` of `color = 120, 250, 255, 128`, stays. The text field can change it. The Scene shows each change at once, for example the light of the system. A click outside the picker keeps the color. One opening of the picker is one entry of the edit history. `Esc` puts back the color of the opening, also while an input of the picker is active. **Original** in the picker shows the color of the opening, and a click on it puts back that color.

The swatch is disabled, and its tooltip tells why, when the line has fewer than three values, when one of the first three values is not a number, or when one of them is outside the range of the form, for example `color = 300, 0, 0`. Correct such a line in the text field. A key without a line, or with two lines or more, has no swatch. Type the first value in the text field. A `Vector3` color factor, for example `color_shift`, has the label drag and no swatch.

Each change of the text shows at once. While a field is active, it stays in its place, also when its text adds or removes the key. After the field ends, the key moves to its list, for example from "Unset keys" to "Keys". Two kinds of field change the data only when the field ends: the text of a key that holds a text id, for example a name, and a market line.

A blue label marks a value that the active mod sets. The tooltip of the label names the layer that sets the value: the base data, a mod that loads before the active mod, or the active mod. A lock tells why a row cannot change, for example the key of a section, or a section that a patch cannot change.

Revert, in the menu of a row or in the item menu, puts back the lines of the lower layer: the base data with the mods that load before the active mod. The next save then writes no line for them. A section that the active mod adds has no lower lines, so it has no Revert. A key of a group of lines that belong together (see "Lines that belong together") reverts only with its whole section. When the active mod sets the `file` of a `[system]` of `universe.ini`, the Revert of the whole item is not available, because the revert would move the contents of the system to another file.

"Markets" lists the market lines of a good, by base. Each value of a line has its own field: "Rank", "Reputation", "Minimum stock", "Maximum stock" and "Price multiplier". "Buys or sells" under them sets the transaction type: "Buys and sells", "Only buys" or "Only sells". An edit of a field changes only its value. The engine ignores a line without the price multiplier. So an edit of a shorter line also writes the values of a new line in the missing places.

The market lines of a lower layer are read-only, because a patch cannot change them. "Add to base" adds a line to a `[BaseGood]` of that base that the active mod adds. Without one, a new `[BaseGood]` goes into the market file in which the base sells goods of the same category, else into the first market file of the base. The editor refuses a base that already sells the good in a market file.

### Edits in the Scene

The Scene edits the objects, zones, lights, nebulae and asteroid fields of the open star system. Click an item in the Scene or in the Hierarchy to select it. The Scene, the Hierarchy and the Inspector then show the same item. You can select one item at a time. The gizmo on the selected item has three tools, and each tool changes one key of the section of the item:

| Tool | Key | Items |
|---|---|---|
| Move (`W`) | `pos` | objects, zones, lights, nebulae and asteroid fields |
| Rotate (`E`) | `rotate` | objects, zones, nebulae and asteroid fields |
| Scale (`R`) | `size` | zones, nebulae and asteroid fields |

A nebula is its `[Nebula]` section of the system file, and an asteroid field is its `[Asteroids]` section. As in a Unity GameObject, the item has a Transform: its zone. The gizmo on a nebula or a field changes the `pos`, `rotate` and `size` of its zone. Some retail nebulae and fields share one zone, for example the smog clouds of Br04. A change of a shared zone changes both items, and the Area section of the Inspector tells it.

A click selects the first item of the stack of items under the mouse. The stack has four layers:

1. The glyphs under the mouse: first the objects and lights, then the nebulae and fields. A glyph draws on top of the 3D scene, so a click on a glyph selects its item.
2. The objects that the ray hits, the nearest first.
3. The nebulae and fields that you can see. First the volumes that the ray enters, the nearest first. Then the volumes that hold the camera: first the nebulae in file order, then the fields, the smallest first. The game shows the fog of the first nebula. Inside a nebula, the fog covers the view, so a click on empty space selects the nebula.
4. The zones: first the zone diamonds that show. With the Zones toggle on, the patrol paths and the zone volumes follow, the nearest first. The zone volumes that hold the camera come last, the smallest first.

A click again at the same place selects the next item of the stack, and so does `Alt` + click at any place. After the last item, the first item comes again. A caption at the bottom-left of the Scene tells the place in the stack, for example "Item 2 of 4 under the mouse". A plain click selects the first item again when you move the mouse or the camera, or when you selected another item in the Hierarchy. A click on empty space clears the selection. `Alt` + click on empty space keeps it, because `Alt` + drag orbits the camera.

A nebula or a field is in layer 3 only when you can see it. With the Zones toggle on, each volume shows. With the toggle off, a nebula shows when the Nebulae toggle is on and the game draws it. With the fog on and the camera in a nebula, only that nebula draws. Without the fog, a nebula draws a cloud only for a sphere or ellipsoid zone with `fill_shape` or puffs. A nebula with puffs also holds its outer puffs, up to `bit_radius * (1 + bit_radius_random_variation)` outside its zone.

A field shows when its asteroids draw: the camera is within the largest size of its zone plus its `fill_dist` of the zone center. A field with a band draws its band from any distance. The editor does not test single asteroids: the volume of the field holds each asteroid that draws.

A nebula and a field have a glyph at the center of their zone. A nebula glyph never fades and shows with the Nebulae or the Zones toggle. A field glyph fades out at 100,000 and shows its label only on hover and selection. The selected nebula, field or zone shows its volume and an accent outline of its zone shape.

- The engine reads no rotation and no size for a light, and no size for an object. The tooltip of a tool button tells why the tool is not available.
- `X` changes between Local and World: a move or a turn along the axes of the item, or along the world axes. A scale always uses the axes of the item. A turn is about one axis.
- The scale handles are the values of `size` that the shape uses. A sphere has its radius, and a cylinder has its radius and its height. An ellipsoid, a box and a ring have three values. A scale changes only these values. When the zone has no `size` line, a scale writes all the values of the shape.
- Scale is not available for an ellipsoid, a box or a ring with two values in `size`. The engine then gives one axis the size 0. Give the line three values first. Scale is also not available when a value that the shape uses is 0 or less.
- A key does what its toolbar button does. So `W`, `E` and `R` select only a tool that the item takes, and `X` does nothing for the scale tool. The keys work while the mouse is over the Scene or the Scene has the focus, and no text field is active. They do not work during a drag, or with `Ctrl` or `Alt`. While the right mouse button is down, `W`, `A`, `S`, `D`, `Q` and `E` fly the camera.

The Snap button of the Scene toolbar (a magnet) makes a drag move by steps. The default steps are 100 for a move, 15 degrees for a turn and 100 for a size. The Snap steps menu next to it changes the steps. `Ctrl` during a drag snaps when the snap is off, and does not snap when it is on. A move snaps along the axes of its space, so a snapped Local move of a turned item can give fractions in `pos`. A snapped size is never below 1.

One drag is one entry of the edit history. The item moves at once, and the Inspector shows the new line. The drag ends when the left mouse button goes up, also over another panel. `Esc` or the right mouse button cancels the drag, and the item goes back. Undo, redo, save and the recovery file wait until the drag ends. A drag that ends where it started is no edit.

A move or a turn changes only the selected item. The zones, the trade lane paths and the other objects that belong to the item stay in place, for example the docking ring of a planet. To add, copy, rename or delete an item, see "Create, duplicate, rename and delete".

The numbers that a drag writes:

- The drag writes only its key. The other lines of the section stay as they are, and so does each value of the line that the drag does not change.
- A changed value is an integer when it is a whole number from -16777216 to 16777216 and it replaces an integer or no value. Each other changed value is a float, also a whole number that replaces a float, for example `200.0`. `-0` becomes `0`.
- A changed angle of `rotate` rounds to 0.001 degree. Each rotation has two sets of angles. The drag takes the set that is nearest to the angles before the drag, from -180 to 180 degrees.
- A label drag in the Inspector and the color picker follow the same rules (see "Edits in the Inspector"). A label drag adds its steps to the exact text of the value at its start, so a drag of `0.3` by one step of 0.1 writes `0.4`. A changed angle wraps into the range from -180 to 180 degrees, then rounds to 0.001 degree. So a drag of `170` by 20 degrees writes `-170`. A changed size of a zone is 1 or more, as for a scale in the Scene.
- The picker rounds only the channels that it changes: to a whole number from 0 to 255, or to 3 decimals from 0 to 1. A channel whose rounded value does not change keeps its value, also a value that is not on that grid, for example `0.784314` or `120.5`.

For example, a snapped move of `Li01_14_depot_water` by 1,000 along the world X axis writes `pos = -37675, 0, -27705`. The save then writes `patches/DATA/Universe/Systems/Li01/Li01.ini` with the `nickname` line and the `pos` line of the depot.

The Scene shows each edit of an item or of a section of the system in the next frame. This includes a drag, an edit in the Inspector, an undo, a redo and a restore of the unsaved edits of an earlier session. A key that loads a model, for example `archetype` or `loadout`, shows after the field or the drag ends. While the game data loads again after a save, it shows after the load. A new name shows in the label, the Hierarchy, the window title and the system list before the save. The unsaved edits stay in the Scene when you open another system and then open the system again. When the Scene cannot show the state of a section, it keeps the last valid state of the item. The Inspector and the Console tell why. For example, the game drops an object whose `archetype` does not exist, and the editor keeps its last model. Such an archetype is an error that stops the save (see "The save gate"). An archetype that the active mod adds and did not save is in the list, so it is no error. The Scene shows it after the save and the game data reload. After the reload, the editor opens the system again when a section of it waited for the reload. The camera and the selection stay.

In these cases a section is read-only. The gizmo does not show, and the lock of each row tells why.

- Two sections of the same type in the file have the same nickname, for example the eight `[LightSource]` sections `light_lair_core_lower` of St02c. A patch cannot find one of them.
- The section has no key, for example `[Archetype]` of a system file.
- The active mod replaces the system file as a whole file.
- The editor takes no edits for the mod, for example when another editor has the mod open.

These keys are locked, and the other keys of the section take edits:

- The key of the section, for example `nickname`.
- The `encounter`, `faction` and `faction_weight` lines of a `[Zone]` (see "Lines that belong together").
- The `file` of a `[system]` of `universe.ini`, because a change moves all the contents of the system.

The gizmo also does not show when a line that it reads has an error, for example a `pos` line with text. Correct the line in the Inspector first.

### Create, duplicate, rename and delete

The Scene adds, copies, renames and deletes the objects, zones and lights of the open system, as Unity does with GameObjects. It also deletes nebulae and asteroid fields. Each action is one entry of the edit history, so one Undo takes back all of it. The actions start in these places:

| Place | Actions |
|---|---|
| The `+` button at the left of the Hierarchy toolbar | Create |
| The menu of a row of the Hierarchy | Create, Duplicate, Rename and Delete, then the other items |
| The menu of empty space and of a group row of the Hierarchy | Create |
| The item menu of the Inspector header | Duplicate, Rename and Delete |
| The Edit menu | Create, Duplicate, Rename and Delete |
| The keys, while the Hierarchy or the Scene has the focus | `F2` Rename, `Ctrl+D` Duplicate, `Delete` Delete |

The keys work only when no text field is active and no field edit or drag is open. A disabled menu item tells why in its tooltip. Jump gates, jump holes, airlock gates and trade lane rings are connection objects. A connection object has a `goto`, a `prev_ring` or a `next_ring` line, or an archetype of such a type. This version cannot add, copy or delete them, because the partner gate, the neighbor rings and the route files must change with them. A rename of a connection object works.

#### Create

The Create menu has five object templates, a zone of each shape, and a light:

| Template | Archetypes |
|---|---|
| Station | the `station` and `docking_ring` types with a `docking_sphere` line |
| Planet | `planet` |
| Sun | `sun` |
| Satellite or buoy | `satellite` and `waypoint` |
| Other solar | `mission_satellite`, `non_targetable`, `weapons_platform` and `destroyable_depot` |

An object template opens the picker of its archetypes. `Esc` makes nothing. The station then opens the picker of the bases, with the bases of the system first. The new station docks with the base that you pick (`dock_with`). `Esc` in the base picker makes the station without a base.

The Create menu lists only the stations with a docking sphere, because a base needs one. The Assets panel also places a station archetype without a docking sphere, as retail places one in Ku07. Such a station gets no base step. A tile is the pick of the archetype, so a spawn from the Assets panel opens no archetype picker (see "The Assets panel").

The new item goes to the orbit point of the Scene camera, as Unity puts a new object at the pivot of the Scene view. Its `pos` has whole numbers. Create on an object row of the Hierarchy puts a new object at the position of that object and gives it `parent = <that object>`. A zone and a light have no `parent` key, so they get the position only.

The nickname is `<system>_<template>_<n>` in lowercase with the first free n, for example `li01_station_1`, `li01_zone_1` or `li01_light_1`. The new item is selected, and its Hierarchy row opens the rename field. A zone gets the `size` of its shape. A sphere gets `1000`, an ellipsoid `2000, 1000, 2000` and a box `2000, 2000, 2000`. A cylinder gets `1000, 2000`, and a ring gets `2000, 1000, 200` (outer radius, inner radius, height). A light gets `color = 255, 255, 255`, `range = 5000`, `type = POINT` and `attenuation = 1, 0, 0.000001`. Without the attenuation, the engine makes a point light that is almost black at 1,000 m.

A new item has its full lines in the patch, with `ol.new = true`. A system file needs no `[Archetype]` line for a new object. The loaded game data does not have an unsaved archetype of the active mod. An object with such an archetype shows in the Scene after the save and the game data reload. The editor then opens the system again, so you need not open it yourself.

#### Duplicate

`Ctrl+D` copies the selected item at the same place, and the copy is selected. An object takes its `parent` children with it, and their children. The `parent` line of a child copy names the copy of its parent. The copies go at the end of the file, the parent before its children, so the launch object of a base does not change.

A nickname that ends with `_` and digits keeps its stem in the copy. The copy takes the first free number with the same count of digits, as Unity numbers "Cube (1)" and "Cube (2)". For example, the copy of `li01_station_1` is `li01_station_2`, and the copy of `Li01_depot` is `Li01_depot_1`.

- A copy leaves out the `base` line, because a base has one object in space. The Console tells it.
- A text placeholder of the active mod gets a copy of its text with a new key, as New from template does. A numeric text id stays.
- This version cannot copy nebulae, asteroid fields or the zones that hold them.

#### Delete

`Delete` removes the item, as Unity does:

- The child sections of the section go with it, and so do the objects whose `parent` names the object, again and again.
- A zone takes its `[Nebula]` and `[Asteroids]` sections, and the `[LootableZone]` sections that name it. The engine drops a nebula or a field without its zone.
- A line that names the item gets a safe fix in the same entry when one exists. The `ring` line of a planet goes. The lines of each hole of a deleted zone go from the `[Exclusion Zones]` sections of the nebula and field files. The engine skips a hole whose zone is not there. But retail gives the lines of that hole to the hole before it, so the lines must go.
- Another line keeps its value and shows "Missing" (see "Data lists and pickers"). Two cases give such a line. One is a fix of a line that the editor cannot change. The other is a line of a mission script that names a deleted object. The save stops at such a mission line until you fix it.
- A line whose rule refuses the delete names the line and stops the delete, as Unity RequireComponent does. This version refuses the delete of a jump gate, a jump hole and a trade lane ring.
- A delete of a nebula or an asteroid field removes only its section, as Unity "Remove Component" does. Its zone stays, with its encounters, its music and its damage.

A delete that removes only the item and its child sections runs at once. Another delete opens a dialog. It lists the items that go too, the lines that change and the lines that show "Missing". It also lists the files that name the item as text, and these warnings:

- A base that loses the last object of its system that docks with it. The game then puts the player at the center of the system after a launch.
- A base that loses the last object of its system with `base = <base>`. The navigation map then does not list the base, and its rooms show no planet.
- A mod that loads later and changes a section that goes. Its patch cannot find the section after the delete.

A delete of a read-only section is refused with the reason, for example a light of the St02c file whose nickname another light also has.

#### Rename

`F2` opens the nickname of the item in its Hierarchy row, as in Unity. The field checks the text while you type and shows the problem below the row. `Enter` or a click outside the field runs the rename. `Esc` cancels. The name text (`ids_name`) stays a field of the Inspector. A rename of a nebula or a field renames its zone, because the zone is the key of its section.

The rename changes each line that the data lists find:

- The `parent`, `next_ring`, `prev_ring` and `ring` lines of the system file, and the `goto` line of the partner gate in its system file.
- The `[Nebula]`, `[Asteroids]` and `[LootableZone]` sections that a zone is the key of. These sections get the new key with `ol.rename`, so they keep their place.
- The hole lines of the nebula and field files.
- The `locked_gate` and `npc_locked_gate` lines of `[locked_gates]` in `InitialWorld.ini`. Each of these lines holds the FLHash of a gate nickname as a number. Retail locks these gates at the start of a new game.

The rename of an object also changes each line of the mission scripts that names the object, in the same entry of the edit history. A line that the editor cannot change keeps the old nickname, and a warning tells it.

A section of a lower layer gets its new key with `ol.rename` (see "Renames"). A section of the lower layer with child sections keeps its key.

The editor refuses a nickname that another object or zone of any system has, without letter case, or that has the same FLHash. Retail keeps the zones of all systems in one map by the hash of the zone nickname. It also keeps the visit flags of objects and zones by the hash of the nickname. A light nickname must be unique in its file only. The editor also refuses a change of letter case only. It refuses a nickname that another section of the lower layer of the file has, also when the active mod deleted that section.

A rename with a warning opens a dialog first:

- The files that name the old nickname as text: the cutscenes, the mission files that are not scripts, and the other INI files. A cutscene or such a file that names the old nickname does not find the item.
- `KnowledgeMap.ini`: a text of the game then does not reveal the item on the navigation map.
- A mod that loads later and changes the section. Its patch cannot find the section after the rename.
- A zone nickname that gets or loses the word `Tradelane` or `vignette` (letter case counts). Retail makes a zone with `Tradelane` a trade lane zone when its shape fits, and a zone with `vignette` lawful.
- Lines that name the new nickname while no item has it. After the rename, they name this item.

A rename changes the hash of the item. A saved game keeps the visit flags and the gate locks of the old hash.

#### Components

The Inspector page of an object starts with its components, as Unity shows the components of a GameObject. Each component is a view over keys of the `[Object]` section:

| Component | Keys | Add | Remove |
|---|---|---|---|
| Solar | `nickname`, `pos`, `rotate`, `archetype` | each object has it | refused: each object needs an archetype |
| Loadout user | `loadout` | the loadout picker | removes `loadout`, so the object gets the loadout of its archetype |
| Dockable station | `dock_with` | the base picker, only for an archetype with a docking sphere | removes `dock_with` |
| Base object | `base` | the base picker | removes `base` |
| Faction member | `reputation` | the faction picker | removes `reputation` |
| Jump connection | `goto` | not in this version | refused in this version |
| Trade lane ring | `prev_ring`, `next_ring`, `tradelane_space_name` | not in this version | refused in this version |
| Planet ring | `ring` | not in this version | removes `ring` |

A click on a chip opens the section of the page with its keys. A chip with a glyph has a problem, and its tooltip tells it. "Add component" offers the components that the object does not have. A right click on a chip offers "Remove component". Each add and each remove is one entry of the edit history.

These component checks give warnings, until a retail comparison proves an effect:

- A Dockable station whose archetype has no docking sphere. The player cannot dock there.
- A Dockable station that docks with a base of another system. After a launch, the game puts the player in the system of the base.
- An object with the nickname or the FLHash of an object of any system. The game can confuse the two objects.
- A `[Base]` without an object in its system that docks with it, or without an object with `base` of it. The Inspector of the base shows these warnings.

#### Used by, Behaviors and marks

The pages of an object, a zone, a nebula and a field have a "Used by" section. It lists each line that names the item, from the data lists of the edit view with the unsaved edits. A button selects the item of the line, or its section.

The page of an object has a "Behaviors" section. It shows the pilot, the behavior, the difficulty and the reputation of the object. It also shows the encounters of the zones that hold its position, and the mission lines that name the object. A click on a mission line opens the mission at that line. The other files that name the object as text follow, for example the cutscenes. The section is read-only, except the rows of its keys.

The Hierarchy marks a row whose section the active mod changes with a blue bar at its left, as Unity marks a prefab override. A row whose section the active mod adds also gets a `+`.

### The Assets panel

The Assets panel is the first tab of the bottom group, as the Unity Project window. It shows the assets of the edit view: the base data, the lower mods, and the active mod with its unsaved edits. So a new item of the active mod shows before a save.

| Category | Rows | In the Scene |
|---|---|---|
| Prop groups | the groups of `props/` (see "Prop groups") | Placed as separate items |
| Solars | the solar archetypes, in one child for each Create template, and "Jump gates, holes and lanes" | Placed with their template |
| Ships | the `[Ship]` sections | Placed as a static prop |
| Asteroids | the `[Asteroid]` sections of `[Data] asteroids` | Placed as a static prop |
| Equipment | the equipment, in one child for each section type | Not placed: equipment goes on an object through its loadout |
| Effects | the `[Effect]` sections | Not placed: an effect goes on an object through equipment in its loadout |
| Loadouts | the `[Loadout]` sections | On an object only |
| Factions | the `[Group]` sections of `initialworld.ini` | On an object only |
| Models | the `.cmp`, `.3db` and `.sph` files below `DATA`, by top folder | Placed as a static prop when a solar archetype, a ship or an asteroid uses the model |

The rule data gives the categories. A solar archetype whose type is in no template shows in "Other solar" with a lock: it does not spawn. Jump gates, jump holes, airlock gates and trade lane rings show with a lock, because this version cannot make them.

The toolbar has the search field (`Ctrl+F`), the path of the category, and the size slider. The smallest size shows a list. The search finds the name, the nickname, the type and the model file, without letter case. "All assets" searches each category. A tile shows a "+" when the active mod adds the item, and a blue bar when it changes it. A ship, an asteroid or a model shows a "prop" badge when a solar archetype uses its model. The hover tooltip shows a larger image, the layer, the model file, and the number of lines that name the entry.

A click selects the asset when the mouse button comes up without a drag. So a drag never changes the selection, as in Unity. The Inspector shows the page of the asset. A ship gets the ship page, and equipment gets the equipment page. Another entry gets the page of its section, and a model or a prop group gets a page of its own. A double-click or `Enter` opens the asset: it selects the asset and shows the Inspector. The arrow keys move the selection. The menu of a tile has "Place at the camera pivot", "New static prop archetype", "Copy nickname" and "Copy model path". A group of the active mod also has Rename (`F2`), Delete (`Delete`) and "Show in folder".

Drag a tile to one of these places:

- **The Scene.** A placeable asset goes to the point on the grid plane under the cursor. The ray must meet the plane in front of the camera, within 4 times the distance to the orbit point. Else the asset goes on the ray at the distance of the orbit point. Each value is a whole number. A ghost shows the point while you drag. A loadout or a faction goes on the object under the cursor.
- **A Hierarchy row.** A placeable asset goes to the position of the object of the row, as its child (`parent`). Another row and the empty space below the rows place the asset at the orbit point. A loadout or a faction goes on the object of the row.
- **An Inspector list field.** A tile of an entry of the list of the field sets the value, as a pick does. For example, drag a loadout tile onto the `loadout` field of an object.

A target that cannot take the asset shows the reason under the cursor, and the drop does nothing. The Hierarchy of a cutscene takes no drop. Each spawn and each drop is one entry of the edit history. A loadout or a faction drop replaces the line, with the entry "Set loadout of <object>".

A solar archetype uses the template of its type. A station with a docking sphere then opens the base picker. A ship, an asteroid or a model opens the picker of the solar archetypes that use its model. Retail uses such archetypes for docking battleships, wrecks and decor ships. "New static prop archetype" adds a new `[Solar]` at the end of the first file of the solar archetypes. It also adds an object of it, in the same entry of the edit history:

```ini
[Solar]
nickname = br_gunboat_prop
type = NON_TARGETABLE
DA_archetype = <the DA_archetype line of the ship>
material_library = <each material_library line of the ship>
envmap_material = <the envmap_material line of the ship, when it has one>
LODranges = <the LODranges line of the ship, when it has one>
```

The lines are the model lines of the source. The nickname is `<stem>_prop`, then `_prop_2` and on. No solar archetype, ship or equipment has it, also not by FLHash. The archetype has no `hit_pts` and no `destructible` line, so the prop cannot be destroyed. The loaded game data does not have the new archetype, so the Scene shows the object after the save and the game data reload. The Console tells it.

The `archetype` and the `loadout` rows of an object page have a button that shows the value in the Assets panel, as the Unity ping.

#### Thumbnails

The engine renders an image of the model of each tile that shows, one new image in each frame at most. It renders only while the game data is loaded and no reload runs, and not while the camera moves or a drag runs. A tile shows the glyph of its kind until its image is ready. A model that no solar archetype, ship or asteroid uses, an effect, a loadout and a faction show their glyph. "Thumbnails: Rendered" in the View menu turns the images off. Then a hover still shows the image.

The editor keeps the images in `%LOCALAPPDATA%\OpenLancer\thumbnails`, one `.olthumb` file for each image. The key of an image holds the files of its model with their layers, lengths and times, so a changed model gets a new image. Over 200 MiB, the editor deletes the least recently used images until the folder uses less than 150 MiB. An image shows retail art, so it never goes into a repository or an export. `tools/content_scan.py` refuses the extension and the signature of these files.

### Data lists and pickers

A key that names another section, for example the `archetype`, the `loadout` or the `reputation` of an `[Object]`, gets a picker. The picker lists the sections that the engine reads for the key: the base data, the mods that load before the active mod, and the active mod with its unsaved edits. So a loadout that the active mod adds is in the list before a save. A mod extends a list when it adds sections. Only a set that the engine fixes, for example the type of a docking sphere, comes from the editor.

Click the field to open the picker. Type to filter the rows by name or nickname. A click on a row sets the value at once, and so do the up and down arrow keys. The Scene shows each value. `Enter` sets the selected row and closes the picker. A double-click and a click outside close the picker and keep the value. `Esc` closes it and puts back the value of the opening. One opening is one entry of the edit history. The first row is "None" for a key that the engine does not need. "None" removes the line. The footer tells the file of the hovered entry, the layer that gives it, and the number of INI lines that name it. The count does not include the templates of the cutscenes. A button at the right of the field selects the section of the entry.

A line with more than one listed value, for example `goto = Li02, Li02_to_Li01, gate_tunnel_bretonia`, gets one slot for each value: System, Exit object and Tunnel. The list of the exit object holds the objects of the system of the first value. A key with more than one line, for example `equip` of a `[Loadout]`, keeps its text field. The check marks each bad line with its number, and "Pick..." sets the first bad line.

A value outside its list shows as "Missing (Loadout): li_foo" in red, as Unity shows a missing reference. The row then offers Pick..., None and Revert. The rule data marks the lists that the engine uses to find the value. For these lists, a value of the active mod outside the list is an error. For the other lists, it is a warning: unlisted values are allowed. A value of a lower layer outside a list is a warning, because the active mod did not set it.

The Inspector also edits three sections of each room file that a `[Room]` of a base file names: `[Room_Info]`, `[CharacterPlacement]` and `[PlayerShipPlacement]`. A room file has each of these sections one time at most, so a patch section finds its target by its type. The other sections of a room file stay read-only.

The script keys of these sections use the list of the cutscenes. The list holds each `.thn` file of the edit view and each new cutscene of the active mod, also before its save. A value is a path below `DATA`, for example `Scripts\Bases\Li_01_bar_enter_01.thn`. The list compares the values as paths: without letter case, and with `\` or `/`. In the picker, a new cutscene that is not saved has the tag "new".

| Key | Field | A value of the active mod outside the list |
|---|---|---|
| `[Room_Info] set_script` | Picker without None | Error: the game crashes when the player enters this room. A room of the active mod without the key is an error too. |
| `[Room_Info] scene` | Text field | Error: the game crashes when the player enters this room. |
| `[CharacterPlacement] start_script` | Picker | Error: the game skips this script when the player enters the room. |
| `[PlayerShipPlacement] landing_script` | Picker | Error: the game skips the landing scene. |
| `[PlayerShipPlacement] launching_script` | Picker | Error: the game skips the launch scene. |
| `[Room_Info] goodscart_script` | Picker | Warning: the game does not play cart scripts. |

A `scene` line names its script by its place: value 2 when value 0 is `all`, else value 1. The check reads that value, as the engine does, also when the value does not end with `.thn`. A `scene` line with fewer values gets no check, because the engine skips it.

The `file` key of a `[Nebula]` and of an `[Asteroids]` section uses the list of the nebula files or of the asteroid files. The list holds the INI files below `SOLAR\NEBULA` or `SOLAR\ASTEROIDS` whose sections all belong to a nebula file or to an asteroid file. So the texture files of these folders, for example `edge_shapes.ini`, are not in the list. The engine reads a file of any folder. So a value outside the list is valid when its file exists, and the picker shows it as "value (other folder)". A file that does not exist is an error: the game crashes when it loads the data.

The `zone` key uses the list of the zones of the system file. A zone that the system does not have is an error: the game drops the nebula or the field. A section without a `file` line is an error of a section that the active mod changes: the game drops the item.

The Inspector page of a nebula or a field shows the zone and the file with its picker. It also shows the area of the zone, the holes and the sections of the file. The sections of a nebula or asteroid file are read-only in this version. The Scene shows another file when you open the system again after the save.

The editor reads some files only for the lists, for example `solararch.ini` and `loadouts.ini`. Their sections are read-only in the Inspector, because this version has no pages for them.

The Assets panel uses two more lists. The `asteroid` list holds the `[Asteroid]` sections of the files of `[Data] asteroids`. The `model` list holds the `.cmp`, `.3db` and `.sph` files below `DATA`. A list field also takes a tile of the Assets panel (see "The Assets panel").

The save gate checks the templates and the clips of the cutscenes with more lists (see "The save gate"). The template rows and the clip picker of the cutscene Inspector use the same lists (see [CUTSCENES.md](CUTSCENES.md)):

| List | Entries |
|---|---|
| `asteroid_mine` | The `[AsteroidMine]` sections of the files of `[Data] asteroids`. The engine puts the asteroids and the mines in one collection. So a template of the category `asteroid` names an entry of `asteroid` or of `asteroid_mine`. |
| `petal_room`, `petal_prop`, `petal_cart` | The first values of the `room`, `prop` and `cart` lines of the `[ObjectTable]` sections of each `[Data] PetalDB` file. The engine reads each such file. When two files give one name, the engine uses the first file. |
| `animation` | The clips of a character: the scripts of the `.anm` files that the `[Animations]` lines of bodyparts.ini name. The engine reads value 0 of each line, joins it to the data path, and skips a file that does not exist. When two files give one name, the engine uses the last file. The detail of an entry is its file name. |

The templates of a cutscene also use the lists `solar_archetype`, `ship`, `equipment`, `costume`, `effect` and `vis_effect`, and `sound`.

The list `animation` reads only the node and the string blocks of each `.anm` file, not the keys. It keeps the names of each file with the length and the last write time of the file on disk. So a save reads no `.anm` file again. The list follows the bodyparts.ini and the `.anm` files of the mods. The editor cannot add or change an `[Animations]` line yet. An `.anm` file that does not read gives one warning, and the list keeps the clips of the other files. The game does not: when it cannot read such a file, it closes during its load. A save gives the warning of each such file of the active mod, and the save continues.

### The raw INI block

Each Inspector page that edits sections ends with "Loaded values (read-only)" and then "Raw INI". The raw block shows `[Type]` and one `name = values` line for each line of the section. Edit the lines, then click outside the block. The edit changes only the names whose lines you changed. One raw edit is one entry of the edit history, and one Undo puts back all its lines. The order of different names stays the file order. A new name goes at the end of the section.

The block refuses an edit and keeps nothing when the text has a problem: a second header, a comment, a line without `=`, a changed nickname line, a line that the type of its key cannot hold, or a key that no row can change. The Console line then holds the problem and your whole text, so you can copy it back. When the section changed while you typed, for example by an undo, the block also refuses the edit. A value outside its list does not stop the edit: the row above then shows the error. The block is read-only when the section is read-only.

#### Raw edits of mission files

The "Raw INI" view of a mission script and the INI block of a mission page take edits too. The game reads the lines of a mission section in order, so the edit compares your text with the text at its start section by section, not name by name:

- A changed section gets all lines of your text, in your order.
- A new section goes in at its place in your text. A removed section goes, and each line that names it shows "Missing" until you fix the line or undo.
- The merge adds a new section of a patch at the end of the file. The game finds a trigger and the other mission items by their names, so their place has no effect. So in a patch, a new section of such a type goes to the end of the file, and the Console tells it. A new ship or formation section keeps its place with `ol.before`. A whole file of the mod keeps each place.
- A ship or a formation section in another place moves, because a formation reads the ships before it. A section of another type keeps its place, and the Console tells it.
- All changes of one raw edit are one entry of the edit history.

The edit stops and keeps nothing when a first nickname line changes, because the lines that name the item would not follow. Use Rename (F2). It also stops when the section changed while you typed, and for a problem of the text: a header of an unknown section type, a line before the first header, or a line without `=` whose key the game does not know. The INI block of a page holds only the sections of the page, so the edit of a block also stops when a header goes in or out of the block, or when the type of a header changes. Add or remove a section in the "Raw INI" view of the whole script. The Console line then holds the problem and your whole text. A comment inside a changed mission section does not stay in the patch, and the Console tells it once. While you type, the marks of the text show its problems at once, and the problems of the mission checks of your text a moment later.

### The save gate

Each save runs the save gate first, also a save with nothing to write, and also the save before Play. A save writes nothing while the gate finds an error:

- A value of the active mod outside a list that the engine uses to find it, for example an `archetype` or a `loadout` that no section has.
- A required key without a valid line in a section that the active mod adds or changes, for example an `[Object]` without an `archetype`. The game drops such an object.
- A value of any layer that names an entry that the active mod removes, for example a retail object whose loadout the active mod deletes with `ol.delete`.
- A value of any layer that names an entry that the active mod makes unusable, also without a delete. For example, the active mod removes the `point` lines of a curve, and a retail light names the curve. The game stops loading with such a light.
- A light of the active mod that names a curve that the engine cannot load, or a name of the active mod with the FLHash of another name of its list. The engine load stops.
- An error of the field checks in a section that the active mod adds or changes. Each such error tells what the game does with the line:

  | Error | What the game does |
  |---|---|
  | A line with too few values | The game ignores the line. |
  | A value that is not a number, in a key of numbers | The game reads 0 in its place. In a text file, the game reads the digits at the start of the value, for example 100 for `100x`. |
  | A name that is not in the list of names of the key, for example `type = BOAT`, or a value that is not a GUID | The game ignores the line. |
  | A required key without a line, for example `da_archetype` of a `[Ship]` | The game drops this section. |
- A zone of the active mod with the nickname or the FLHash of another zone of any system. The game drops one of the two zones.
- An error of the format checks of the export or of the content checks of the retail game, when the active mod adds it. See "Format checks" and "Content checks of the retail game". These checks run only for a tier-1 mod that names a retail target. They compare the data with the active mod with the data of the mods that load before it. They do not merge on the files of each target game. Play in the game and the export do that. For example, a good with the price 0 blocks the save, because the retail game can crash when a player buys or sells it.
- An error of the mission checks in a mission script that the active mod changes. Examples are a trigger without a condition (rule M01) and two triggers whose names have one game id (rule M27). A line of any script that names a name that the active mod removes or renames is an error too. A mission problem that the base game or a lower mod also has is a warning, so it does not block the save.
- A mission patch that does not merge on the game of a retail target of `mod.json` (rule T01). The check runs for each target with a linked game. It merges the lower mods of the profile on that game first, as Play in the game does. For example, the patch changes a trigger that the game of the target does not have. A section of the target that differs from the `ol.base` of the patch gives a warning (rule T02), because the changes of the target in the section go away. A target without a linked game gives one info line in the Console.
- An error of the checks of a cutscene that the active mod changes, when the lower cutscene does not have the error. For example, a LIGHT has no `spatialprops.pos`. A whole cutscene file of the active mod that the engine cannot read is an error too. The game crashes when it plays such a cutscene, so the message starts with "The game crashes when it plays this cutscene."
- A template of a cutscene that the active mod changes, when the template names no entry of its data list (see "Data lists and pickers"). The check reads the lists with the unsaved edits, so an unsaved INI edit counts, for example a new costume. The check of the lower cutscene uses the lower game data. So a template that the lower cutscene has with the same problem in the lower data does not block the save. This is also true after a rename of its entity. A template whose item the active mod deletes, renames or makes unusable blocks the save, also when the template does not change. An INI edit that deletes an item that only an unchanged cutscene names does not block the save. These templates crash the game, so their messages start with "The game crashes when it plays this cutscene.":
  - the costume of a DEFORMABLE without `userprops.Actor`. A costume of the active mod whose `body` names no body part is not usable either, because the game drops it. The message tells why.
  - the solar archetype, the ship, the room or the cart of a COMPOUND. A solar archetype, a ship, an equipment or an asteroid without a `DA_archetype` line also crashes the game. It also crashes the game when the file of the line does not exist. The message tells which of the two is true.
  - an equipment of a COMPOUND whose section type gets no model, for example `[Light]`, `[Commodity]`, `[Scanner]` or `[Engine]`, also with a `DA_archetype` line. A `[Munition]` gets a model only with a `motor` line.

  These templates do not crash the game. The message tells what the scene does without the item:
  - the prop, the equipment or the asteroid of a COMPOUND: the scene shows the entity without a model. The game makes no equipment from `[Armor]` and `[Shield]` sections, so such a template is also in this group.
  - the effect of a PSYS: the scene does not show the entity.

  The engine finds the item of a template by the text as it is, without letter case. So a known name with a space before or after it names no item. The check skips a template that the room fills in, for example `$terrain_dyna_01`. A PSYS template that starts with `$` names an effect as it is, so the check reads it. The check also skips the templates `PlayerShip` and `PlayerShipEngines`, the costume of a DEFORMABLE with `userprops.Actor`, and the template of a SOUND. A SOUND can also name a voice line.

  The game makes no COMPOUND of a script that only `[Trigger] Act_CallThorn` lines name, so the check skips the COMPOUNDs of such a script. The check reads these lines with the unsaved edits. So you can add the trigger line and the cutscene in one save.
- A clip of a START_MOTION event on a character, when the list `animation` does not have the clip. As for a template, the check reads the cutscenes that the active mod changes. The engine finds a clip by its name as it is, without letter case. No clip has an empty name, so an empty clip blocks the save too. The message says what the scene does: "The character does not move." The check applies only when the first target of the event names an entity, and the last entity with that name is a DEFORMABLE.

  The clip picker of the Inspector uses the same rule. A clip that the lower cutscene has with the same problem in the lower data does not block the save. A clip that the active mod removes blocks the save, also when the clip does not change.

The save also gives a warning for each `.anm` file of the active mod that the list `animation` cannot read. The warning does not block the save. If the game cannot read the file either, the game closes during its load.

A field-check error or a missing required key that the base game has too does not block the save. This is true when the key has the value of the base data, no lower mod changes the section, and the same check of the base version of the section gives the same error. For example, the active mod changes the loadout of a retail object, and the position of the object in the base data has a value that is not a number. The save continues with a warning that ends with "The base game has the same problem, so the save continues." Each other error blocks the save, also when it is a value of the base data: an error of a key that the active mod writes again, an error that a key of the active mod causes, an error of a key that a lower mod changes, and each error that reads another section.

The Console hides such a warning by default, as it hides each base-game message. The Inspector shows such a field problem muted, with a lock glyph. A missing required key keeps its normal look.

An entry is unusable when the engine cannot use it. A curve without points is unusable, and so is a name that two curves have. An entry is also unusable when its own checks give an error that makes the game drop it or stop loading. Some examples:

- A `[Munition]` with a `motor` and no `explosion_arch`. The game stops loading.
- A `[Gun]` without a valid `projectile_archetype`. The game drops the gun.
- An `[Object]` without a valid `archetype`. The game drops the object.

An error that makes the game crash does not make its entry unusable, because the game loads the entry. The game crashes only when it uses the bad value. For example, a jump gate with a `dock_with` that names no base is an error on its `dock_with` line, and the error blocks Save. The `goto` line of the partner gate names a gate that the game loads, so that line is no error.

A line that names an unusable entry can make its own section unusable. The gate follows such lines. For example, a missile without its explosion makes each gun that fires it an error. The game drops such a gun, so each loadout line that names the gun is an error too. When a lower mod or the base data made the entry unusable, the line gives a warning only.

Each error tells what the game does with the data, for example "The game stops loading with this value.", "The game crashes when the player docks here." or "The game drops this object." An error about an unusable entry tells why the entry is not usable, and how to correct the line:

`[LightSource] nickname = System_LIGHT: 'atten_curve = DYNAMIC_DIRECTION': Curve 'DYNAMIC_DIRECTION' is not usable: the [IGraph] 'DYNAMIC_DIRECTION' has no 'point' line. The game stops loading with this value. Fix 'DYNAMIC_DIRECTION', or pick another value.`

The Console lists each error with its section. A click on the line selects the section, and the Inspector shows the error below the row of its key. The Select button of the row selects the unusable entry. An edit with an error stays in the editor and in the recovery file until you correct it or undo it. A delete never stops at a check: the references to the deleted entry show as "Missing", as in Unity, and one Undo heals them. One Undo of the edit that made an entry unusable also heals each line that names the entry.

### Cutscenes in the editor

The Cutscenes tab lists the cutscene scripts, and the Cutscene tab previews one. The Timeline tab and the Inspector show and edit its entities and events. A save writes a THN patch for each changed cutscene (see "Cutscenes" and "What stays in a patch file"). New cutscene in the Cutscenes tab makes a new script in the active mod, and a save writes it as a whole file. See [CUTSCENES.md](CUTSCENES.md).

### After a save

File > Save and `Ctrl+S` save the edits of the active mod. As in Unity, `Ctrl+S` also works while a text field has the keyboard. An open field edit or drag ends first and keeps its value, and then the save starts. That end asks no question, so a new text goes only to the section of the field. Only a dialog stops `Ctrl+S`. In a text field, `Ctrl+Z` and `Ctrl+Y` stay the undo and redo of the text.

Each save shows its result in the status bar and as the last Console line of the save:

| Result | Message |
|---|---|
| The save changed files | `Saved mod 'my-mod' (save 2): 3 files written, 0 files deleted.` |
| No unsaved edits | `Mod 'my-mod' has no unsaved edits. No file changed.` |
| Errors stop the save | `The editor did not save mod 'my-mod' because of 2 errors. The Console lists them.` |
| Files changed outside the editor | `The editor did not save mod 'my-mod'. Something outside the editor changed 1 file of the mod. Select Overwrite or Cancel in the dialog.` |
| The active mod is None | `The editor did not save. Mod > None is read-only. Select a mod in the Mod menu, then save again.` |
| The session is read-only, for example because the mod is in the game folder | `The editor did not save mod 'my-mod' because it is read-only.` and the reason |

Above the result, the Console lists the files that the save wrote and deleted, or the problems that stopped it. After a save that changed files, the game data loads again in the background, and `oltool validate --active <mod> --json` checks the save in its own process. One validation runs at a time. The oltool process reads the files of the mod, so a save of new edits stops the validation of the save before it, before the save moves files. When that save then changes no file, the stopped validation starts again. The Console labels each result with the number of its save. A click on a problem of a cutscene opens the cutscene and selects the element of the problem (see "The validation of cutscenes").

One load of the game data runs at a time, because each load fills the same resources. A save can come while the game data loads, for example a save of the restored edits of an earlier session before the first load ends. The save writes its files at once. The game data then loads again with the saved files after the running load, and the Console tells it. More saves during one load give only one more load.

When a file does not move into place, for example because a program uses it, the save is not complete. The game data then does not load again, and no validation runs. The next save completes the files first. Then the game data loads again, and the validation runs.

While the game of Play runs, a save whose validation finds no errors also reloads the game. See [PLAY.md](PLAY.md).

### What stays in a patch file

- The lines above the comment block of the first section, the comment block directly above each section, and each line that no edit touched stay with their text and their bytes, comments included.
- A line of the patch stays also when its value equals the value of a lower mod. So the mod keeps its value when the lower mod changes. Only an edit of that name can remove the line.
- A line whose value an edit changes gets new text, without its inline comment.
- A patch file whose content the edits do not change keeps its bytes.
- When a patch has no sections after the edits, the editor deletes the file and its empty folders below `patches/`.

The editor writes an integer as a decimal number. In a patch for a binary file, it writes a hash in its signed form, for example `-1980013182`. A float always has a `.` or an exponent, for example `100.0`, so that the merge reads it as a float. A boolean is `true` or `false`.

The editor writes a THN patch in canonical form (see "Cutscenes"). It keeps the comment block at the top of the file. It drops the other comments, and the Console tells it. A path that the patch sets or removes stays in the patch also when its value equals the value of a lower mod, until an edit or a revert of that path. When the patch sets a whole record, for example `cameraprops`, an edit of a value in it keeps the whole record in the patch, with the new value. A revert of a value in it takes only that value out of the patch: the other values of the record stay in the patch, each with its own path. A THN patch whose content the edits do not change keeps its bytes. When the active mod has a whole file for a cutscene, the editor writes the whole file as canonical Lua text.

### Sections in which the line order counts

The engine reads the lines of some sections in order, for example a mission `[Trigger]` or `[ObjList]`. In a mission file, the editor writes a changed section of such a type with `ol.replace` and all its lines in order. It adds `ol.base` with the hash of the section in the lower layers (see "Mission files"). So the order stays, also when a line comes before the `nickname` line. A new ship or formation that must come before another section gets `ol.before`. A moved ship or formation is a delete with `ol.base`, and a new section with `ol.before`.

When a patch of the mod changes such a section by names, the next save of the file writes the section again with `ol.replace`. The game gets the same lines. The save gives a warning: "The editor wrote {section} of {file} with ol.replace. The game gets the same lines." After the save, the patch has the new form, so the warning does not come again.

Outside the mission files, the editor writes all lines of such a section except the `nickname` line. The merge puts these lines at the place of the first line that they replace, and the `nickname` line stays in its place. So when a line comes before the `nickname` line, a patch from the editor cannot keep the order. The editor shows such a section, but it does not change it. When a hand-written patch of the mod changes such a section, the editor writes that section again at each save of the file. So the editor cannot save an edit of that file, and the save reports the section. Change that file by hand.

### How a save writes the files

A save writes all changed files of the mod, or none of them:

1. The editor makes each patch and checks that the merge gives the edited data from it. Any problem stops the save before it writes a file.
2. The editor writes the new files to the `.olsave/` folder of the mod. Then it writes the journal, `.olsave/journal`: each move and delete, the hash of each file before the save, and the hash of its new content.
3. The editor moves each new file into place, deletes the files to delete, and then deletes the journal. Each move replaces its file in one step. oltool and the game of Play read mod files so that a move can replace a file while they read it. When another program writes a mod file in place at that moment, their read of the file fails, so they never read a part of it.

When the editor stops after step 2, it completes the moves when it opens the profile again. Do not delete `.olsave/` while the journal is in it. Open the profile in the editor once, so that the editor completes the save. Until then, `oltool build`, `validate` and `deploy` show a warning for the mod.

When the editor completes a save, it changes a file only when the file still has its content from before the save. A file that changed after the editor stopped, for example a file that a backup put back, stays as it is, and the editor reports it. When the editor cannot complete a save, for example because a file is in use, the profile opens and the editor tells why. If the save is of the active mod or of a mod that loads before it, the editor then shows the data but does not take edits.

A save also stops in these cases:

- A file of the active mod changed outside the editor after the editor read it. The save replaces such a file only when you confirm it. Cancel is the default button of that dialog.
- The base data, a mod that loads before the active mod, or the mod list of the profile changed after the profile opened. This includes a change of the load order that `dependsOn` and `loadAfter` give, and a change of the text ids of those mods. The edits then apply to old data. Open the profile again.
- A patch or a whole INI file of the mod uses a text placeholder that the mod's `strings.json` does not have after the save. When the save changes `strings.json`, it checks each patch and whole INI file of the mod, also the files that it does not write.
- A patch line of a key that holds a text id holds the id of a text of a mod as a number. The id comes from the position of the text in the generated DLL, so it changes when a mod adds text. Copy this text into the mod and use its placeholder.
- A file that the save writes is a folder now, a folder of its path is a file, or the file is read-only.
- The active mod is in the OpenLancer repo, and the save would write a whole cutscene file into it. The content scan cannot tell such a file from a copy of a retail script. A THN patch is allowed.
- The save gate finds an error (see "The save gate"). For example, a light names an attenuation curve (`atten_curve`) that the engine cannot load: no FLOAT graph of `igraph.ini` has the name, more than one graph has it, or the graph has no points. The light can be a light of the active mod, or a light of any layer when the active mod removed the points of the curve. The game stops loading with such a light, so each later load of the profile would fail. The Console names the light, the curve and the file. The gate reads the curves from the edit view, so it needs no loaded game data.

While the editor has unsaved edits, it keeps them in `.olsave/recovery.json`, a moment after each edit. When no unsaved edits remain, for example after an undo, the editor deletes the file. A close of the profile and a change of the active mod ask first: Save, Discard or Cancel. After a save, the action continues, also when the save needed your confirmation to overwrite a file.

A close of the editor window asks too: the close button, `Alt+F4`, the taskbar and File > Exit. An active field or drag ends first. With unsaved edits, the window stays open, and the dialog asks `Save changes to <mod name> before you close?`. The name is the `name` of `mod.json`, or its `id` when it has no `name`. Save is the save of `Ctrl+S`, with its checks. When the save succeeds, the window closes. When the save fails or a check stops it, the window stays open, and the Console tells why. Don't Save closes the window and deletes the recovery file, so the next open does not offer the edits. Cancel and `Esc` keep the window open. Without unsaved edits, the window closes at once. The field that ends at the close asks no question, so a new text goes only to the section of the field, also when other sections of the item show the same text.

While the game data loads, or loads again after a save, the window stays open until the load ends. Unity also ends a running import before it quits. Until the load ends, the dialog "The game data loads now. The editor closes when the load ends." blocks the window, as the progress bar of a Unity import does. You cannot edit, save or start Play then, and `Esc` does not close the dialog. Its button **Close now** closes the window at once, for example when a load does not end. The load then stops, and the saved files do not change.

A crash or a kill of the editor cannot ask. Then the recovery file keeps the unsaved edits, and the next open of the mod offers to restore them. The editor restores them only when the files of the mod did not change after it wrote the recovery file. Until you restore or discard them, the editor does not take new edits for the mod. So a second recovery file is never necessary.

One editor at a time edits a mod. While an editor has a mod open as the active mod, it holds a lock on `.olsave/lock`, and `.olsave/lock.json` names its process and the time it opened the mod. A second editor on the same mod shows the data, but it takes no edits and does not save. Its Console names the first editor. The second editor does not change the save files or the recovery file of the first editor. A crash can leave the two lock files, but they do not block the next editor.

### strings.json and profile files

The editor writes `strings.json` with sorted keys and an indent of two spaces. It keeps the other fields of the file, its byte order mark and its line ends. A new file has no byte order mark. The editor does not write a `strings.json` that has comments, because the reader skips comments and a new file cannot keep them.

The editor adds a new mod to the end of the `mods` list of a profile with the same rules. It keeps the fields that it does not know, and it does not write a profile file that has comments. It adds the mod only when the profile loads with it: the mods in its `dependsOn` list must be in the profile, and the load order must have no cycle.

### New items, renames and text

New from template copies a ship or equipment with the sections that belong to it:

- A ship: the `[Ship]` with its child sections, the hull good that the engine uses (the last one in load order), and the packages of that hull.
- Equipment: its section with its child sections, its good, and the munition that `projectile_archetype` names, with the good of the munition.

Each copy goes into the file of the section that it copies, because a patch cannot change the file list of `freelancer.ini`. The ship or the equipment and its good get the new nickname. The hull good gets `<new>_hull`, a package gets `<new>_package`, and a munition and its good get `<new>_ammo`. A line of a copy that names another copy names the new copy: `projectile_archetype`, `equipment`, `ship` and `hull`. In a binary file, a copy writes `true` and `false` in lowercase, because the merge stores a new boolean that way.

A copy keeps a text id of the base data. A text of a mod gets a copy in `strings.json` of the active mod, with a key from the nickname of the copy, and the line of the copy gets its placeholder:

- A placeholder of the active mod: so a text edit of the copy does not change the template.
- The id of a text of a lower mod: the merge gives that id from the position of the text in the generated DLL, so the id changes when a mod adds text.

Copies of sections that share a text share the copy of the text.

The Database has no menu item for Rename yet. Only the data API (`ItemEdits.Rename` in OpenLancer.Data) renames an item of the item files. It renames only an item that the mod adds. Equipment and its good keep the same nickname, because the engine finds the equipment of a good by that nickname. The editor changes each line that names the item in a section that the mod can change: the `ship`, `hull`, `equipment` and `addon` lines of goods, `projectile_archetype`, and `marketgood`. The new nickname must be a value that a patch line can hold, for example without `,` or `;`. The Scene renames the objects, zones and lights of the system files, also of the lower layers (see "Rename").

When you change the text of a key that holds a text id (see "Text: strings.json"), the editor writes the text into `strings.json` and sets the line to its placeholder. The text of no other section changes:

- A line with a placeholder of the right kind keeps its key, when no other section uses that key.
- Otherwise the editor makes a new key: the nickname in lowercase for `ids_name` and `strid_name`, and the nickname with a suffix for the other keys, for example `li_gun01.info` for `ids_info`. When another section uses that key, or `strings.json` has an unused text with it, the key gets the section type, for example `li_gun01.good`, and then a number, for example `li_gun01.good_2`.

A retail gun and its good have the same `ids_name`, but the Inspector shows the gun and a shop shows the good. So the editor offers to set the same placeholder on each section of the item that has the same id. The sections share one text only when you accept the offer. An infocard from plain text gets one paragraph for each line.

A new item, a rename and a text edit read the data when you start them. When the data changes before the edit runs, the edit does not run, because it would undo the change.

A boolean line without a value, for example `separable =` of a `[CollisionGroup]`, is true. The engine reads the value of such a line when it has one, so the editor writes `separable = false` to make the flag false.

## Tools

| Command | Effect |
|---|---|
| `oltool build <profile.json>` | Merge the mods. Show the changed files and the merge problems. |
| `oltool validate <profile.json> [--active <mod id>] [--json]` | Merge, then report the data problems that the mods add to vanilla. With `--active`, report only the problems that this mod adds to the mods that load before it, without the later mods. With `--json`, write only a JSON list of `{"severity", "source", "message"}` to stdout. A problem of a cutscene also has `"cutscene": {"path", "kind", "index", "value", "key"}`: the install path, `entity` or `event` and the place of the element in its list from 0, the path of the value, and the key of the element as `ol_match` writes it. Without a kind, the problem is about the whole script. |
| `oltool deploy <profile.json>` | Validate, then write the merged data to `<profile name>/` next to the profile, and write `<profile name>.lancer.ini`. |
| `oltool ini <profile.json\|folder> <install path>` | Print a merged INI file as text. Binary INI files are decoded. |
| `oltool dump <profile.json\|folder> <out.json>` | Write a sorted JSON dump of the data that the engine resolves. |
| `oltool thn <profile.json\|folder> <install path>` | Print a cutscene script (`.thn`) as canonical Lua text. The script comes from the merged view, so the text shows the changes of the mods. |
| `oltool thn roundtrip <profile.json\|folder> [<install path>]` | Check the canonical text of each `.thn` file of the source, or of one file. The text must give the same tables, with exact numbers and the same key order, and the text of the result must be the same text. Each problem names the first value that differs. |
| `oltool validate <profile.json> --target <hde-0.7.1\|vanilla-1.1> [--game <folder>] [--json]` | Merge all mods of the profile on the files of the target game. Report the problems of the format checks and of the content checks of the retail game (see "Content checks of the retail game"). `--game` names the game folder of the target. Without it, the base of the profile must give the target. For HD Edition 0.7.1, the command also lists the changed lines of HD Edition that the mods replace. This needs a profile on Freelancer 1.0 or 1.1. A mod that changes data under `DATA` gets the note "Single player, or a server with the same mod." The command only reads the game folder. |
| `oltool export <profile.json> --mod <id> --target <target> [--target <target>]... [--game <folder>]... [--format dropin\|flmm\|all] [--out <folder>] [--checklist] [--flmm-probe] [--same-version]` | Make the packages of one mod for its targets (see "Export"). Repeat `--target` for more targets. `--game` names the game folder of a retail target. Repeat it for each retail target: the export uses each folder for the target of its game. Without it, the base of the profile must give the target. `--format` picks the package forms of a retail target, all by default. `--out` names the exact output folder, `exports\<mod id>\<version>` next to the profiles folder by default. `--checklist` prints the checklist and writes no file. `--flmm-probe` writes only the probe of the FLMM check, which is not for players. The probe does not change `nexus-page.txt`, `CHECKLIST.txt` and the screenshots of the folder. The export records its packages and its checks in the publish history. An export of a version that the history has with other game files exits 1. `--same-version` replaces that version when it does not have the mark "published". The replace needs each target and form of the earlier exports of the version, so give all their targets in one command. A history file that does not load stops the export with exit 2. |
| `oltool publish <profile.json> --mod <id> [--json]` | Print the publish checklist of a mod: each step with `done` or what is missing, and the recommended items. It reads the publish history, the last checks and the base of the profile, and runs no check. With `--json`, write only one JSON object. Exit 0 when all steps are complete, 1 when a step is missing, 2 when the command cannot run. |
| `oltool verify-base <folder>` | Check that a folder is the unchanged, read-only retail data. |

Exit codes: 0 = OK, 1 = the data has problems, 2 = the command could not run.

Start the game on a deployed profile: `lancer.exe <profiles folder>\<profile name>.lancer.ini`.

Play in the editor needs no deploy. The game of Play reads the mods through the overlay, and it writes its settings and saves to `<profile name>.play/` next to the profile. See [PLAY.md](PLAY.md).

The deploy folder is managed by `oltool`. A deploy deletes files in it that are not in the merged view. Do not keep your own files there. To protect other data, the deploy stops before it writes anything when:

- the folder exists, is not empty, and has no `.openlancer-deploy` marker from an earlier deploy;
- the folder overlaps the base data, a mod folder, or the profile file;
- the folder or one of its parents is a link, or the folder contains a link;
- the path contains `,` or `;`, which lancer.exe cannot read;
- a file in the folder is read-only (it can be a link to the base data).

When a deploy stops part of the way, the marker records it. Run the deploy again to complete the folder.

When the deploy folder path has characters outside ASCII, lancer.exe reads the path correctly only until the game saves its settings, because the game then writes the config file in a form that loses those characters. After that, run `oltool deploy` again, or move the workspace to a path with only ASCII characters.

## Export

The export makes the packages that players install. It merges one mod on the files of a target game, checks the result and writes the packages. The internal patch format stays the working format of the editor. Open the export in the editor with **Mod > Export mod...** or with **Export...** of the Mods window, or run `oltool export`.

The Export dialog reads the mod from its folder when it opens, so it uses the applied display name, version and targets. It shows the display name with **Change...**, which opens the Mods window at that field, and the publish checklist. It writes into the version folder of the output folder, and it tells that folder. After the export, it records the packages in the publish history, and it tells the next step: the upload to Nexus and the mark "published". When the Mods window has unapplied publish settings of the mod, the export asks to apply them first.

When you close the Export dialog while the export runs, the export goes on without the dialog. An export that still checks the mod then stops and writes no package. An export that writes its packages completes, records them in the publish history and writes the result to the Console. Export and play then does not start the game.

### Targets and bases

| Target | Base | Packages |
|---|---|---|
| `hde-0.7.1` (HD Edition 0.7.1) | A linked game that the game link identifies as HD Edition 0.7.1 | Drop-in zip, FLMM package |
| `vanilla-1.1` (Freelancer 1.1) | A linked game that the game link identifies as Freelancer 1.0 or 1.1 | Drop-in zip, FLMM package |
| `openlancer` | None | A zip of the mod folder |

- The export of a retail target merges the mod on the files of its base. HD Edition changes most INI files, so each target gets its own packages.
- A target that the mod does not name in `targets` is not available. A tier-2 mod cannot export to a retail target.
- The export of a mod with `dependsOn` is not available yet.
- When the INI files of the base differ from the file list of its version, the export warns. A base with JFLP also gives a warning, because the drop-in zip carries the JFLP version of each changed file.

### What a package holds

A package holds only the files of the mod and the merged files that the mod changes. It never holds a game file.

- A changed INI file is text. A text file of the game keeps its comments and its other lines. Only the changed lines change. A binary file of the game becomes canonical text.
- A file whose sections do not change is not in the package, also when its bytes change.
- A changed cutscene is Lua text. The export writes the number of each THN name that only Librelancer defines, for example `SUBTITLE`. A whole cutscene file of the mod goes into the package as it is.
- The text of the mod goes into `EXE\ol_<mod id>.dll`. Its index follows the rule of the retail game: 1 plus the number of `DLL` lines in all `[Resources]` sections. A line counts also when its DLL does not exist. On HD Edition 0.7.1 the index is 8, and on the retail game it is 7. The new `DLL` line goes after the last `DLL` line of the last `[Resources]` section.
- The package leaves out hidden and system files, names that start with `.`, `Thumbs.db`, `desktop.ini`, `*.oltmp`, `*.tmp` and names that end with `~`. The export gives a warning for each.

The export refuses these files:

- A whole `EXE\freelancer.ini`, `EXE\dacom.ini`, `EXE\dacomsrv.ini` or `EXE\flplusplus.ini` of the mod, for a retail target. Use a patch with `ol.append`.
- A whole INI file or cutscene of the mod with the same content as the file of the game in another form.
- A file with the bytes of a file of a build manifest (`data-manifest`).

A whole INI file or cutscene of the mod that replaces a file of the game with changes gives a warning. It undoes the changes of HD Edition and of other mods in that file.

### Format checks

The format checks look at each line that the export writes. These are the lines that the mod changes or adds, and each line of a binary file that becomes text. An error stops the export.

| Check | Level | What the game does |
|---|---|---|
| A line longer than 1,023 bytes | Error | It reads the rest of the line as the next line. |
| A section name, a key or a value longer than 1,023 bytes | Error | It writes past the end of its buffer. |
| A text value with `;` | Error | It ignores the text after `;`. |
| A text value of a binary file with `,` | Error | It reads two values in place of one. |
| `true` or `false` in place of a number | Error | It reads `true` as 0 in a number key. |
| A number that is not finite | Error | It reads 0. |
| A text value with digits and a final `d`, `b` or `h` in a number key | Warning | It reads the number in another base. |
| A character outside code page 1252 | Warning | It shows another character. |
| An infocard that is not valid XML | Error | It shows no text. |
| An infocard with a single `%` | Warning | It can show wrong text. Write `%%` for one `%`. |
| A THN name that only Librelancer defines | Warning | The export writes its number. Check the cutscene in the game. |

The content checks of the retail game also run on each export (see "Content checks of the retail game"). An error of the format checks or of the content checks stops the export.

### Drop-in zip

`<mod id>-<version>-<target>.zip` has the folders `DATA` and `EXE` at its top, with the letter case of the folders of the game, and the README. The player copies the files of the list to a backup folder, then extracts the zip into the game folder. A drop-in zip replaces whole files, so it does not combine with other mods that change the same files.

When the zip holds `EXE\freelancer.ini`, the README gives each line that the mod adds, with its place. A player can add these lines to their own file, and keep the options of their installer. Two OpenLancer mods with text do not work together as drop-in zips, because both use the same text index.

### FLMM package (preview)

`<mod id>-<version>-<target>-flmm.zip` holds `<mod id>.flmod` and the README. The `.flmod` file is a zip with `script.xml` and the files of the mod, for the Freelancer Mod Manager (FLMM). OpenLancer did not check this package with FLMM yet. Check it with FLMM before you publish it.

- A new section is an `append` operation. A new line is a `sectionappend` after the header line and the key line of its section. A changed value is a `sectionreplace` of the raw line of the game file.
- A new `[BaseGood]` adds its lines to the `[BaseGood]` of the same base. A new `[Data]` line goes after the last line of its key.
- The text of the mod becomes `GENERATESTRRES("<text>")` and `GENERATEXMLRES("<text>")`. FLMM makes its own text DLL. A text with `"` or with a character outside ASCII refuses the change, because no known FLMM rule can hold it.
- A change that the operations cannot express puts the whole merged file into the package, with a warning. Examples are a removed line or section, and an anchor that is not unique in the file. A whole file cannot hold the text of the mod, so such a change with text refuses the package.
- `oltool export --flmm-probe` writes the text of the mod as it is. The probe is for the FLMM check only. It writes only its package.

### Package of the target OpenLancer

`<mod id>-<version>-openlancer.zip` holds the mod folder: `mod.json`, `strings.json`, `publish.json`, `patches/`, `files/` and the listed screenshots that pass the checks. It leaves out `.olsave/`, `props/`, the screenshots that the list does not name, and the hidden, system and temporary files.

### README, Nexus page and checklist

Each package has `<mod id> README.txt`: the display name, the version, the authors, the summary, the description, the requirements, the install and remove steps, the changes, the credits, the permissions, the page link and the notice of the Game Content Usage Rules of Microsoft. The FLMM script has the display name, the authors, the summary, the description and the page link in its header.

The output folder also gets `nexus-page.txt` and `CHECKLIST.txt`. `nexus-page.txt` has one block for each field of a Nexus page, with the name of the field: the mod name, the version, the author, the category, the summary, the description in BBCode with the install steps, the requirements, the files with a description of each, the changelog, the permissions and credits, the tags and the images. Copy each block into its field. "Files" lists each valid package of the version in the folder, also of an earlier export, so two exports for two targets give one page. The checklist has the changes of the version and one line for each entry that the mod adds, changes or removes, for testers. For example: "Good 'lf_hull': price 6600 -> 5600." No text of an export has a date.

The listed screenshots go into `screenshots\` of the output folder for the Nexus upload, with the names `01-<name>.<ext>` in list order. They do not go into the drop-in zip or the FLMM package, because a player extracts these into the game folder. The export deletes no file in `screenshots\`. An image of an earlier list stays there, and the "Images" block of `nexus-page.txt` names only the images of the current list.

The packages hold merged game files, so do not commit them. The export warns when the output folder is in a git repository. The export never writes into a game folder or into the mod folder.

### The output folder and the publish history

The output root of a mod is `exports\<mod id>` next to the profiles folder. Each version gets its own folder:

```
exports/<mod id>/
  publish-history.json
  1.0.0/
    <mod id>-1.0.0-hde-0.7.1.zip
    <mod id>-1.0.0-hde-0.7.1-flmm.zip
    <mod id>-1.0.0-openlancer.zip
    nexus-page.txt
    CHECKLIST.txt
    screenshots/01-<name>.png
```

`publish-history.json` records each export: the version, the time, the OpenLancer version, the content and page fingerprints, the output folder, and the target, the form, the file name, the SHA-256 and the guard digest of each package. It also records the last check of each target, the last 50 test plays, and the versions with the mark "published". The file is local and holds the full paths of your machine. It is never in the mod folder.

- Each write of the history holds `publish-history.lock`. A write waits up to 5 seconds for another program, then gives a warning and records nothing.
- A history file that does not load stops each export, because the guard needs the history. Correct the file, or move it out of its folder. The next record of a play, a check or a mark moves such a file to `publish-history.bad.json`, or to a numbered name when that file exists, and a new history starts.
- OpenLancer does not write a history file of another format version, and does not move it.
- The flat packages of an export before the history stay where they are.

### The same-version guard

Two different packages with the same version confuse players. The guard compares a new export with the exports of the same version in the history:

- An export of the same target and form with other game files is a conflict. An export of other content with a package of another target is a conflict too.
- A change of the texts only is no conflict. The guard digest of a package leaves out the README, the header of the FLMM script and the `mod.json` of the OpenLancer zip. The export keeps the version, and the history gets a new entry.
- Packages with the same bytes as an earlier export of the version add no history entry, when the content and the page fingerprints are also the same. A change of a text that only `nexus-page.txt` has, for example a tag, adds an entry, so that the publish checklist sees the current texts.
- A version with the mark "published" never gets other game files. Export as a new version.
- A replace of a version without the mark needs each target and form of the old export. The old entry stays in the history with the time of the new export as `replacedBy`.

`oltool export` prints the text of a conflict and exits 1. `--same-version` replaces a version without the mark. To replace a version with packages of two targets, give both targets in one `oltool export`.

The Export dialog asks after its checks and before it writes. **Export as <next>** writes the next patch version into `mod.json` and a changelog entry into `publish.json`, and exports again. **Replace version <v>** writes over the packages of a version without the mark. A replace that does not have each target and form of the old export is not available, and its tooltip tells why.

**Export and play** in the Export dialog exports first. Then it installs the export of the first checked retail target into the play copy of its base, and starts the game. A source build is never the base of an export. See "Play in the game" in [PLAY.md](PLAY.md).

## What the validator checks

Some broken references stop the engine load. Many others make the engine drop data and log nothing. The validator reports both. It compares the merged data with vanilla, so it reports only the problems that the mods add.

The editor also checks the references before each save, with the data lists (see "Data lists and pickers" and "The save gate"). The rule data of the lists is `src/OpenLancer.Data/Editing/Lists/list-rules.json`. Each rule maps one value of one key to a list. A rule is closed only when a line of the engine shows that the engine finds the value through the list. Each closed rule names that line. A rule of a file that Librelancer does not read, for example `gate_tunnel.ini`, stays open. The retail data gives no list error and four warnings: two `parent` lines in Li01 that name an object that does not exist, and two `lens_glow` lines in `stararch.ini`.

First, the INI checks:

- A ship package whose hull good does not exist or is not a ship hull (the engine load stops), or whose addon is not equipment (the engine drops it).
- A hull good without a ship (the load stops), or with a ship that is not in shiparch (the engine drops its packages).
- A ship package without an engine or a power plant.
- A market for a base that does not exist, a market good that does not exist, or a market good that the engine cannot sell (a ship hull, or a good without equipment).
- A market ship whose letter case differs from the ship package. The engine finds ship packages with a case-sensitive match.
- A ship that a market sells with min = max = 0 (warning: it is not for sale). A line whose transaction type is 1 tells that the base only buys the ship, so it gives no warning. Retail has such a line for each ship of each ship market.
- A duplicate nickname, two nicknames with the same hash, a nickname that is not ASCII, or an equipment section without a nickname. (The engine drops a `[Good]` or `[Ship]` without a nickname and logs `Missing required field nickname`.)
- A ship or good whose text id has no text.

When the INI checks find errors, the validator stops there. It then also reports the new lines of the engine's INI parser, because a parse error can cause a reference error: for example, an `addon` line with too few values is dropped, and the package then has no engine. When the INI checks find no errors, the validator loads the data as the game does, and reports:

- A load that throws, and ship packages that the engine did not resolve.
- Every new warning or error line of the engine, for example `Unknown entry` for a misspelled key. An engine error line is an error.
- For every ship package: an addon hardpoint that the ship model does not have, a model that does not load, and a gun or shield that does not fit its hardpoint by the server's rule, also on `internal` (the server rejects the purchase).

### Content checks of the retail game

Librelancer loads some data that the retail game does not accept or does not read. The content checks find this data for the targets HD Edition 0.7.1 and Freelancer 1.1. They run at each save of a tier-1 mod that names a retail target. They also run before each Play in the game, at each export of a retail target and in `oltool validate --target`. Each check compares the data with the mods with the data without them, so it reports only what the mods add. An error blocks Save, Play in the game and the export.

| Check | Level | What the game does |
|---|---|---|
| More than 10 jump holes and jump gates in one system | Error | It crashes. |
| A `[Good]` with a `price` of 0 or less, or a price that is not a number | Error | It can crash when a player buys or sells the good. A ship package has no `price` line, and needs none. |
| A system of `universe.ini` whose file has no `[Object]` | Error | It can crash when it loads the system. |
| A `[Munition]` with `requires_ammo = true`, when `MISSIONS\lootprops.ini` has no `[mLootProps]` for the munition or for a `[Gun]` that fires it | Error | It can crash. |
| A `[Trigger]` of a mission without a `Cnd_` line | Error | It crashes when it runs the trigger. Write `Cnd_True = no_params` for a trigger without a condition. |
| An `[Asteroids]` section whose `zone` is not a `[Zone]` of its system | Error | It crashes when it loads the system. |
| An `[NPCShipArch]` whose loadout has no engine | Error | It crashes when the ship tries to go into cruise. The check reads `MISSIONS\npcships.ini`, `MISSIONS\npcships_test.ini`, the `npcships.ini` of each mission folder and each file of a mod with an `[NPCShipArch]`. |
| A key that only Librelancer reads: the section `[Extended]` of `freelancer.ini`, a `[Data]` key that the retail loaders do not know (for example `newplayer`, `mpnewcharacter`, `mbases`, `navmap`, `mouse` or `cameras`), and `cruise_speed` of an `[Engine]` | Error | It does not read the key. Only OpenLancer reads it. |
| A boolean key with a value other than `true`, `false`, `1` or `0` (in any letter case) | Warning | It reads false and logs an error. For `requires_ammo` and `force_gun_ori`, the check gives an error, because the game can crash. |
| A line of the mod that replaces a line that HD Edition changed (HD Edition 0.7.1 only) | Warning | The mod can undo a fix of HD Edition. The check compares the HD Edition file with the file of a linked Freelancer 1.0 or 1.1 game. Without such a game, the check does not run. |

The crash checks follow the crash list of The Starport knowledge base. The retail data itself has two such cases, which the game uses only in scripted scenes. A cutscene missile has no `lootprops.ini` entries, and some capital ships of the story missions have no engine. These cases give no finding, because the checks report only what the mods add.

The content checks do not read models and textures. So they do not check collision parts with too many triangles, texture sizes, or cutscene animations that a model does not have.
