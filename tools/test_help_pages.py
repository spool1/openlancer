"""Tests of the help pages for the users of the release zip (#61).

Run them with: python -B -m unittest discover -s tools

docs/GETTING_STARTED.md and docs/TROUBLESHOOTING.md name the same supported games and the same Play support as the
README table "Supported games" (the owner decided this on 2026-11-14). They name the project of the source build only in
the words of the README row and of the README sentence. Each relative link of the pages names a file and a heading that
exist.
"""
import os
import re
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import content_scan  # noqa: E402
from test_release_check import internal_ids  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PAGES = ("docs/GETTING_STARTED.md", "docs/TROUBLESHOOTING.md")
README = "README.md"
GAME = "Game"
PLAY = "Play the mod in the game (Ctrl+F5)"
NO_CODE = "OpenLancer contains no code of the Freelancer-Decomp project."
PROJECT = "Freelancer-Decomp"
LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
FENCE = re.compile(r"^```.*?^```[ \t]*$", re.M | re.S)
CODE_SPAN = re.compile(r"`[^`\n]*`")
CONTRACTION = re.compile(r"(?i)\b(?:\w+n't|\w+'(?:re|ve|ll|m|d)|(?:it|that|there|what|here|let)'s)\b")
# The verbs that start a command in the steps of the pages.
IMPERATIVES = {"attach", "click", "close", "copy", "correct", "do", "double-click", "download", "drag", "extract",
               "fly", "get", "go", "install", "link", "make", "open", "paste", "press", "remove", "repair", "run",
               "select", "start", "type", "write"}
# The Play sections that must give the Play support of Freelancer 1.0, because their steps can start the 1.0 program.
PLAY_STEPS = (("docs/GETTING_STARTED.md", "## Play the change in your game (Ctrl+F5)"),
              ("docs/PLAY.md", "## Play in the game"))
# The pages whose steps start from the release zip. They name start.bat and the bench options of the editor only in a
# section for developers.
ZIP_PAGES = (README, "docs/MISSIONS.md", "docs/CUTSCENES.md")
DEVELOPER_ONLY = re.compile(r"start\.bat|--bench-")
HEADING = re.compile(r"(#{1,6}) +(.+)")


def read(path):
    with open(os.path.join(REPO, path), encoding="utf-8") as f:
        return f.read()


def section(text, heading):
    """The text of a Markdown section, from its heading line to the next heading of the same or a higher level. None
    when the text has no such heading."""
    level = len(heading) - len(heading.lstrip("#"))
    match = re.search(rf"^{re.escape(heading)}[ \t]*\n(.*?)(?=^#{{1,{level}}} |\Z)", text, re.M | re.S)
    return match.group(1) if match else None


def tables(text):
    """Each Markdown table of a text, as a list of rows. A row is the list of its cells. The first row is the header,
    and the rule line under it is not a row."""
    found, rows = [], []
    for line in text.split("\n") + [""]:
        if line.startswith("|"):
            cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
            if not all(re.fullmatch(r":?-+:?", cell) for cell in cells):
                rows.append(cells)
        elif rows:
            found.append(rows)
            rows = []
    return found


def supported_games(repo=REPO):
    """The README table "Supported games": its header, and one {column: cell} dictionary for each game row, in the
    order of the rows."""
    with open(os.path.join(repo, README), encoding="utf-8") as f:
        text = section(f.read(), "## Supported games")
    if text is None or not tables(text):
        raise AssertionError("README.md has no table in the section 'Supported games'.")
    header, *rows = tables(text)[0]
    return header, [dict(zip(header, row)) for row in rows]


def games_problems(text, readme_header, readme_rows):
    """The differences between the games tables of a page and the README table "Supported games". A games table has
    the game in its first column and has the Play column. The page must have one or more games tables, and each one
    must have the games of the README in their order, with the README cell in each column."""
    problems = []
    found = [table for table in tables(text) if table[0][0] == GAME and PLAY in table[0]]
    if not found:
        problems.append(f"no table with the columns '{GAME}' and '{PLAY}'")
    readme_games = [row[GAME] for row in readme_rows]
    for number, (header, *cells) in enumerate(found, 1):
        rows = [dict(zip(header, row)) for row in cells]
        problems += [f"table {number}: the README has no column '{column}'" for column in header
                     if column not in readme_header]
        games = [row.get(GAME) for row in rows]
        if games != readme_games:
            problems.append(f"table {number}: the games {games} are not the games {readme_games} of the README")
            continue
        problems += [f"table {number}: {row[GAME]}, {column}: '{row.get(column)}' is not '{readme_row[column]}'"
                     for readme_row, row in zip(readme_rows, rows) for column in header
                     if column in readme_header and row.get(column) != readme_row[column]]
    return problems


def project_problems(text, source_build):
    """The lines of a page that name the project of the source build, or a decompilation, outside the allowed words.
    The allowed words are the game cell of the README row (source_build) and the README sentence NO_CODE. A page
    without the sentence NO_CODE is also a problem."""
    problems = [] if NO_CODE in text else [f"no sentence '{NO_CODE}'"]
    for number, line in enumerate(text.split("\n"), 1):
        rest = line.replace(NO_CODE, "")
        if line.startswith("|") and line.strip("| ").split(" |")[0] == source_build:
            rest = rest.replace(source_build, "", 1)
        if "decomp" in rest.lower():
            problems.append(f"line {number}: {line}")
    return problems


def numbered_steps(text):
    """The text of each item of a numbered list, without its number."""
    return re.findall(r"^\d+\. +(.+)$", FENCE.sub("", text), re.M)


def actions(step):
    """The number of commands of a numbered step. A command is a sentence that starts with a verb of IMPERATIVES,
    also after "Or", "Then", "also" or a condition that ends with a comma, for example "If the editor crashed, attach ..."."""
    count = 0
    for sentence in re.split(r"(?<=[.!?])\s+", prose(step).replace("**", "")):
        words = re.sub(r"^(?:If|When|For|In|On|Before|After|Instead of)\b[^,]*, ", "", sentence).split()
        words = words[1:] if words[:1] in (["Or"], ["Then"], ["also"]) else words
        count += bool(words) and words[0].lower() in IMPERATIVES
    return count


def anchor(heading):
    """The anchor that GitHub gives a heading: lower case, without punctuation, and each space a hyphen."""
    return re.sub(r"[^\w\- ]", "", heading.strip().lower()).replace(" ", "-")


def anchors(text):
    """The anchors of the headings of a Markdown text. A second heading with the same anchor gets "-1", and so on."""
    seen, found = {}, set()
    for match in re.finditer(r"^#{1,6} +(.+?) *$", FENCE.sub("", text), re.M):
        base = anchor(match.group(1))
        count = seen.get(base, 0)
        seen[base] = count + 1
        found.add(base if count == 0 else f"{base}-{count}")
    return found


def link_problems(page, text=None):
    """The relative links of a page that name no file, or a heading that the file does not have.

    page: the path of the page in the repo, with forward slashes. text: the text of the page, or None to read it.
    """
    text = read(page) if text is None else text
    folder = os.path.dirname(page)
    problems = []
    for target in LINK.findall(FENCE.sub("", text)):
        if re.match(r"(?i)(?:https?|mailto):", target):
            continue
        path, _, fragment = target.partition("#")
        if not path:
            headings = anchors(text)
        else:
            name = os.path.normpath(os.path.join(folder, path))
            if not os.path.isfile(os.path.join(REPO, name)):
                problems.append(f"{target}: no file {name}")
                continue
            headings = anchors(read(name)) if fragment else set()
        if fragment and fragment not in headings:
            problems.append(f"{target}: no heading with the anchor '{fragment}'")
    return problems


def prose(text):
    """The text without code blocks and code spans."""
    return CODE_SPAN.sub("", FENCE.sub("", text))


def developer_problems(text):
    """The lines of a Markdown text that name start.bat or a bench option of the editor outside a section for
    developers. A section is for developers when its heading, or the heading of a section that holds it, contains the
    word "developers" in any letter case. A line in a code block is never a heading, but it can be a problem."""
    headings, problems, code = [], [], False
    for number, line in enumerate(text.split("\n"), 1):
        if line.startswith("```"):
            code = not code
        heading = None if code else HEADING.match(line)
        if heading:
            level = len(heading.group(1))
            headings = [(other, name) for other, name in headings if other < level] + [(level, heading.group(2))]
        if DEVELOPER_ONLY.search(line) and not any("developers" in name.lower() for _, name in headings):
            problems.append(f"line {number}: {line}")
    return problems


class HelpPageTests(unittest.TestCase):
    def test_each_page_exists_and_the_content_scan_allows_it(self):
        for page in PAGES:
            self.assertTrue(os.path.isfile(os.path.join(REPO, page)), page)
            self.assertIn(page, content_scan.MARKDOWN_FILES)

    def test_the_readme_table_has_the_four_games_and_the_play_column(self):
        """The reference of the pages and of README-FIRST.txt. The test finds a broken parse of the table."""
        header, rows = supported_games()
        self.assertEqual(GAME, header[0])
        self.assertIn(PLAY, header)
        games = [row[GAME] for row in rows]
        self.assertEqual(4, len(games), games)
        for start, game in zip(("Freelancer 1.1", "Freelancer: HD Edition 0.7.1", "Freelancer 1.0",
                                f"A source build of the {PROJECT} project"), games):
            self.assertTrue(game.startswith(start), game)
        self.assertIn("Play > Play in source build", rows[3][PLAY])
        self.assertIn(NO_CODE, read(README))

    def test_each_page_names_the_games_of_the_readme_with_their_play_support(self):
        header, rows = supported_games()
        for page in PAGES:
            self.assertEqual([], games_problems(read(page), header, rows), page)

    def test_each_games_table_of_a_page_must_be_the_same_as_the_readme(self):
        header, rows = supported_games()
        good = "\n".join(["| Game | Play the mod in the game (Ctrl+F5) |", "|---|---|"] +
                         [f"| {row[GAME]} | {row[PLAY]} |" for row in rows]) + "\n"
        self.assertEqual([], games_problems(good, header, rows))
        self.assertEqual([], games_problems(good + "\n| File | Content |\n|---|---|\n| a | b |\n", header, rows))
        second = good + "\n| Game | Play the mod in the game (Ctrl+F5) |\n|---|---|\n| Freelancer 1.0 (retail) | Yes |\n"
        self.assertEqual(1, len(games_problems(second, header, rows)), second)
        self.assertIn("table 2", games_problems(second, header, rows)[0])
        lines = good.split("\n")
        swapped = "\n".join(lines[:2] + [lines[3], lines[2]] + lines[4:])
        self.assertEqual(1, len(games_problems(swapped, header, rows)), swapped)
        changed = good.replace(rows[2][PLAY], "Yes")
        self.assertEqual(["table 1: Freelancer 1.0 (retail), Play the mod in the game (Ctrl+F5): 'Yes' is not "
                          f"'{rows[2][PLAY]}'"], games_problems(changed, header, rows))
        self.assertEqual([f"no table with the columns '{GAME}' and '{PLAY}'"], games_problems("No table.\n", header,
                                                                                               rows))

    def test_each_page_names_the_project_of_the_source_build_only_in_the_allowed_words(self):
        source_build = supported_games()[1][3][GAME]
        for page in PAGES:
            self.assertEqual([], project_problems(read(page), source_build), page)

    def test_the_project_of_the_source_build_outside_the_allowed_words_is_a_problem(self):
        source_build = supported_games()[1][3][GAME]
        allowed = f"| {source_build} | Yes |\n{NO_CODE}\n"
        self.assertEqual([], project_problems(allowed, source_build))
        for added in ("A decomp build is a play target only.", f"Build the {PROJECT} project first.",
                      "Open a decompiled file.", f"| {source_build} | Yes, a {PROJECT} build |"):
            self.assertEqual([f"line 3: {added}"], project_problems(allowed + added + "\n", source_build), added)
        self.assertEqual([f"no sentence '{NO_CODE}'"], project_problems(f"| {source_build} | Yes |\n", source_build))

    def test_the_play_steps_give_the_play_support_of_freelancer_1_0(self):
        """The Play menu lists Freelancer 1.0, so these steps can start its program. They give the README cell."""
        cell = next(row[PLAY] for row in supported_games()[1] if row[GAME].startswith("Freelancer 1.0"))
        for page, heading in PLAY_STEPS:
            text = section(read(page), heading)
            self.assertIsNotNone(text, f"{page}: {heading}")
            self.assertIn(cell.lower(), " ".join(text.lower().split()), f"{page}: {heading}")

    def test_a_step_that_stops_the_game_goes_to_the_editor_window(self):
        """Shift+F5 and the Stop button are parts of the editor. The game window does not use the keys of the
        editor."""
        stops = [step for page in PAGES for step in numbered_steps(read(page)) if re.search(r"Shift\+F5|\bStop\b", step)]
        self.assertNotEqual([], stops)
        for step in stops:
            self.assertIn("editor window", step)

    def test_a_step_that_runs_oltool_works_in_cmd_and_in_powershell(self):
        """PowerShell runs a program of the current folder only with ".\\" before its name. cmd also accepts it."""
        commands = [step for page in PAGES for step in numbered_steps(read(page))
                    if re.match(r"(?:Type|Run)\b", step) and "oltool" in step]
        self.assertNotEqual([], commands)
        for step in commands:
            self.assertRegex(step, r"`\.\\oltool ")

    def test_each_numbered_step_has_one_command(self):
        for page in PAGES:
            self.assertEqual([], [step for step in numbered_steps(read(page)) if actions(step) != 1], page)

    def test_the_commands_of_a_step_are_counted(self):
        for step, expected in (("Press Ctrl+F5.", 1),
                               ("Press Ctrl+F5. Or click the Play menu and the item of your game.", 2),
                               ("Attach `a.txt`. For a problem in Play, also attach `b.txt`.", 2),
                               ("In the editor, click **Help > Report a problem**. The editor copies a text.", 1),
                               ("If Windows shows \"Windows protected your PC\", do the steps of [it](#it).", 1),
                               ("Click **Select game folder...**. Then select the folder.", 2),
                               ("The editor shows a card.", 0)):
            self.assertEqual(expected, actions(step), step)

    def test_each_relative_link_names_a_file_and_a_heading_that_exist(self):
        for page in PAGES:
            self.assertEqual([], link_problems(page), page)

    def test_the_anchor_of_a_heading_is_the_anchor_of_github(self):
        for heading, expected in (("Play in the game (Ctrl+F5)", "play-in-the-game-ctrlf5"),
                                  ("Failed to create OpenGL context", "failed-to-create-opengl-context"),
                                  ("Save, export and Play", "save-export-and-play"), ("`oltool game`", "oltool-game"),
                                  ("The play_copy  folder", "the-play_copy--folder")):
            self.assertEqual(expected, anchor(heading))
        self.assertEqual({"notes", "notes-1", "keys"}, anchors("# Notes\n## Notes\n```\n# Code\n```\n### Keys\n"))

    def test_a_link_to_a_missing_file_or_heading_is_a_problem(self):
        """The links of this page go to the real files of the repo."""
        text = ("[a](PLAY.md#play-in-the-game) [b](../README.md) [c](#here) [d](https://example.com/NO.md)\n"
                "[e](NO.md) [f](PLAY.md#no-such-heading) [g](#there)\n```\n[h](IN_CODE.md)\n```\n## Here\n")
        self.assertEqual(["NO.md: no file " + os.path.normpath("docs/NO.md"),
                          "PLAY.md#no-such-heading: no heading with the anchor 'no-such-heading'",
                          "#there: no heading with the anchor 'there'"], link_problems("docs/NEW.md", text))

    def test_the_pages_name_no_internal_id(self):
        pattern = internal_ids(REPO)
        for page in PAGES:
            lines = enumerate(read(page).split("\n"), 1)
            found = [(n, m.group(0)) for n, line in lines for m in pattern.finditer(line)]
            self.assertEqual([], found, page)

    def test_the_pages_have_no_semicolon_and_no_contraction(self):
        for page in PAGES:
            text = prose(read(page))
            self.assertNotIn(";", text, page)
            self.assertEqual([], CONTRACTION.findall(text), page)

    def test_the_readme_links_both_pages_for_players_and_in_the_help_menu(self):
        readme = read(README)
        for heading in ("### Players", "## Help menu"):
            text = section(readme, heading)
            self.assertIsNotNone(text, heading)
            for page in PAGES:
                self.assertIn(f"]({page})", text, heading)


class ZipPageTests(unittest.TestCase):
    """The steps of the README, MISSIONS.md and CUTSCENES.md start from the release zip. The steps for developers are in
    their own sections."""

    def test_the_zip_pages_name_the_developer_steps_only_in_sections_for_developers(self):
        for page in ZIP_PAGES:
            self.assertEqual([], developer_problems(read(page)), page)

    def test_a_developer_step_outside_a_section_for_developers_is_a_problem(self):
        text = "\n".join([
            "# Page",
            "Run `start.bat`.",
            "## Set up",
            "### Developers",
            "Run `start.bat`.",
            "## Make a mod",
            "Use `--bench-mission`.",
            "### For developers: the command line",
            "Run `start.bat`.",
            "#### Build",
            "Run `start.bat` again.",
            "## Play from start.bat",
            "```",
            "# start.bat is not a heading here",
            "```",
            "## For Developers",
            "Use `--bench-cutscene`.",
        ])
        self.assertEqual(["line 2: Run `start.bat`.", "line 7: Use `--bench-mission`.", "line 12: ## Play from start.bat",
                          "line 14: # start.bat is not a heading here"], developer_problems(text))

    def test_the_first_step_of_a_page_starts_the_editor_of_the_release_zip(self):
        for page, heading in (("docs/MISSIONS.md", "## Open a mission"),
                              ("docs/CUTSCENES.md", "## Browse and preview the retail cutscenes")):
            first = numbered_steps(section(read(page), heading))[0]
            self.assertIn("`OpenLancer.Editor.exe`", first, page)
            self.assertIn("release zip", first, page)
            self.assertIn("](GETTING_STARTED.md#", first, page)

    def test_make_a_mod_starts_from_the_release_zip(self):
        text = section(read(README), "## Make a mod")
        players = text.split("\n### ")[0]
        self.assertIn("](docs/GETTING_STARTED.md)", players)
        for part in ("release zip", "`my-mod`", "Ctrl+S", "F5", "Ctrl+F5", "**Mod > Export mod...**"):
            self.assertIn(part, players)
        self.assertNotIn("oltool validate", players)
        self.assertIn("oltool deploy", section(text, "### For developers: the command line"))

    def test_the_links_of_the_zip_pages_name_files_and_headings_that_exist(self):
        for page in ZIP_PAGES:
            self.assertEqual([], link_problems(page), page)


if __name__ == "__main__":
    unittest.main()
