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
| `[NewsItem]` of `DATA\MISSIONS\news.ini` | none: patch sections are always added, because a news item has no key. A patch cannot change or delete a news item of the game. |
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
| `ol.new = true` | Add this section at the end of the file. A section with the same key must not exist. A new `[Base]` of `universe.ini` goes before the first `[system]` (see "universe.ini"). |
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

The engine reads the goods of all `[BaseGood]` sections for one base. So a `[BaseGood]` patch section is always added as a new section. Use it to sell more goods at a base:

```ini
[BaseGood]
base = li01_01_base
marketgood = my_package, 0, -1, 1, 1, 0, 1, 1
```

The values of `marketgood` are: good, rank, reputation, min, max, transaction type, price multiplier. The transaction type is 0 when the base buys and sells the good, 1 when the base only buys it, and 2 when the base only sells it. The engine sells a ship only when min or max is not 0.

The retail games and OpenLancer Play keep the last line of a good at a base, in the order of the market files of `[Data]`. The merge puts a new `[BaseGood]` of a patch after the sections of the game in its file. So a patch changes a line of the game with a later line of the same good in the same market file:

```ini
[BaseGood]
base = li01_01_base
marketgood = my_good, 0, -1, 150, 500, 0, 0.8
```

To remove a good from a base, write the buy-only form: min 0, max 0 and the transaction type 1. The base then stops the sale of the good, and it only buys the good. The Inspector and the agent tools write this form for a remove of a line of the game.

The retail games and OpenLancer Play skip a line in which each value after the good has its default value: rank 0, reputation -1, min 0, max 0, transaction type 0 and price multiplier 1. A value that the line does not give has its default value. The earlier line of the good then counts. So such a line does not remove a good. The Inspector, the save checks and the agent tools read such a line as no line. The Inspector and the agent tools do not write one, and they tell you why.

The price of a ship at a base comes from the market price of its hull good there. A line of the hull good, for example `marketgood = my_ship_hull, 0, -1, 0, 0, 1, 0.9`, gives the hull price times the multiplier. Without such a line, the price is the price of the hull good in `goods.ini`. A line of the ship package does not change the price.

The ship dealer shows this price for the hull. The trade-in value of the ship of the player is this price times 0.8. The retail games also apply the price variance of the base and the damage of the ship. OpenLancer Play does not apply them.

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

### universe.ini

The game reads the sections of `DATA/UNIVERSE/universe.ini` in file order. It links a station of a system file to its base only when it read the `[Base]` before the `[system]` of that system. The files of the game have each `[Base]` before the first `[system]`. A `[Base]` at the end of the file has no station in the game.

So the merge puts a new `[Base]` of a patch of `universe.ini` directly before the first `[system]` of the file. A new section of another type, for example a new `[system]`, goes at the end of the file:

```ini
[Base]
ol.new = true
nickname = ol01_01_base
system = Ol01
strid_name = $str:ol01_01_base_name
file = MODS\my-mod\BASES\ol01_01_base.ini

[system]
ol.new = true
nickname = Ol01
file = ..\MODS\my-mod\SYSTEMS\Ol01\Ol01.ini
```

- The place needs no directive. `ol.new = true` is enough, so the mod format does not change.
- "New systems" tells the lines of a new `[system]` and the place of its file.
- Two new bases go before the first `[system]` in the order of the patch.
- When a lower mod has a whole `universe.ini` with a `[Base]` after a `[system]`, the new base still goes before every system.
- When the file has no `[system]`, the new base goes at the end.
- The editor uses the same rule. A new `[Base]` goes before the first `[system]` in the edit view and in the drop-in zip. OpenLancer Play (F5) and Play in the game (Ctrl+F5) use the same place. The save writes the base with `ol.new`, and a later edit of the patch keeps the place.
- In the edit view, a base of the lower layers can come after a new base, for example when you delete it and add it again. The save then writes `ol.delete` and `ol.new` for that base, so the merge puts it after the new base. The save writes no line for the systems.
- The FLMM package refuses a new `[Base]` (see "FLMM package (preview)"). Use the drop-in zip.

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
| `ids_name`, `ids_short_name`, `strid_name`, `tradelane_space_name`, `individual_name` | `$str:` (a string) |
| `ids_info`, `ids_info1`, `ids_info2`, `ids_info3` | `$ifc:` (an infocard) |
| `ids_info` of a `[KeyCmd]` | `$str:` (a string) |

`individual_name` is the name of a bar character in `mBases.ini` and of a mission NPC. The name of a bar character has three lines: the faction, the title and the name of the person. For example, the three lines are "Ol Guild", "Harbor Rock Bartender" and "Ada Vance".

Freelancer also shows a string id in `ids_info` as an infocard with plain text, and some retail solars, objects and commodities use that. Librelancer shows only infocards, so use `$ifc:` for `ids_info`.

## Whole files

A file in `files/` adds a new file, or replaces the file at the same install path. Patches of later mods apply to the replaced file. A replacement discards the changes of the mods that load before it, and the merge reports a warning that names them. Put new assets under `DATA/MODS/<mod id>/` so that they do not collide with other mods.

The editor edits two kinds of whole files of the active mod:

- A file under `DATA/MISSIONS`, for example a test script, a replaced slot script or a file of ship builds.
- An INI file below `DATA/MODS/<mod id>/` of the active mod, for example the file of a new star system (see "New systems"). Put such a file in `files/` of the mod. The agent tools `system_create` and `delete_items` make and delete the file of a new system. This version has no menu item for it.

A line of another file names such a file by its path. For example, the `file` line of a new `[system]` or a new `[Base]` in `universe.ini` names a file in the mod folder (see "universe.ini"). A `[Data]` line of `freelancer.ini` names a new data file (see "freelancer.ini").

Each section of such a file is editable, also a section whose key another section has. The save writes the whole file as text into `files/`. A text file keeps its comments and the lines that do not change. A binary file becomes text, and the save tells it.

A new file can use text placeholders, and the save checks that `strings.json` has their text.

An INI file below `DATA/MODS/<mod id>/` stays editable also when the game or a lower mod has a file at the same path. The editor does not delete such a file, because the game then uses the other file again. A whole file outside `DATA/MODS/<mod id>/` that replaces a file of the game or of a lower mod stays read-only, except under `DATA/MISSIONS`. The editor shows the other whole files of the active mod, but it does not change them.

When the active mod is in the OpenLancer repo, the save refuses a whole mission file and a whole INI file below `DATA/MODS/<mod id>/`. The content scan cannot tell a whole mission file or a whole INI file from a copy of a retail file.

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

## New systems

A new star system has two parts: a `[system]` in the patch of `universe.ini`, and a new system file in the folder of the mod. The system file is a whole file of the active mod, so the editor edits each of its sections (see "Whole files").

```ini
[system]
ol.new = true
nickname = Av01
file = ..\MODS\my-mod\SYSTEMS\Av01\Av01.ini
pos = 4, 5
visit = 0
strid_name = $str:av01
ids_info = $ifc:av01.info
NavMapScale = 2.5
```

- The system file goes to `DATA/MODS/<mod id>/SYSTEMS/<nickname>/<nickname>.ini`. The mod stores it in `files/` as text.
- The `file` line names the file relative to the folder of `universe.ini`, `DATA/UNIVERSE`. So the value starts with `..\` to go up to `DATA`. The game, OpenLancer Play and the editor resolve the `..` part.
- The lines come in the order of the example, and `nickname` is always first. The game reads the system file at the `file` line, with the nickname that the section has before that line.
- A new system has no `msg_id_prefix`. The game writes one line to its log for such a system, and NPC speech that names the system is silent. A prefix of another system makes the NPCs say the wrong name.
- `pos` is the place on the nav map grid, 0 to 15 on each axis. `visit = 0` shows the system on the nav map after the first visit. `visit = 1` shows it in a new game.
- `strid_name` is the name on the nav map, and `ids_info` is the infocard. Write them as text placeholders. The new system file can use placeholders too, for example the `ids_name` of its sun.
- The nickname has 2 to 15 letters, digits or `_`, and it starts with a letter. The game keeps only 15 characters of a system nickname in its route tables. The id of a system is the FLHash of its nickname, and the game drops a second system with the same id.

The agent tool `system_create` makes a new system (see "The universe tools" in `docs/MCP.md`). OpenLancer copies a template system:

- The `[system]` gets a new name, a new infocard, its place and its visit flag. It copies the `NavMapScale` of the template.
- The new file gets these sections of the template, in this order: `[SystemInfo]`, `[Archetype]`, `[TexturePanels]`, `[Dust]`, `[Music]`, `[Ambient]`, `[Background]`, each `[LightSource]` and each `[EncounterParameters]`. A light keeps its nickname, because a light name is unique only in its file. The faction of `[SystemInfo]` can change.
- A sun goes to the center, `<nickname>_sun`. It copies the archetype, the star, the colors, the atmosphere range and the numeric infocard of the first sun of the template. It gets a new name. A template without a sun needs the archetype and the star of the sun.
- The death zone of the sun goes to the center too, `zone_<nickname>_sun_death`. It copies the `SPHERE` zone with `damage` at the sun of the template. Without a death zone, the player flies through the sun.
- When the template sun has no death zone, the zone gets the defaults of the rule data. Its radius is the atmosphere range of the sun less 1000 m, or 10000 m when the sun has no range. Its lines are `damage = 10000`, `sort = 99.5`, `density = 0` and `relief_time = 0`.
- The names and the infocard go into `strings.json`. The new system is one entry of the edit history. One Undo takes back the `[system]`, the file and the texts.

A rename of a system changes its nickname and value 0 of each `goto` that names it. It also changes the `system` line of each `[Base]` of the system and the mission lines that name it. The file keeps its path. OpenLancer renames only a system that the active mod adds. A save game in the system does not find it after the rename.

A delete of a system removes its `[system]` and its file. Each object of another system whose `goto` names the system goes too, with its traffic zones and its gate lock lines. OpenLancer deletes only a system that the active mod adds, and it always asks first. A system with a base cannot go in this version. The mission lines that name the system or its objects show "Missing". A save game in the system does not find it after the delete.

The file stays when the active mod does not add it in its folder, or when another system uses it. The delete then removes only the `[system]`, and a warning tells why the file stays.

The checks of a system and of a base of `universe.ini`:

| Problem | Severity | What the game does |
|---|---|---|
| The `file` line comes before the `nickname` line. | Error | The game loads the system under another id. |
| Two systems have one nickname. The game ignores letter case. | Error | The game drops one of the two systems. |
| The `file` line names no file of the edit view. A new file that you did not save counts. | Error | The game loads the system without its objects and zones, and the game can crash. |
| `pos` is outside 0 to 15 on an axis. | Warning | The nav map shows the system off the map. |
| Two systems have one `pos`. | Warning | Their stars overlap on the nav map. |
| No jump gate or jump hole of another system leads to the system. | Warning | The player can only go there with a mission or a new game start. |
| Two systems name one file. | Warning | The game loads its objects twice. |
| A `[Base]` comes after the `[system]` of its system. | Warning | The game does not link the base to its station. |

An error stops the save only when the active mod writes the line. A problem of a line of the game or of a lower mod is a warning. Two different system nicknames with one FLHash get the FLHash error of the data lists (see "The save gate"). The Inspector shows these problems above the sections of a `[system]` or a `[Base]`, and the raw INI block marks the line of each problem. The count of the errors that block the save includes these errors. The agent tools `get_system`, `get_item` and `check` give them too.

The editor shows a new system before the save and after it:

- The Systems table of the Database reads the `[system]` sections of the edit view. So a new system shows at once, and a rename, a delete and an undo show at once too. The row of a new system has a dot, the tooltip "New. Save to open it in the Scene." and empty counts. A click on the row selects the `[system]`.
- The Inspector page of a `[system]` shows its rows from the edit view. The Identity section has "Nickname", "Name", "Infocard" and "File", then a "Scene" row with the link "Open system". "Nickname" and "File" are read-only. The Universe section has "Nav map position", "Nav map scale" and "On the nav map".
- The position has two whole-number fields from 0 to 15. "On the nav map" is a choice of "After the first visit" or "On the map at start". The choice changes only the flag with the value 1 of `visit`, and it keeps the other flags. The closed Advanced section has "Visit flags", "Visit value" and "Message id prefix". The raw INI block shows the lines.
- The Scene builds a system from the loaded game data. So it opens a new system only after the save and the reload of the game data. Before that, an open of the system shows a card: "This system is new. Save the mod to open it in the Scene." The Save button runs File > Save, with the save gate.
- During the reload, the card says "The editor loads the game data." Then the Scene opens the system. When a field edit or a drag runs at the end of the reload, the open waits for its end. Each open of a new system shows this card. Examples are "Go to partner", the "Go to" of a completeness step, the Database, the link "Open system" and follow mode of the agent. The `[system]` of the system gets the selection.
- A rename of the system before the save keeps the card with the new nickname, also a rename of the agent. An undo of the create and a delete of the system close the card. When the Scene cannot show the card, the Console tells why. Examples are the first load of the game data, and two `[system]` sections with one nickname.
- The Universe panel shows a new system at its place before the save, with a blue dot. Its new jump connections show as lines (see "The Universe panel").
- After the save and the reload, the row of the system has no dot and shows the counts of the loaded game data. A click on the row selects the system. Its page has the same rows of the `[system]`, and it also shows the environment, the music and the contents of the system.

### Jump connections and gate locks

A jump connection is a pair of objects in two system files, or in one system file for a hole pair. Each object has a `goto` line that names the system and the object at the other end, and the tunnel. The `goto` lines are in the system files, not in `universe.ini`.

```ini
[Object]
nickname = Li01_to_Av01
ids_name = $str:li01_to_av01
pos = 50000, 0, 0
rotate = 0, 90, 0
archetype = jumpgate
ids_info = 66145
jump_effect = jump_effect_bretonia
reputation = li_n_grp
behavior = NOTHING
difficulty_level = 10
goto = Av01, Av01_to_Li01, gate_tunnel_bretonia
loadout = jumpgate_li_01
pilot = pilot_solar_hard
```

- The type of the solar archetype gives the kind: `JUMP_GATE`, `JUMP_HOLE` or `AIRLOCK_GATE`. An object with a `goto` line and an archetype of another type is no jump.
- Each end of a pair names the other end. Both ends have one kind. A pair of two kinds puts each end in another route table (see "Route tables").
- The game records the jump with the nickname that the object has before its `goto` line. So `nickname` comes first.
- The game crashes with more than 10 objects with `goto` in one system. Airlock gates count too. The retail maximum is 9, in Li01 and Br04.

The agent tool `connection_create` makes a pair, and `connection_set_lock` sets the locks of a gate (see "The universe tools" in `docs/MCP.md`). When OpenLancer makes a jump gate pair or a jump hole pair:

- Each new end gets the lines in the order of the example. A gate to a system with `msg_id_prefix` gets that line after `archetype`. A jump hole has no `reputation`, `behavior`, `difficulty_level`, `loadout` and `pilot` lines. It has `visit = 0` as its last line.
- The nickname is `<system>_to_<other system>` for a gate and `<system>_to_<other system>_hole` for a hole. When an object or a zone has that nickname, OpenLancer adds `_1`, `_2` and so on.
- The name is "<name of the other system> Jump Gate" or "<name of the other system> Jump Hole". It goes into `strings.json`. The infocard is the infocard that the retail ends of the kind share.
- An end goes toward the other system on the nav map. Its distance from the center is the distance of the farthest object of its system, and at least 50000 m. Its front looks at the center. A hole pair in one system has no direction, so its ends go to the two sides of the center on the x axis. So do two systems at one place of the nav map.
- An end keeps 8000 m from each jump object of its system: two traffic zones of 4000 m. When its place is closer, the end goes 8000 m farther out until the place is free. So a second pair to the same system goes past the first pair.
- A template object can give its archetype, infocard, jump effect, behavior, difficulty, visit flag, loadout, pilot and tunnel.
- A gate gets the local faction of its system as `reputation`. Its loadout and its pilot come from the gates with the same faction. Without such a gate, they come from the rule data for the house of the faction, for example `li` for `li_n_grp`. A gate without a loadout has no lights.
- Each new end gets a traffic zone, `Zone_<object>_traffic`: a `SPHERE` of 4000 m with `pop_type = jumpgate` or `pop_type = wormhole`. NPCs come and go at the end only when the zone has encounters.
- An end can name an existing object without a pair. Then OpenLancer writes only the `goto` line of that object. This fixes a one-way object of a lower mod or of a raw edit.
- Each new end gets the lock that the request asks for. A lock line of `InitialWorld.ini` can already name the nickname of a new end. OpenLancer then keeps or removes that line, as the request asks. An existing end keeps its locks when the request asks for no lock.
- The new pair is one entry of the edit history, also when it changes two system files, the gate locks and `strings.json`.

A pair is one jump connection item, so a delete of one end deletes the pair. The other end goes when it names the end back. The gate lock lines of each end go too. Of the traffic zones, only the zones that `connection_create` makes go with the pair: `Zone_<object>_traffic` and `Zone_<object>_traffic_<n>`. A retail traffic zone has another name, so it stays. When a zone with `pop_type = jumpgate` or `pop_type = wormhole` stays and its size holds a deleted end, the delete warns and asks.

A one-way object goes alone. A one-way object that goes to an end is no part of the pair, so it stops the delete of that end. The Inspector and `set_fields` lock the `goto` line, so a change of one end cannot break the pair. The raw INI block can still change the line (see "Create, duplicate, rename and delete" and "The raw INI block").

The gate locks are in the `[locked_gates]` section of `InitialWorld.ini`:

```ini
[locked_gates]
locked_gate = 2926089285
npc_locked_gate = 2926089285
```

- Each value is the FLHash of the nickname of a gate or a hole, as a decimal number.
- `locked_gate` locks the gate for the player in a new game. A mission opens it with `Act_LockDock`. The route tables leave out the gate, also after a mission opens it.
- `npc_locked_gate` locks the gate for the NPCs. It does not change the route tables.
- OpenLancer can change the locks of a gate of the game too. Then the patch of `InitialWorld.ini` changes. The story can depend on such a lock.
- OpenLancer Play does not lock gates.

The checks of the jump connections:

| Problem | Severity | What the game does |
|---|---|---|
| A system has more than 10 objects with `goto`. | Error | The game crashes. |
| An object has its `goto` line before its `nickname` line. | Error | The game records the jump under another object. |
| The other end does not name the object in its `goto` line. | Warning | The player cannot jump back. |
| The two ends of a pair have two kinds. | Warning | The game counts each end in another route table. |
| An object with `goto` has an archetype that is no jump gate, jump hole or airlock gate. | Warning | The player cannot use the object as a jump. |
| A gate lock names no jump gate, jump hole or airlock gate. | Warning | The game ignores the line. Vanilla 1.1 has six such lines. They name the plain nicknames of three hole pairs that the system files do not have. |
| The active mod gives a gate a player lock. | Warning | The route tables leave out the gate. |
| The active mod puts a jump object closer than 8000 m to another jump object of its system. | Warning | The traffic zones of the two objects overlap. |

The count error stops the save of a data mod (tier 1) when the active mod makes the count go above 10. The order error stops the save when the active mod writes the `goto` line. Each other case is a warning. A `goto` line that names a system or an object that does not exist gets the error of the data lists.

The Inspector shows the problems in the Jump connection component of the object, and above the sections of `[locked_gates]`. The spacing warning shows at the position of the object. Retail has seven pairs of jump objects closer than 8000 m. So a jump object that the active mod does not place gets no spacing warning. The raw INI block marks the line of each problem. The agent tools `get_item` and `check` give them too.

### Trade lanes

A trade lane is a chain of `[Object]` rings in one system file. Each ring links to its neighbors, and the two end rings have the names of the lane.

```ini
[Object]
nickname = Av01_Trade_Lane_Ring_1
ids_name = 60245
pos = 51400, 0, 0
rotate = 0, 90, 0
archetype = Trade_Lane_Ring
ids_info = 66170
next_ring = Av01_Trade_Lane_Ring_2
behavior = NOTHING
tradelane_space_name = $str:av01_lane_start
reputation = li_n_grp
difficulty_level = 10
loadout = trade_lane_ring_li_01
pilot = pilot_solar_hard
```

- A ring has the archetype `Trade_Lane_Ring`, of the type `TRADELANE_RING`. The game makes lanes only from objects with that archetype. So the lane tools follow only the links to rings. A link to another object, for example a jump gate, ends the lane. A delete or a repair of the lane never changes that object.
- `next_ring` names the next ring, and `prev_ring` names the ring before. The first ring has no `prev_ring`, and the last ring has no `next_ring`. The links stay in one system file.
- The two end rings have `tradelane_space_name`. The HUD shows "<first end name> -> <last end name>". When one end has no name, the HUD shows no lane label.
- The HUD follows the links to the two ends. A chain that goes in a circle makes the game hang.
- The rings of a retail lane stand 5165 to 9445 m apart, and the median is 7500 m. A retail lane has 4 to 15 rings.
- `rotate` turns the front of the ring, its negative z axis, along the lane. It has the pitch, the yaw and no roll of the direction from the first ring to the last ring.

The lane zone is a `[zone]` where lane traffic spawns:

- Its nickname has the word `Tradelane`, and the letter case counts.
- Its shape is `BOX`, and its x size and its y size are each more than 10 times smaller than its z size.
- Without such a zone, the lane has no traffic. The lane still works.
- Lane traffic spawns only when the zone has encounters. Give them with the agent tool `scene_set_encounters`.
- `lane_id` and `tradelane_down` have no effect in the retail game. OpenLancer writes them as the retail files have them.

OpenLancer finds the lane zone of a lane. It is a zone that meets these rules, and whose z axis passes the first and the last ring within 1500 m. Each of the 124 retail lanes has such a zone.

The agent tool `trade_lane_create` makes a lane (see "The universe tools" in `docs/MCP.md`). When OpenLancer makes a lane:

- An end can be a point or an object. An end at an object goes on the line toward the other end. Its distance from the object is the `solar_radius` of the archetype of the object plus 2000 m.
- When the two end objects stand too close for these distances, the ends pass each other, and the plan stops.
- When a zone with `damage` holds that place, the end goes farther along the line, to 2000 m past the edge of the zone. So a lane toward a sun ends outside the death zone of the sun.
- The ring count is the length of the lane divided by 7500 m, rounded to a whole number, plus one. A lane has 2 to 100 rings, and two rings are 1000 to 20000 m apart. A distance outside the retail range gives a warning.
- The rings get the nicknames `<system>_Trade_Lane_Ring_<n>` with the first free numbers. Their lines come in the order of the example.
- The name `60245` and the infocard `66170` are generic ring texts of both games. All HD Edition rings use that name. `behavior` and `difficulty_level` come from the rule data.
- The faction is the local faction of the system. The loadout and the pilot come from the rule data for the house of the faction, for example `trade_lane_ring_li_01` for `li_n_grp`.
- A template ring can give its archetype, name, infocard, behavior, difficulty, faction, loadout and pilot.
- An end name is a new text in `strings.json`, or the `ids_name` of the object at that end. An end without a name stops the plan.
- The lane zone is `Zone_<system>_Tradelane_<n>`: a `BOX` at the middle of the lane, with the `rotate` of the rings. It is 1000 m longer than the lane. Its x size and its y size are at most 500 m and at most one eleventh of its length. So a short zone meets the rules too.
- The zone gets `pop_type = major_tradelane` and the other values that most retail lane zones have. It has no encounters.
- A ring inside a zone with `damage` or inside the `solar_radius` of an object gives a warning.
- The new lane is one entry of the edit history, with its rings, its zone and the texts.

The other edits of a lane:

- `trade_lane_insert_ring` adds a ring after a ring, or before the first ring. The new ring takes the links of its place, and it copies the lines of the ring next to it. Its default place is the middle between the two rings, or one ring distance beyond the end.
- After the last ring or before the first ring, the lane gets longer. The end name moves to the new end ring, and the lane zone follows the new ends.
- A delete of a ring links its neighbors to each other. A removed end ring makes its neighbor the new end, and the end name moves to it. The lane zone follows the new ends and keeps its extra length.
- The new links, the end name and the zone are parts of the lane, so the delete of a ring does not ask. When one ring or none stays, the whole lane goes with its lane zone, and the delete asks.
- A delete of a ring of a lane that goes in a circle cuts the circle at that ring.
- A stray ring links to a ring of a lane, but that ring links both ways with another ring. The stray ring is no part of the lane. So a delete of the stray ring does not change the lane.
- `trade_lane_repair` finds the rings of a lane from any ring. It follows the links in both directions. It also takes the rings with a broken link near the line of the end rings. Such a ring is within 1500 m of the line and at most 20000 m beyond an end ring. It brings the rings that its own links reach.
- A ring without links joins the nearest lane whose line passes it. So a repair joins a lane again after a raw edit deleted a ring of it.
- The repair sorts the rings along the line, writes `prev_ring` and `next_ring` again, and moves the end names to the end rings. It keeps the positions, so it stops when the rings do not stand on one line. A lane with good links does not change.
- `trade_lane_delete` deletes all rings of a lane and its lane zone. It always asks.
- A lane zone that the editor cannot change stays as it is, with a warning.
- A rename of a ring changes the links of its neighbors (see "Create, duplicate, rename and delete").

The Inspector and `set_fields` lock the `prev_ring` and `next_ring` lines, so a change of one ring cannot break the lane. The ring buttons of the Trade lane ring component and the agent tools `trade_lane_insert_ring`, `trade_lane_repair` and `delete_items` change the links. The raw INI block can still change the lines (see "The raw INI block"). The game reads these lines only on a trade lane ring. On another object, for example a jump gate, the lock tells you to remove the line in the raw INI block. The lane tools refuse such an object, and a delete of the ring that the line names also removes the line.

The checks of the trade lanes:

| Problem | Severity | What the game does |
|---|---|---|
| The links of a lane go in a circle. | Error | The game hangs when the HUD reads the lane. |
| A ring links to a ring that does not link back. | Warning | Ships on the lane can stop or go the wrong way. |
| An end ring has no `tradelane_space_name`. | Warning | The HUD shows no lane label. |
| A ring of another part of the lane links to an end ring, but the end ring does not link back. An example is a middle ring whose `next_ring` line a raw edit removed. | Warning | Ships on the lane can stop at that ring. The text names the repair, also for a missing end name of that ring. |
| A ring links to an object that is not a trade lane ring. | Warning | The game does not count the object in the lane. |
| A ring has a lane name, but it has no links and no ring links to it. | Warning | The ring is no part of a lane. |
| The active mod places a ring inside a zone with `damage` or inside the `solar_radius` of an object. | Warning | Ships on the lane can take damage or hit the object. |
| A lane zone does not cover the first and the last ring of a lane near it, and the lane has no other lane zone. | Warning | Lane traffic spawns away from the lane. |

The circle check follows the links as the HUD does: each direction alone, and also a stray link. A walk that comes to a ring of the walk in the other direction is no circle. A ring before a circle also gets the problem, because the walk of the HUD from it goes round the circle.

The circle error stops the save when the active mod writes a link line of a ring of the circle. A circle of the game or of a lower mod is a warning. The save gate gives one error for each circle, on the first ring of the circle in the file whose link line the active mod writes. A link line that names no object of the file gets the error of the data lists.

Each text names a fix: the button Repair lane and the agent tool `trade_lane_repair`, a delete, the Lane name row and `set_text`, or a move. A repair sorts the rings along their line, so a ring off the line stops it. The text then names the move or the delete of that ring.

The circle text names the ring whose delete cuts the circle. When the circle has a stray link, that is the ring with the stray link, because the relink of a delete does not follow a stray link.

The zone warning comes after a move of an end ring, or after a raw edit that adds a ring beyond an end. A lane zone that covers the first and the last ring of a lane near it is the zone of that lane, so it gives no warning for another lane.

The Inspector shows the circle, link and name problems in the Trade lane ring component of the ring. The placement warning shows at the position of the ring, in the Solar component. The zone warning shows above the sections of the zone. A ring of the game or of a lower mod gets no placement warning. The raw INI block marks the line of each problem. The agent tools `get_item` and `check` give them too.

The retail lanes of vanilla 1.1 and HD Edition 0.7.1 give one warning. The lane under construction in Bw10 has no name at its start, because its first rings are not there yet.

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
- The editor refuses connection objects, nebulae, asteroid fields, the zones that hold them, and items of more than one system file. A stray `prev_ring` or `next_ring` line on an object that is not a trade lane ring does not make it a connection object (see "Create, duplicate, rename and delete").

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

The check also reports the clip of a START_MOTION event on a character as an error when the game does not have the clip. No clip has an empty name. The character then does not move. A clip on a COMPOUND names a script of its model, so it gets no check.

The clips are the scripts of the `.anm` files that the `[Animations]` lines of bodyparts.ini name, as the engine loads them. The check reads these files once for each view, and only for a script with such an event. An `.anm` file that does not read gives one warning, and the check keeps the clips of the other files. The game does not: when it cannot read such a file, it closes during its load.

The scene does not make a REFERENCE entity, or an entity that a later entity with the same name hides. So the checks of the scene skip these entities, and a target with the name of such an entity names an object of another script of the scene. `flags` gives REFERENCE also as a name of another group, because the engine casts the name to its number: `flags = CAMERA` is 3, which is REFERENCE and LIT_DYNAMIC. A room fills in the `$terrain_*` templates with the terrain of its base, so the check reads the template of each base that plays the script. The game makes no COMPOUND of a script that only `[Trigger] Act_CallThorn` lines name, because it plays such a script in space with the objects of the mission. So the checks of a COMPOUND skip such a script.

The check does not know the objects of the other scripts of a room. So a target that names no entity of the script is not an error, also for SET_CAMERA and START_PATH_ANIMATION. The engine stops when no script of the scene has that name.

A THN patch that does not apply is a merge error. Each problem of a cutscene has its place: the cutscene, the element with its key and the path of the value. A click on the problem in the Console of the editor opens the cutscene and selects the element. When an edit after the save moved the element, the key finds it. When the key names no element or more than one element, the click selects the cutscene.

A change of the active mod in the game data can delete, rename or break an item that a cutscene names, for example a costume. The save gate of the editor then checks that cutscene too (see "The save gate"). It can be a retail cutscene that no mod changes. The export runs the save gate of the mod and refuses such a mod (see "Export"). `oltool validate` does not run the save gate, so it does not check a cutscene that no mod adds or changes.

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

- The header: the display name, the id, the version, the authors and the chips of the state. The tooltip of a chip tells more. **Export...** opens the Export dialog of the mod. The dialog refuses the export of a mod that the open profile does not hold (see "Export"). **Play** plays the mod alone in a linked game (see "Play in the game" in [PLAY.md](PLAY.md)). The **...** menu has Make active mod, Duplicate..., Rename..., Add to profile or Remove from profile, Open folder, Open export folder and Show in Console.
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

The Inspector edits ships, equipment, goods, the other sections of the item files and the market lines. It also edits the parts of the bases, and the objects, zones and lights of the open star system. The sections that set the fields of a system take edits too. The edits go into the active mod. A field shows each line of a key on its own text line, with the values separated by `, `. The editor reads the text as the merge reads a patch line, so a value gets the type that a save and a reload give it (see "Value types in binary files"). For example, in a binary file, `true` that replaces the text `TRUE` stays `TRUE`, and `200` that replaces a float is a float. An empty field removes the key.

The parts of a base are its `[Base]` of `universe.ini`, its base file, its room files and its `[MBase]` in `mBases.ini`. The base file has a `[BaseInfo]` and a `[Room]` for each room. The children of an `[MBase]` are `[MVendor]`, `[BaseFaction]`, `[GF_NPC]` and `[MRoom]`. The fields of these sections get the checks of their types and of their lists (see "Data lists and pickers").

`[BaseInfo]` also has `ship_repair_cost`, the cost factor of a ship repair at the base. Retail reads it, and OpenLancer does not read it. `individual_name` of a `[GF_NPC]` holds the name of the character as a text (see "Text: strings.json").

`mBases.ini` is below `DATA\MISSIONS`, but its edits are patches, as the edits of the other data files are. A patch finds a child section by its nickname. So an `[MVendor]` and a `[BaseFaction]`, which have no nickname, are read-only. An `[MRoom]` whose nickname another `[MRoom]` has is read-only too, for example `Bar`. When the active mod holds a whole copy of `mBases.ini`, each section takes edits.

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

The page of a star system and the page of a `[system]` of `universe.ini` have the same rows of the `[system]` (see "New systems"). "Nav map position" has two whole-number fields from 0 to 15. Drag a field to change its value, or double-click the field and type the value. Each field is one entry of the edit history. A line with another value, for example `pos = 4.5, 5`, gets text fields, so the row shows each value.

Each change of the text shows at once. While a field is active, it stays in its place, also when its text adds or removes the key. After the field ends, the key moves to its list, for example from "Unset keys" to "Keys". Two kinds of field change the data only when the field ends: the text of a key that holds a text id, for example a name, and a market line.

A blue label marks a value that the active mod sets. The tooltip of the label names the layer that sets the value: the base data, a mod that loads before the active mod, or the active mod. A lock tells why a row cannot change, for example the key of a section, or a section that a patch cannot change. The `goto` line of a jump gate and the `prev_ring` and `next_ring` lines of a trade lane ring are locked rows. Their lock names the tools that change the pair or the lane, and the raw INI block can still change these lines. The Jump connection component has a lock dropdown, and the Trade lane ring component has ring buttons. They change a pair or a lane by hand (see "Components").

Revert, in the menu of a row or in the item menu, puts back the lines of the lower layer: the base data with the mods that load before the active mod. The next save then writes no line for them. A section that the active mod adds has no lower lines, so it has no Revert. A key of a group of lines that belong together (see "Lines that belong together") reverts only with its whole section. When the active mod sets the `file` of a `[system]` of `universe.ini`, the Revert of the whole item is not available, because the revert would move the contents of the system to another file.

"Markets" lists the market lines of a good, by base. Each value of a line has its own field: "Rank", "Reputation", "Minimum stock", "Maximum stock" and "Price multiplier". "Buys or sells" under them sets the transaction type: "Buys and sells", "Only buys" or "Only sells". An edit of a field changes only its value. The engine ignores a line without the price multiplier. So an edit of a shorter line also writes the values of a new line in the missing places.

The "Price multiplier" of a line of a ship package is read-only, because it does not change the price of the ship (see "Markets"). Its tooltip names the hull good of the ship. To set the price of the ship at a base, open the hull good from the Goods section of the ship page, and use its "Markets" section.

"Markets" shows the last line of each base, because the games keep the last line of a good at a base. An edit of a line of a lower layer writes a later line of the active mod in the same market file (see "Markets"). A second edit changes that later line. The remove button of a line of the active mod deletes the line. When the line replaces a line of a lower layer, the remove button writes the buy-only form instead. A buy-only line with a line of a lower layer already stops the sale, so its remove button is not available.

The row "Change" under a line that replaces a line of a lower layer shows the result in plain words, for example "Removed: the base only buys it". Its tooltip names the line that the save writes. Revert next to it removes the line of the active mod, and the line of the lower layer counts again.

"Add to base" adds a line to a `[BaseGood]` of that base that the active mod adds. Without one, a new `[BaseGood]` goes into the market file in which the base sells goods of the same category, else into the first market file of the base. The editor refuses a base that already sells the good in a market file.

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
- The active mod replaces the system file with a whole file outside `DATA/MODS/<mod id>/` (see "Whole files").
- The editor takes no edits for the mod, for example when another editor has the mod open.

These keys are locked, and the other keys of the section take edits:

- The key of the section, for example `nickname`.
- The `encounter`, `faction` and `faction_weight` lines of a `[Zone]` (see "Lines that belong together").
- The `file` of a `[system]` of `universe.ini`, because a change moves all the contents of the system.
- The `goto` line of an `[Object]`, because it links the two ends of a jump connection. To change the pair, delete one end, and its pair goes too. Then make a new pair with the agent tool `connection_create`. The raw INI block can still change the line.
- The `prev_ring` and `next_ring` lines of an `[Object]`, because they link the rings of a trade lane. To change the lane, use Insert ring after, Repair lane or Delete ring on the Trade lane ring component. An agent uses `trade_lane_insert_ring`, `trade_lane_repair` and `delete_items`. The raw INI block can still change the lines. The game reads these lines only on a trade lane ring, so on another object the lock tells you to remove the line in the raw INI block.

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

The keys work only when no text field is active and no field edit or drag is open. A disabled menu item tells why in its tooltip.

Jump gates, jump holes, airlock gates and trade lane rings are connection objects. A connection object has an archetype of such a type, or a `goto` line. Only an object with an archetype of the type `TRADELANE_RING` is a trade lane ring, because the game makes lanes only from such objects. A `prev_ring` or a `next_ring` line on another object does not make it a connection object.

The Create menu and Duplicate cannot make connection objects, because each one needs a partner or a lane. The jump connection tool `connection_create` of the agent server makes a jump gate pair or a jump hole pair (see "Jump connections and gate locks"). The trade lane tools `trade_lane_create` and `trade_lane_insert_ring` make a lane and add a ring to it (see "Trade lanes"). In the editor, select a ring and click Insert ring after in the Inspector to add a ring to a lane.

A delete of a jump gate or a jump hole deletes its pair. A delete of a trade lane ring links the lane again. This version cannot delete an airlock gate. A rename of a connection object works.

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
- This version cannot copy connection objects. A copy of a jump gate or a jump hole has no partner. A copy of a trade lane ring is not a part of the lane. A stray `prev_ring` or `next_ring` line on another object, for example a sun, does not stop the copy. The copy keeps the line, and the game ignores it.

#### Delete

`Delete` removes the item, as Unity does:

- The child sections of the section go with it, and so do the objects whose `parent` names the object, again and again.
- A zone takes its `[Nebula]` and `[Asteroids]` sections, and the `[LootableZone]` sections that name it. The engine drops a nebula or a field without its zone.
- A line that names the item gets a safe fix in the same entry when one exists. The `ring` line of a planet goes. The lines of each hole of a deleted zone go from the `[Exclusion Zones]` sections of the nebula and field files. The engine skips a hole whose zone is not there. But retail gives the lines of that hole to the hole before it, so the lines must go.
- Another line keeps its value and shows "Missing" (see "Data lists and pickers"). Two cases give such a line. One is a fix of a line that the editor cannot change. The other is a line of a mission script that names a deleted object. The save stops at such a mission line until you fix it.
- A line whose rule refuses the delete names the line and stops the delete, as Unity RequireComponent does. This version refuses the delete of an airlock gate.
- A jump gate or a jump hole goes with its pair, because a pair is one connection item. The other end goes when its `goto` line names the end back. The traffic zones that `connection_create` makes for each end go, and so do the `locked_gate` and `npc_locked_gate` lines of each end. A one-way object goes alone. When the other end is in a file that the editor cannot change, the delete stops with the reason. This is an OpenLancer choice: Unity would keep the other end with a "Missing" link.
- A trade lane ring is a part of its lane, so a delete of a ring links the lane again (see "Trade lanes"). The neighbors of the ring link to each other, and an end name moves to the new end ring. The lane zone follows the new ends. When one ring or none stays, the whole lane goes with its lane zone. A ring can name a ring that goes, while the ring that goes does not name it back. Such a one-way link stops the delete. Delete that ring in the same delete, or remove its link line in the raw INI block. When its rings stand on one line, the agent tool `trade_lane_repair` also fixes the link. The game reads a link line only on a ring. So a link line of another object, for example a jump gate, goes with the ring that it names, and the delete asks.
- A one-way object whose `goto` line names an end that goes is no part of the pair. Its locked `goto` line stops the delete, and the message names the object. Delete the one-way object first or in the same delete. To keep it, change its `goto` line in the raw INI block. You can also pair it with the jump connection tool `connection_create` of the agent server.
- A delete of a nebula or an asteroid field removes only its section, as Unity "Remove Component" does. Its zone stays, with its encounters, its music and its damage.

A delete that removes only the item and its child sections runs at once. So does the delete of a jump connection in one file. Its other end, the traffic zones that `connection_create` made for it and its lock lines are parts of the item. So does the delete of a ring of a lane that keeps two or more rings. The new links, the end names and the lane zone are parts of the lane.

Another delete opens a dialog. It lists the items that go too, the lines that change and the lines that show "Missing". For a jump gate pair, the list names the other end and its file. For a lane that goes as a whole, the list names its last ring and its lane zone. The dialog also lists the files that name the item as text, and these warnings:

- A base that loses the last object of its system that docks with it. The game then puts the player at the center of the system after a launch.
- A base that loses the last object of its system with `base = <base>`. The navigation map then does not list the base, and its rooms show no planet.
- A mod that loads later and changes a section that goes. Its patch cannot find the section after the delete.
- A jump traffic zone with another name (`pop_type = jumpgate` or `pop_type = wormhole`) that stays at an end that goes. When the zone has encounters, NPC ships still come and go at that place. This warning also makes the delete of a jump connection in one file open the dialog.

A delete of a read-only section is refused with the reason, for example a light of the St02c file whose nickname another light also has.

The texts of the items that go stay in `strings.json` as unused texts of the mod, as for each other delete.

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
| Jump connection | `goto` | the jump connection tool `connection_create` of the agent server | refused: delete the object, and its pair goes too |
| Trade lane ring | `prev_ring`, `next_ring`, `tradelane_space_name` | the trade lane tools `trade_lane_create` and `trade_lane_insert_ring` of the agent server | refused: the ring buttons, the `Delete` key and the trade lane tools change the lane |
| Planet ring | `ring` | not in this version | removes `ring` |

A click on a chip opens the section of the page with its keys. A chip with a glyph has a problem, and its tooltip tells it. "Add component" offers the components that the object does not have. A right click on a chip offers "Remove component". Each add and each remove is one entry of the edit history.

The Jump connection component and the Trade lane ring component have their own sections. Each section has an info icon that tells what the component is and what it writes to the game files. The sections read the edit view, so a new pair and a new lane show at once, before the save. The Jump connection section has these rows:

- "Kind": jump gate, jump hole or airlock gate.
- "Destination system": the locked `goto` row with the lock reason. Its button selects the system, also a new system before the save.
- "Exit object": a link that goes to the partner. An exit object that the edit view does not have shows "Missing".
- "Partner": the button "Go to partner" and the state of the pair. The button opens the system of the partner in the Scene and selects the partner. For a new system that the mod has not saved, the Scene shows its save card, and the Inspector shows its `[system]` (see "New systems"). After the save, the Scene opens the system and selects the partner.
- "Traffic zone": a link to each traffic zone that `connection_create` makes for the object.
- "Lock at start": a dropdown with "No lock", "Locked for the player", "Locked for NPCs" and "Locked for the player and NPCs". A pick runs the plan of the agent tool `connection_set_lock`. The partner gets the same lock when the partner names this gate in its `goto` line. Only a jump gate and a jump hole have this row.

The Trade lane ring section has these rows:

- "Place in lane": for example "Ring 3 of 7", from the start of the lane.
- "Previous ring" and "Next ring": the locked rows of the links, with links to the rings.
- "Lane name": the end name, on the first ring and on the last ring only. The HUD reads only the names of the end rings. An edit changes the text in `strings.json` as one entry of the edit history.
- "Lane zone": a link to the lane zone that covers the lane.
- "Left name" and "Right name": the end names that the game gives to each ring of the lane. The left name comes from the last ring, and the right name comes from the first ring. These rows show the game data, so they change after the save.
- The buttons "Insert ring after", "Repair lane" and "Delete ring". They run the plans of the agent tools `trade_lane_insert_ring` and `trade_lane_repair`. "Insert ring after" selects the new ring. "Delete ring" does what the `Delete` key does, with the same dialog.

Each pick and each click is one entry of the edit history. A disabled control tells why in its tooltip. For example, the profile takes no edits, the editor cannot change the ring, or the plan stops. "Repair lane" is disabled when a repair has nothing to change. The lock dropdown writes only the gate locks of `InitialWorld.ini`, so a system file that the editor cannot change does not stop it. A lock that the plan refuses is disabled in the list, and its tooltip tells why. When the plan refuses each other lock, the whole dropdown is disabled. While a field of the Inspector takes an edit, the controls wait until the edit ends.

The page of a jump gate, a jump hole or a trade lane ring starts with a completeness panel under the components strip. An example is "2 of 3 steps complete". Each missing step tells what is missing and why the step is necessary. The text of a missing step is the text of its check. "Go to" selects the item of the step: the partner, the system, a ring or the lane zone. A lane without a lane zone goes to its first ring. A broken link of a lane also has the button "Fix", which repairs the lane.

| Component | Step | Done when |
|---|---|---|
| Jump connection | Partner goes back | The object at the other end names this object in its `goto` line. |
| Jump connection | Kinds match | Both ends are jump gates, or both ends are jump holes. |
| Jump connection | Jump objects of the system | The system has at most 10 objects with `goto`. The step name gives the count, for example "Jump objects of Li01: 6 of 10". |
| Trade lane | Links go both ways | Each link of the lane goes both ways, each link names a ring, and the links do not go in a circle. |
| Trade lane | Names at both ends | Both end rings have `tradelane_space_name`. |
| Trade lane | Zone covers the lane | A lane zone covers the first ring and the last ring of the lane. Without one, the step gives the position, the rotation and the size of a new lane zone. Give the new zone a nickname with the word `Tradelane` (see "Trade lanes"). |

A step whose check stops the save is an error, and the panel opens its list of missing steps. An airlock gate has no partner, so it has no panel. The game makes no lane from an object of another type with a link line. So such an object gets no place in a lane, no ring buttons and no panel.

These component checks give warnings, until a retail comparison proves an effect:

- A Dockable station whose archetype has no docking sphere. The player cannot dock there.
- A Dockable station that docks with a base of another system. After a launch, the game puts the player in the system of the base.
- An object with the nickname or the FLHash of an object of any system. The game can confuse the two objects.
- A `[Base]` that no object docks with, or without an object with `base` of it in its system. The Inspector of the base shows these warnings.

The base checks give the warnings of a Dockable station and of a `[Base]` (see "The save gate"). One fault gives one warning. So a base whose only dock object is in another system gets no warning of its own, and the object gets one.

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

The rule data gives the categories. A solar archetype whose type is in no template shows in "Other solar" with a lock: it does not spawn. Jump gates, jump holes, airlock gates and trade lane rings show with a lock, because each one needs a partner or a lane. The lock names the jump connection tool `connection_create` of the agent server, which makes a jump gate pair or a jump hole pair. It also names the trade lane tools `trade_lane_create` and `trade_lane_insert_ring`, which make a lane and add a ring to it.

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

### The Universe panel

The Universe tab at the bottom shows the systems of the edit view on the nav map grid, with their jump connections. The panel is read-only. It reads the `[system]` sections of `universe.ini` and the `goto` lines of the system files, as the agent tool `get_universe` does. So each edit shows at once, also an edit of the agent and a new system before the save. While you drag an item or type in a field, the panel shows the state before the drag or the typing. At the end, it shows the change.

The info icon in the toolbar tells what the panel shows. The toolbar also gives the count of the systems and of the new systems.

- Each system is a dot at its `pos`, with its name below or above it. A system without a name shows its nickname. The grid goes from 0 to 15 on x and from 0 to 14 on y. A larger y is lower, as on the nav map of the game. A place outside the grid shows outside it, because the nav map of the game shows that system off the map.
- A name shows only where it does not cover another name or another dot. Hold the mouse on a dot to see its name in the tooltip, or make the panel larger to see more names. The names of the selected system and of the system under the mouse always show.
- A solid line is a pair of jump gates, and a dashed line is a pair of jump holes. An airlock gate shows as a gate. Two ends of a pair make one line.
- A red line has a player lock in a new game (`locked_gate`). An NPC lock does not change the line.
- A jump whose other end does not go back has a line of its own. Its arrow points to the system of its `goto` line. The player cannot jump back.
- Two lines between the same two systems go side by side, for example a gate pair and a hole pair, or two hole pairs.
- A hole pair inside one system has no line. The tooltip of the system names it.
- A blue dot at a system marks a new system that the mod has not saved.
- A system without a `pos` line is not on the map. A note at the bottom left names it.

The tooltip of a system tells its name, its nickname and its jump objects, for example "Jump objects: 3 of 10". It also gives each jump connection of the system with its lock. A click on a system selects it, as a row of the Systems table of the Database does. The Inspector then shows the page of the system, or the page of the `[system]` of a new system. A double-click opens the system in the Scene. A new system shows the save card of the Scene (see "New systems").

The selected system has a blue ring, and its lines are wider. When you select an object of a system in another view, for example in the Scene or the Hierarchy, that system gets the ring. A selected jump gate or jump hole also makes its line wider.

Change the place of a system in the Inspector of the system. The agent tools `system_create`, `connection_create` and `connection_set_lock` add systems, add connections and change locks. The agent tool `capture_panel` with the panel `universe` gives an image of the panel. While no profile is open or the game data loads, the panel tells it.

### Data lists and pickers

A key that names another section, for example the `archetype`, the `loadout` or the `reputation` of an `[Object]`, gets a picker. The picker lists the sections that the engine reads for the key: the base data, the mods that load before the active mod, and the active mod with its unsaved edits. So a loadout that the active mod adds is in the list before a save. A mod extends a list when it adds sections. Only a set that the engine fixes, for example the type of a docking sphere, comes from the editor.

Click the field to open the picker. Type to filter the rows by name or nickname. A click on a row sets the value at once, and so do the up and down arrow keys. The Scene shows each value.

`Enter` sets the selected row and closes the picker. A double-click and a click outside close the picker and keep the value. `Esc` closes it and puts back the value of the opening. One opening is one entry of the edit history.

The first row is "None" for a key that the engine does not need. "None" removes the line. The footer tells the file of the hovered entry, the layer that gives it, and the number of INI lines that name it. The count does not include the templates of the cutscenes. A button at the right of the field selects the section of the entry.

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

The base files and `mBases.ini` use these lists. A base file is a file that the `file` line of a `[Base]` of `universe.ini` names. `mBases.ini` is the file of the bar characters, the dealers and the factions of the bases. The games read it at `DATA\MISSIONS\mBases.ini`.

| Key | Field | A value of the active mod outside the list |
|---|---|---|
| `[BaseInfo] start_room` | Picker of the rooms of the same base file, without None | Error: the game crashes when the player docks here. A `[BaseInfo]` that the active mod adds or changes without the key is an error too. |
| `[MBase] nickname` | The key of the section | Error: the game does not use the bar data of this block. |
| `[MBase] local_faction`, `[BaseFaction] faction`, `[GF_NPC] affiliation` | Picker of the factions | Error: the game ignores this value. |
| `[GF_NPC] body`, `head`, `lefthand`, `righthand` | Picker of the body parts of that kind | Error: the game ignores this value. |
| `[GF_NPC] accessory` | Text field, because the key can have more than one line | Error: the game ignores this value. |
| `[GF_NPC] base_appr` | Picker of the costumes | Error: the game ignores this value. |
| `[GF_NPC] voice` | Picker of the voices | Warning: unlisted values are allowed. OpenLancer opens the voice bank by its name. |

The start room of a base compares without letter case, as the engine compares it. So `start_room = bar` names the room `Bar`.

Some keys of `mBases.ini` name a part of the base of their `[MBase]`. `[GF_NPC] room` and `[MRoom] nickname` name a room of the base. Value 0 of `[MRoom] fixture` and of `[BaseFaction] npc` names a character of the same `[MBase]`. The base names these parts, so no list of the rule data can give them.

The picker of `[GF_NPC] room` lists the rooms of the base file of the base. The picker offers "None", because a character without a room line is a fixture of a room. A value outside the rooms shows as "Not listed". The footer counts the lines of the base that name the room. These are the `start_room` line, the room lines of the characters, the nickname of each `[MRoom]` and the doors of the rooms. A fixture line and an npc line can repeat, so they keep their text field.

The base checks report a value outside these parts with a warning (see "The save gate"). Examples are a room line that names no room of the base, and a fixture that names no character before it in its `[MBase]`.

The `file` key of a `[Nebula]` and of an `[Asteroids]` section uses the list of the nebula files or of the asteroid files. The list holds the INI files below `SOLAR\NEBULA` or `SOLAR\ASTEROIDS` whose sections all belong to a nebula file or to an asteroid file. So the texture files of these folders, for example `edge_shapes.ini`, are not in the list. The engine reads a file of any folder. So a value outside the list is valid when its file exists, and the picker shows it as "value (other folder)". A file that does not exist is an error: the game crashes when it loads the data.

A list of files reads each INI file of the active mod below `DATA/MODS/<mod id>/` (see "Whole files") with its unsaved edits. The file must fit the list: the extension, the folder and, for a list with a file class, the sections of the file. So the save does not change the list.

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

The list `animation` reads only the node and the string blocks of each `.anm` file, not the keys. It keeps the names of each file with the length and the last write time of the file on disk. So a save reads no `.anm` file again. The list follows the bodyparts.ini and the `.anm` files of the mods. The editor cannot add or change an `[Animations]` line yet.

An `.anm` file that does not read gives one warning, and the list keeps the clips of the other files. The game does not: when it cannot read such a file, it closes during its load. A save gives the warning of each such file of the active mod, and the save continues.

### The raw INI block

Each Inspector page that edits sections ends with "Loaded values (read-only)" and then "Raw INI". The raw block shows `[Type]` and one `name = values` line for each line of the section. Edit the lines, then click outside the block. The edit changes only the names whose lines you changed. One raw edit is one entry of the edit history, and one Undo puts back all its lines. The order of different names stays the file order. A new name goes at the end of the section.

The block refuses an edit and keeps nothing when the text has a problem: a second header, a comment, a line without `=`, a changed nickname line, a line that the type of its key cannot hold, or a key that no row can change. The Console line then holds the problem and your whole text, so you can copy it back. When the section changed while you typed, for example by an undo, the block also refuses the edit. A value outside its list does not stop the edit: the row above then shows the error. The block is read-only when the section is read-only.

The `goto`, `prev_ring` and `next_ring` lines of an `[Object]` are exceptions to the keys that no row can change. No row changes them, because they link the two ends of a jump connection or the rings of a trade lane. The block takes an edit of them. A raw edit that breaks a pair gets the warnings of "Jump connections and gate locks". A raw edit that breaks a lane gets the checks of "Trade lanes".

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
- A system file that the active mod changes, with more than 10 objects with `goto`. The game crashes. The error comes for each data mod (tier 1), also without a retail target, when the active mod makes the count go above 10. An `[Object]` of the active mod with its `goto` line before its `nickname` line is an error too. The game then records the jump under another object (see "Jump connections and gate locks").
- A trade lane whose links go in a circle, in a system file that the active mod changes. The error comes only when the active mod writes a link line of a ring of the circle. The game hangs when the HUD reads the lane (see "Trade lanes").
- A `[system]` of `universe.ini` that the active mod adds or changes, with one of three errors (see "New systems"). Its `file` line comes before its `nickname` line, so the game loads the system under another id. Another system has its nickname, so the game drops one of the two. Its `file` line names no file, so the game loads it without objects and zones and can crash. The error comes only from a line of the active mod.
- A base whose `[Base]`, base file or room file the active mod adds or changes, with one of these errors. The game can crash when the player docks at the base or enters the room:
  - The `[Base]` names no base file, or its base file does not exist.
  - The base file has no `[BaseInfo]`. A start room that is not a room of the base is an error of the data lists (see "Data lists and pickers").
  - A `[Room]` has no nickname or no room file.
  - A room file does not exist.

  The error comes only from a line or a file of the active mod. The same problem of the base game or of a lower mod is a warning.
- An error of the format checks of the export or of the content checks of the retail game, when the active mod adds it. See "Format checks" and "Content checks of the retail game". These checks run only for a tier-1 mod that names a retail target. They compare the data with the active mod with the data of the mods that load before it. They do not merge on the files of each target game. Play in the game and the export do that. For example, a good with the price 0 blocks the save, because the retail game can crash when a player buys or sells it.
- An error of the mission checks in a mission script that the active mod changes. Examples are a trigger without a condition (rule M01) and two triggers whose names have one game id (rule M27). A line of any script that names a name that the active mod removes or renames is an error too. A mission problem that the base game or a lower mod also has is a warning, so it does not block the save.
- A mission patch that does not merge on the game of a retail target of `mod.json` (rule T01). The check runs for each target with a linked game. It merges the lower mods of the profile on that game first, as Play in the game does. For example, the patch changes a trigger that the game of the target does not have. A section of the target that differs from the `ol.base` of the patch gives a warning (rule T02), because the changes of the target in the section go away. A target without a linked game gives one info line in the Console.
- An error of the checks of a cutscene, when the lower cutscene does not have the error. For example, a LIGHT has no `spatialprops.pos`. The gate checks each cutscene that the active mod changes. It also checks each cutscene of the edit view that a change of the active mod in the game data breaks. A whole cutscene file of the active mod that the engine cannot read is an error too. The game crashes when it plays such a cutscene, so the message starts with "The game crashes when it plays this cutscene."
- A template of a cutscene that the gate checks, when the template names no entry of its data list (see "Data lists and pickers"). The check reads the lists with the unsaved edits, so an unsaved INI edit counts, for example a new costume. A template whose item the active mod deletes, renames or makes unusable blocks the save, also when the template does not change. A room, a prop or a cart that a petalDB file of the active mod removes counts too. A rename of a ship or equipment with `rename_item` or the data API also changes such a template. A template that the rename changes gives no error (see "New items, renames and text").

  A change of the active mod in the game data can delete, rename or break an item that a cutscene names. The gate then checks that cutscene, also when the active mod does not change it. Examples are a retail cutscene, a cutscene of a lower mod and a cutscene that no INI line names. When the game plays such a cutscene, it crashes, or the scene does not show the item or play the clip.

  To find such a cutscene, the gate reads an index of the templates and the clips that each cutscene of the edit view uses. The index runs each script, so it also finds a template that the Lua text of a script joins from parts, for example with `..`. The editor builds the index when it opens the profile. It keeps the uses of each file by the content of the file, so after a save it runs only the changed scripts again. When the index is not complete at a save or at Play, the editor waits for it. The status bar first shows "The editor checks the cutscenes that use the changed items."

  The check of the lower cutscene uses the lower game data. So a template that the lower cutscene has with the same problem in the lower data does not block the save. This is also true after a rename of its entity, and after an edit of another line of its item. For example, the active mod changes the `mass` of a solar that has no `DA_archetype` line in the lower data either.

  These templates crash the game, so their messages start with "The game crashes when it plays this cutscene.":
  - the costume of a DEFORMABLE without `userprops.Actor`. A costume of the active mod whose `body` names no body part is not usable either, because the game drops it. The message tells why.
  - the solar archetype, the ship, the room or the cart of a COMPOUND. A solar archetype, a ship, an equipment or an asteroid without a `DA_archetype` line also crashes the game. It also crashes the game when the file of the line does not exist. The message tells which of the two is true.
  - an equipment of a COMPOUND whose section type gets no model, for example `[Light]`, `[Commodity]`, `[Scanner]` or `[Engine]`, also with a `DA_archetype` line. A `[Munition]` gets a model only with a `motor` line.

  These templates do not crash the game. The message tells what the scene does without the item:
  - the prop, the equipment or the asteroid of a COMPOUND: the scene shows the entity without a model. The game makes no equipment from `[Armor]` and `[Shield]` sections, so such a template is also in this group.
  - the effect of a PSYS: the scene does not show the entity.

  The engine finds the item of a template by the text as it is, without letter case. So a known name with a space before or after it names no item. The check skips a template that the room fills in, for example `$terrain_dyna_01`. A PSYS template that starts with `$` names an effect as it is, so the check reads it. The check also skips the templates `PlayerShip` and `PlayerShipEngines`, the costume of a DEFORMABLE with `userprops.Actor`, and the template of a SOUND. A SOUND can also name a voice line.

  The game makes no COMPOUND of a script that only `[Trigger] Act_CallThorn` lines name, so the check skips the COMPOUNDs of such a script. The check reads these lines with the unsaved edits. So you can add the trigger line and the cutscene in one save.
- A clip of a START_MOTION event on a character, when the list `animation` does not have the clip. As for a template, the gate checks each cutscene that the active mod changes. It also checks each cutscene that plays a clip that the active mod removes. The engine finds a clip by its name as it is, without letter case. No clip has an empty name, so an empty clip blocks the save too. The message says what the scene does: "The character does not move."

  The check applies only when the first target of the event names an entity, and the last entity with that name is a DEFORMABLE. The clip picker of the Inspector uses the same rule. A clip that the lower cutscene has with the same problem in the lower data does not block the save. A clip that the active mod removes blocks the save, also when the clip does not change.

The save also gives a warning for each `.anm` file of the active mod that the list `animation` cannot read. The warning does not block the save. If the game cannot read the file either, the game closes during its load.

The base checks also give warnings, which do not block the save. The Inspector shows them above the sections of a base, of its base file and of `mBases.ini`. The agent tools `get_item` and `check` with the scope `item` give them too. A data mod with a retail target gets some of them as errors of the content checks (see "Content checks of the retail game"):

- A door of a room that leads to a room that the base does not have. The retail games can crash when the player uses the door. A door without `room_switch` gives no warning.
- An `[MBase]` whose first line is not its nickname line. The retail games read the first line as the nickname.
- A base nickname with more than 63 characters: the retail games ignore the bar data of the base. A nickname with more than 31 characters: a mission condition cannot name the base. A base of the active mod whose nickname starts with "intro": OpenLancer also shows its rooms in the main menu.
- A fixture of an `[MRoom]` that names no `[GF_NPC]` before it in its `[MBase]`. The retail games show no character at this place.
- An `npc` line of a `[BaseFaction]` that names no character of its `[MBase]`. The game ignores the line.
- A `room` line of a character, or an `[MRoom]`, that names no room of the base. The game ignores the line.
- A base that no object docks with, and a base without an object with `base = <base>` in its system. An object that docks with a base of another system. A base of the active mod also warns when no object with its base line shows the name of the base. The trade lists of the game then skip the base.
- A base that sells more ships than the room of its ship dealer shows. The game shows only the first ships.
- A base that sells goods without the dealer of their category. The player cannot buy them.
- A dealer whose category the market does not sell. The dealer shows an empty list.
- A base of the active mod without an `[MBase]`. Its bar has no characters, and it offers no missions.
- A base whose market the active mod adds or changes, when the base sells a ship and has no line for each ship package. The check runs only when the ship markets of the game and of the lower mods have buy-only lines, as vanilla has. Vanilla lists each ship package at each base that sells ships. The trade-in value does not come from these lines (see "Markets"). The completeness of the base gives a fix that adds the missing buy-only lines.
- A room scene that shows a `$terrain_*` template, when the base has no `terrain_*` line for it. The room shows no model there.
- A character of the active mod without a body or a costume, a voice or a name. The game does not show the character.

A field-check error or a missing required key that the base game has too does not block the save. This is true when the key has the value of the base data, no lower mod changes the section, and the same check of the base version of the section gives the same error. For example, the active mod changes the loadout of a retail object, and the position of the object in the base data has a value that is not a number. The save continues with a warning that ends with "The base game has the same problem, so the save continues." Each other error blocks the save, also when it is a value of the base data: an error of a key that the active mod writes again, an error that a key of the active mod causes, an error of a key that a lower mod changes, and each error that reads another section.

The Console hides such a warning by default, as it hides each base-game message. The Inspector shows such a field problem muted, with a lock glyph. A missing required key keeps its normal look.

An entry is unusable when the engine cannot use it. A curve without points is unusable, and so is a name that two curves have. An entry is also unusable when its own checks give an error that makes the game drop it or stop loading. Some examples:

- A `[Munition]` with a `motor` and no `explosion_arch`. The game stops loading.
- A `[Gun]` without a valid `projectile_archetype`. The game drops the gun.
- An `[Object]` without a valid `archetype`. The game drops the object.

An error that makes the game crash does not make its entry unusable, because the game loads the entry. The game crashes only when it uses the bad value. For example, a jump gate with a `dock_with` that names no base is an error on its `dock_with` line, and the error blocks Save. The `goto` line of the partner gate names a gate that the game loads, so that line is no error.

A line that names an unusable entry can make its own section unusable. The gate follows such lines. For example, a missile without its explosion makes each gun that fires it an error. The game drops such a gun, so each loadout line that names the gun is an error too. When a lower mod or the base data made the entry unusable, the line gives a warning only. This is also true when the active mod changes another line of the entry.

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
- The save would write a whole mission file, or a whole INI file below `DATA/MODS/<mod id>/`, into a mod in the OpenLancer repo. The content scan cannot tell such a file from a copy of a retail file.
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

The Database has no menu item for Rename yet. Only the agent tool `rename_item` and the data API (`ItemEdits.Rename` in OpenLancer.Data) rename an item of the item files. They rename only an item that the mod adds. The Scene renames the objects, zones and lights of the system files, also of the lower layers (see "Rename").

At a rename, equipment and its good keep the same nickname, because the engine finds the equipment of a good by that nickname. The rename changes each line that names the item in a section that the mod can change. These are the `ship`, `hull`, `equipment` and `addon` lines of goods, `projectile_archetype`, and `marketgood`. The new nickname must be a value that a patch line can hold, for example without `,` or `;`.

A rename of a ship or equipment also changes the template of each cutscene COMPOUND that names the item. One undo puts back both. This follows the INI rename, which changes each line and each mission line that names the item. Unity also keeps the references to a renamed asset. A rename of a good also renames its equipment, so the COMPOUNDs that name that equipment change too.

A COMPOUND names a ship with the category `ship` or `spaceship`, and equipment with the category `equipment`. The engine finds the item by the text of the template as it is, without letter case. So the rename changes only a template with that text. A template of another kind of entity, for example of a PSYS or a DEFORMABLE, does not change. The rename finds such a COMPOUND in these cutscenes:

- each cutscene file that holds the old nickname as a word
- each cutscene with unsaved edits
- each cutscene that the active mod changes

A save writes the change of a lower cutscene as a THN patch of the active mod, with only the changed templates. A cutscene that the editor cannot change keeps the old name, and the rename warns first. The warning tells what the game does with that cutscene, and that the save gate does not check it. A COMPOUND that a later entity with the same name hides keeps the old name with no warning, because the engine does not make it.

A change of the active mod in the game data can delete or rename an item of a lower layer. The save gate then blocks the save when a cutscene names that item (see "The save gate"). An example is an `ol.rename` of a lower item in a patch that you write by hand.

When you change the text of a key that holds a text id (see "Text: strings.json"), the editor writes the text into `strings.json` and sets the line to its placeholder. The text of no other section changes:

- A line with a placeholder of the right kind keeps its key, when no other section uses that key.
- Otherwise the editor makes a new key: the nickname in lowercase for `ids_name` and `strid_name`, and the nickname with a suffix for the other keys, for example `li_gun01.info` for `ids_info`. When another section uses that key, or `strings.json` has an unused text with it, the key gets the section type, for example `li_gun01.good`, and then a number, for example `li_gun01.good_2`.

A retail gun and its good have the same `ids_name`, but the Inspector shows the gun and a shop shows the good. So the editor offers to set the same placeholder on each section of the item that has the same id. The sections share one text only when you accept the offer. An infocard from plain text gets one paragraph for each line.

A new item, a rename and a text edit read the data when you start them. When the data changes before the edit runs, the edit does not run, because it would undo the change. A rename also does not run when a cutscene template that it changes no longer names the item.

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
| `oltool validate <profile.json> --target <hde-0.7.1\|vanilla-1.1> [--game <folder>] [--json]` | Merge all mods of the profile on the files of the target game. Report the problems of the format checks, of the content checks of the retail game (see "Content checks of the retail game") and of the route tables (see "Route tables"). `--game` names the game folder of the target. Without it, the base of the profile must give the target. For HD Edition 0.7.1, the command also lists the changed lines of HD Edition that the mods replace. This needs a profile on Freelancer 1.0 or 1.1. A mod that changes data under `DATA` gets the note "Single player, or a server with the same mod." The command only reads the game folder. |
| `oltool export <profile.json> --mod <id> --target <target> [--target <target>]... [--game <folder>]... [--format dropin\|flmm\|all] [--out <folder>] [--checklist] [--flmm-probe] [--same-version]` | Make the packages of one mod for its targets (see "Export"). Repeat `--target` for more targets. `--game` names the game folder of a retail target. Repeat it for each retail target: the export uses each folder for the target of its game. Without it, the base of the profile must give the target. `--format` picks the package forms of a retail target, all by default. `--out` names the exact output folder, `exports\<mod id>\<version>` next to the profiles folder by default. `--checklist` prints the checklist and writes no file. `--flmm-probe` writes only the probe of the FLMM check, which is not for players. The probe does not change `nexus-page.txt`, `CHECKLIST.txt` and the screenshots of the folder. The export runs the save gate of the mod first, and an error of the gate stops the export with exit 1. The export records its packages and its checks in the publish history. An export of a version that the history has with other game files exits 1. `--same-version` replaces that version when it does not have the mark "published". The replace needs each target and form of the earlier exports of the version, so give all their targets in one command. A history file that does not load stops the export with exit 2. |
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

The Export dialog needs an open profile that holds the mod, because it runs the save gate of the mod (see below). The dialog reads the mod from its folder when it opens, so it uses the applied display name, version and targets. It shows the display name with **Change...**, which opens the Mods window at that field, and the publish checklist. It writes into the version folder of the output folder, and it tells that folder. After the export, it records the packages in the publish history, and it tells the next step: the upload to Nexus and the mark "published". When the Mods window has unapplied publish settings of the mod, the export asks to apply them first.

Each export runs the save gate of the mod first (see "The save gate"). When the gate finds an error, the export writes nothing. It gives the errors of a save after this text: "The export of mod '<id>' stopped. The save gate refuses the mod with <n> errors, so a save of the mod stops too." An example is a patch with `ol.delete` of a costume that an unchanged retail cutscene loads for a DEFORMABLE without `userprops.Actor`. The Export dialog, the agent tool `export_mod` and `oltool export` give the same errors:

- The dialog and the agent tool use the save gate of the editor when the mod is the active mod and has no unsaved edits.
- Else the export opens the profile with the mod active, only to read the saved files. The dialog does this for another mod of the profile, and `oltool export` always does it.
- The dialog refuses a mod that no open profile holds, because it cannot run the save gate of such a mod. It then writes no package, and the dialog and the Console show this text: "The export of mod '<id>' stopped. The open profile does not hold mod '<id>', so the export cannot run the save gate of the mod. Open a profile that holds the mod, then export again." When no profile is open, the dialog also refuses the mod, and the second sentence of the text is "No profile is open, so the export cannot run the save gate of the mod." Export and play refuses the mod in the same way.
- `oltool export --checklist` and `--flmm-probe` do not run the save gate. `oltool validate` does not run it either.

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
- When the mod changes the routes between the systems, a package of a retail target holds the three route tables of the game (see "Route tables").
- The text of the mod goes into `EXE\ol_<mod id>.dll`. Its index follows the rule of the retail game: 1 plus the number of `DLL` lines in all `[Resources]` sections. A line counts also when its DLL does not exist. On HD Edition 0.7.1 the index is 8, and on the retail game it is 7. The new `DLL` line goes after the last `DLL` line of the last `[Resources]` section.
- The package leaves out hidden and system files, names that start with `.`, `Thumbs.db`, `desktop.ini`, `*.oltmp`, `*.tmp` and names that end with `~`. The export gives a warning for each.

The export refuses these files:

- A whole `EXE\freelancer.ini`, `EXE\dacom.ini`, `EXE\dacomsrv.ini` or `EXE\flplusplus.ini` of the mod, for a retail target. Use a patch with `ol.append`.
- A whole INI file or cutscene of the mod with the same content as the file of the game in another form.
- A file with the bytes of a file of a build manifest (`data-manifest`).

A whole INI file or cutscene of the mod that replaces a file of the game with changes gives a warning. It undoes the changes of HD Edition and of other mods in that file.

### Route tables

The retail game reads three route tables at its start. `DATA\UNIVERSE\systems_shortest_path.ini` has the routes through all jump connections, `shortest_legal_path.ini` the routes through jump gates, and `shortest_illegal_path.ini` the routes through jump holes. The NPC routes, the mission targets and the routes of the nav map use them. The game makes new tables only when the first file does not open. A drop-in zip cannot delete a file of the game, so the tables must change with the jump connections.

A mod does not hold route tables. OpenLancer makes them when it builds the mods for a retail game. It makes them for the drop-in zip, the FLMM package and the play copy of Play in the game (Ctrl+F5).

- The build compares the routes of the merged data with the routes of the game data without mods. When they differ, the build adds the three tables as text files. When they are the same, the build adds no table.
- The routes can change only when the mods change `universe.ini`, a system file, a solar archetype file or `initialworld.ini`. Else the build does not read the jump connections.
- The tables follow the rules of the retail game. Each system with an open jump connection gets one section, with the shortest path to each system that it reaches. The first shortest path in the order of the system files wins.
- A `locked_gate` line of `initialworld.ini` closes its jump gate or jump hole for the tables. An `npc_locked_gate` line has no effect on the tables. An airlock gate counts only in `systems_shortest_path.ini`.
- The tables come from the data of each target game. HD Edition 0.7.1 locks a jump hole of Ku07, so the tables that OpenLancer makes for HD Edition 0.7.1 have no section for Ku07. The route files that HD Edition ships still have that section.
- A path line longer than 1,023 bytes is an error, because the game reads only the first 1,023 bytes of a line. Use shorter system nicknames, or add a connection that makes the route shorter.
- When a mod has its own route files, as whole files or as patches, a warning tells it. When the build makes tables, they replace the files of the mod. When the build makes no tables, the files of the mod go to the game as they are, and they can be out of date.
- The checklist names each table with the counts of its new, shorter, longer and lost routes. The counts compare the table with the route file of the game that it replaces. On HD Edition 0.7.1, the routes from Ku07 thus count as lost. The checklist also names each new system that no route of the table reaches.
- The export of the target OpenLancer makes no tables. OpenLancer Play computes its routes from the jump connections when it loads the game data.
- An FLMM package holds the tables of the target game with this mod. The connections of another mod are not in them.

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

`<mod id>-<version>-<target>.zip` has the folders `DATA` and `EXE` at its top, with the letter case of the folders of the game, and the README. The player copies the files of the list to a backup folder, then extracts the zip into the game folder. A drop-in zip replaces whole files, so it does not combine with other mods that change the same files. When the mod changes the routes, the zip holds the three route tables (see "Route tables").

When the zip holds `EXE\freelancer.ini`, the README gives each line that the mod adds, with its place. A player can add these lines to their own file, and keep the options of their installer. Two OpenLancer mods with text do not work together as drop-in zips, because both use the same text index.

### FLMM package (preview)

`<mod id>-<version>-<target>-flmm.zip` holds `<mod id>.zip.flmod` and the README. The `.zip.flmod` file is a zip with `script.xml` and the files of the mod, for the Freelancer Mod Manager (FLMM). FLMM 1.31 unpacks only a file whose name ends in `.zip.flmod`. OpenLancer did not check an exported package with FLMM yet. Check it with FLMM before you publish it.

- FLMM 1.31 reads one tag or one tag end on each line of `script.xml`. So each tag and each tag end is on its own line, and the content of an element is on the lines between them. Each attribute value is in quotes.
- Each content line of an operation is an INI line as the game file has it. The script adds no indent.
- A new section is an `append` operation at the end of the file. A new line is a `sectionappend` after the header line and the key line of its section. A changed value is a `sectionreplace`: the raw line of the game file and the new line. FLMM changes only the lines below the header of the section.
- FLMM uses the first match of an anchor. So the header line and the key line must occur once in the file as whole lines. Their first match as text must also be these lines. For example, the anchor of `ring_1` is not valid when the section of `ring_10` comes first. The same rule holds for a changed line in its section.
- A new `[BaseGood]` of new goods adds its lines to the `[BaseGood]` of the same base. The games keep the last line of a good at a base, and a line there comes before the line of the game. So a new `[BaseGood]` with a good that the game section of the base sells is an `append` at the end of the file. An example is a line that changes or removes a line of the game. A new `[Data]` line goes after the last line of its key.
- The text of the mod becomes `<key> = 0 ;GENERATESTRRES("<text>")` and `<key> = 0 ;GENERATEXMLRES("<text>")`. FLMM makes its own text DLL and writes the id of the text in the line. The game reads the text after the semicolon as a comment. So the text must be the last value of its line, and a line can hold only one text. Else the export refuses the change.
- A text with `"`, with a line end or with a character outside ASCII refuses the change, because no known FLMM rule can hold it.
- The script writes `&`, `<` and `>` as `&amp;`, `&lt;` and `&gt;`, also in a content line. The FLMM test did not show if FLMM writes the characters or the escapes into the game file. So a text with these characters, for example each infocard, is not checked in FLMM yet.
- A change that the operations cannot express puts the whole merged file into the package, with a warning. Examples are a removed line or section, a key line that does not directly follow its header, and an anchor that FLMM can find at more than one place. A whole file cannot hold the text of the mod, so such a change with text refuses the package.
- A new whole INI file of the mod with mod text refuses the package, because FLMM reads its text macros only in script operations.
- The route tables are whole files. The package copies them with no operation and no warning (see "Route tables").
- A new `[Base]` of `universe.ini` refuses the package. The game needs each `[Base]` before the `[system]` sections (see "universe.ini"), and an `append` puts the section at the end of the file. This version of the FLMM package cannot put a section before the first `[system]` yet.
- Each refusal names the drop-in zip. The drop-in zip always carries the new content.
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
- A market for a base that does not exist, a market good that does not exist, or a market good that the engine ignores (a good without equipment). A line of a hull good is valid: it sets the price of its ship at the base (see "Markets").
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
| A door (`ExitDoor`) of a room that leads to a room that its base does not have | Error | It can crash when the player uses the door. A door without `room_switch` gives no finding. The check reads each base whose `[Base]`, base file or room file the mods add or change. |
| An `[MBase]` whose nickname names no base | Error | It stops reading the bar data of the bases after the block. |
| An `[MBase]` whose first line is not its nickname line | Error | It reads the first line as the nickname. |
| A base nickname with more than 63 characters | Error | It ignores the bar data of the base. |
| A base with the nickname or the FLHash of another base | Error | It drops one of the two bases. The check reads the bases of the game of each target. So a base of the mod can collide with a base of one target game only. |
| A fixture of an `[MRoom]` that names no `[GF_NPC]` before it in its `[MBase]` | Error | It shows no character at this place. |
| A base whose market the mods add or change, when it sells a ship and has no line for each ship package | Warning | Vanilla lists each ship package at each base that sells ships. The trade-in value does not come from these lines. The check runs only for a target whose ship markets have buy-only lines. Vanilla 1.1 has them, and HD Edition 0.7.1 does not. So a mod that you make on HD Edition data can get the warning for the vanilla target. |

A mod without a retail target gets warnings for the door, the first line, the long nickname and the fixture. The base checks give them (see "The save gate").

The save gate does not run the check of the jump holes and jump gates. It gives that error through the checks of the jump connections, for each data mod (see "Jump connections and gate locks"). So the save gate shows one error for one cause. Play in the game, the export and `oltool validate --target` run the content check.

The crash checks follow the crash list of The Starport knowledge base. The retail data itself has two such cases, which the game uses only in scripted scenes. A cutscene missile has no `lootprops.ini` entries, and some capital ships of the story missions have no engine. These cases give no finding, because the checks report only what the mods add.

The content checks do not read models and textures. So they do not check collision parts with too many triangles, texture sizes, or cutscene animations that a model does not have.
