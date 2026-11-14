"""Tests of the help pages for the users of the release zip (#61). Run them with: python -B -m unittest discover -s tools

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


def games_table(text):
    """The table of a page whose first column is the game and which has the Play column, or None."""
    for header, *rows in tables(text):
        if header[0] == GAME and PLAY in header:
            return header, [dict(zip(header, row)) for row in rows]
    return None


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
        readme_header, readme_rows = supported_games()
        for page in PAGES:
            table = games_table(read(page))
            self.assertIsNotNone(table, f"{page} has no table with the columns '{GAME}' and '{PLAY}'.")
            header, rows = table
            self.assertEqual([], [column for column in header if column not in readme_header], page)
            self.assertEqual([row[GAME] for row in readme_rows], [row.get(GAME) for row in rows], page)
            for readme_row, row in zip(readme_rows, rows):
                for column in header:
                    self.assertEqual(readme_row[column], row.get(column), f"{page}: {row[GAME]}, {column}")

    def test_each_page_names_the_project_of_the_source_build_only_in_the_allowed_words(self):
        source_build = supported_games()[1][3][GAME]
        for page in PAGES:
            text = read(page)
            self.assertIn(NO_CODE, text, page)
            self.assertNotIn("decompil", text.lower(), page)
            for number, line in enumerate(text.split("\n"), 1):
                row = line.startswith("|") and line.strip("| ").split(" |")[0] == source_build
                if not row:
                    self.assertNotIn(PROJECT, line.replace(NO_CODE, ""), f"{page}:{number}")

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
            found = [(n, m.group(0)) for n, line in enumerate(read(page).split("\n"), 1) for m in pattern.finditer(line)]
            self.assertEqual([], found, page)

    def test_the_pages_have_no_semicolon_and_no_contraction(self):
        for page in PAGES:
            text = prose(read(page))
            self.assertNotIn(";", text, page)
            self.assertEqual([], CONTRACTION.findall(text), page)


if __name__ == "__main__":
    unittest.main()
