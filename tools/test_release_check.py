"""Tests of tools/release_check.py. Run them with: python -B -m unittest discover -s tools"""
import contextlib
import hashlib
import io
import json
import os
import re
import struct
import sys
import tempfile
import unittest
import zipfile
from unittest import mock
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import release_check as rc  # noqa: E402


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data.encode("utf-8") if isinstance(data, str) else data)


def fake_pe(imports=(), pdb=None):
    """A small PE32+ file with one section: an import table and, with pdb, an embedded portable PDB."""
    section_rva, section_raw, section_size = 0x1000, 0x200, 0x800
    body = bytearray(section_size)
    import_rva = debug_rva = 0
    if imports:
        import_rva = section_rva
        names_at = 0x100
        for i, name in enumerate(imports):
            struct.pack_into("<I", body, 20 * i + 12, section_rva + names_at)
            body[names_at:names_at + len(name) + 1] = name.encode("ascii") + b"\0"
            names_at += len(name) + 1
    if pdb is not None:
        blob = b"MPDB" + struct.pack("<I", len(pdb)) + zlib.compress(pdb)[2:-4]
        blob_at = 0x400
        body[blob_at:blob_at + len(blob)] = blob
        debug_rva = section_rva + 0x300
        struct.pack_into("<IIHHIIII", body, 0x300, 0, 0, 0x100, 0x100, rc.EMBEDDED_PDB, len(blob),
                         section_rva + blob_at, section_raw + blob_at)
    data = bytearray(section_raw + section_size)
    data[0:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x40)
    data[0x40:0x44] = b"PE\0\0"
    coff = 0x44
    struct.pack_into("<HHIIIHH", data, coff, 0x8664, 1, 0, 0, 0, 240, 0x22)
    optional = coff + 20
    struct.pack_into("<H", data, optional, 0x20B)
    directories = optional + 112
    struct.pack_into("<II", data, directories + 8, import_rva, 40 if imports else 0)
    struct.pack_into("<II", data, directories + 48, debug_rva, 28 if pdb is not None else 0)
    table = optional + 240
    data[table:table + 8] = b".rdata\0\0"
    struct.pack_into("<IIII", data, table + 8, section_size, section_rva, section_size, section_raw)
    data[section_raw:] = body
    return bytes(data)


def fake_version_resource(file_version, product):
    parts = [int(p) for p in file_version.split(".")]
    fixed = struct.pack("<IIIIII", 0xFEEF04BD, 0x10000, parts[0] << 16 | parts[1], parts[2] << 16 | parts[3], 0, 0)
    key = "ProductVersion\0".encode("utf-16-le")
    return b"\0" * 64 + fixed + b"\0" * 16 + key + b"\0\0" + (product + "\0").encode("utf-16-le") + b"\0" * 8


class HeaderAndTextTests(unittest.TestCase):
    def test_a_block_comment_loses_its_marks(self):
        text = "\ufeff/*\n * Copyright (c) 2021 Someone\n *\n * Permission is granted.\n */\nusing System;\n"
        self.assertEqual("Copyright (c) 2021 Someone\n\nPermission is granted.\n", rc.header_comment(text))

    def test_line_comments_after_a_region_and_with_an_empty_line(self):
        text = "#region License\n// MIT License\n\n// Copyright (c) 2020 A\n//\n// Text.\n#endregion\nclass X {}\n"
        self.assertEqual("MIT License\n\nCopyright (c) 2020 A\n\nText.\n", rc.header_comment(text))

    def test_a_file_without_a_comment_gives_nothing(self):
        self.assertEqual("", rc.header_comment("using System;\n// late comment\n"))

    def test_the_version_title(self):
        self.assertEqual("v0.1.0 (preview)", rc.version_title("0.1.0-preview"))
        self.assertEqual("v1.0.0", rc.version_title("1.0.0"))

    def test_marks_and_placeholders(self):
        self.assertEqual(2, len(rc.marks_problems("x", "a [if built]\nb\nc [W4]\nd [Wx]\n")))
        text, problems = rc.fill("x", "@VERSION@ @OTHER@", {"@VERSION@": "1.0"})
        self.assertEqual("1.0 @OTHER@", text)
        self.assertEqual(["x: unknown placeholder @OTHER@"], problems)


class VersionTests(unittest.TestCase):
    """The names, the titles and the version checks of a beta, a release candidate, the final version and the
    development version of master (#57)."""

    def name_command(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = rc.main(["name", *args])
        return code, out.getvalue()

    def test_a_beta_and_a_release_candidate(self):
        for version, suffix, other in (("1.0.0-beta.1", "beta.1", "1.0.0-rc.1"), ("1.0.0-rc.1", "rc.1", "1.0.0")):
            with self.subTest(version):
                self.assertEqual(f"OpenLancer-v{version}-win-x64.zip", rc.zip_name(version, False, "abc1234"))
                self.assertEqual(f"v1.0.0 ({suffix})", rc.version_title(version))
                self.assertFalse(rc.is_development(version))
                self.assertEqual([], rc.name_problems(version, dry_run=False))
                with tempfile.TemporaryDirectory() as stage:
                    write(os.path.join(stage, "OpenLancer.Editor.exe"),
                          fake_version_resource("1.0.0.0", version + "+abc1234"))
                    self.assertEqual([], rc.check_version(stage, version))
                    self.assertEqual([f"h: OpenLancer.Editor.exe has the product version {version}+abc1234, "
                                      f"not {other}+<commit>"], rc.check_version(stage, other))

    def test_the_final_version(self):
        self.assertEqual("OpenLancer-v1.0.0-win-x64.zip", rc.zip_name("1.0.0", False, "abc1234"))
        self.assertEqual("v1.0.0", rc.version_title("1.0.0"))
        self.assertFalse(rc.is_development("1.0.0"))

    def test_a_development_version_builds_only_in_a_dry_run(self):
        self.assertEqual("OpenLancer-1.0.0-dev-dryrun-abc1234-win-x64.zip", rc.zip_name("1.0.0-dev", True, "abc1234"))
        for version in ("1.0.0-dev", "1.1.0-dev.2"):
            self.assertTrue(rc.is_development(version), version)
            self.assertEqual([], rc.name_problems(version, dry_run=True))
            self.assertEqual(1, len(rc.name_problems(version, dry_run=False)))
        for version in ("1.0.0", "1.0.0-beta.1", "1.0.0-rc.1", "0.1.0-preview", "1.0.0-beta.1-dev"):
            self.assertFalse(rc.is_development(version), version)

    def test_the_name_command_prints_only_the_name(self):
        self.assertEqual((0, "OpenLancer-1.0.0-dev-dryrun-abc1234-win-x64.zip\n"),
                         self.name_command("--version", "1.0.0-dev", "--sha7", "abc1234", "--dry-run"))
        self.assertEqual((0, "OpenLancer-v1.0.0-beta.1-win-x64.zip\n"),
                         self.name_command("--version", "1.0.0-beta.1", "--sha7", "abc1234"))

    def test_the_name_command_refuses_a_release_of_a_development_version(self):
        code, text = self.name_command("--version", "1.0.0-dev", "--sha7", "abc1234")
        self.assertEqual(1, code)
        self.assertIn("the version 1.0.0-dev is a development version. Only a dry run can build it.", text)
        self.assertNotIn(".zip", text)


class WorkRootTests(unittest.TestCase):
    """The preflight refuses a work root that check c would refuse at the end of the run (#57). The natives keep the
    source paths of the build, and these paths are below the work root."""

    REPO = "D:\\src\\openlancer"

    def setUp(self):
        user = {"USERNAME": "Someone", "USERPROFILE": "X:\\Users\\Someone", "HOME": "X:\\Users\\Someone"}
        environment = mock.patch.dict(os.environ, user)
        environment.start()
        self.addCleanup(environment.stop)

    def work_root_command(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = rc.main(["work-root", *args])
        return code, out.getvalue()

    def test_the_forbidden_paths_are_the_repo_and_its_parent_folder(self):
        self.assertEqual([self.REPO, "D:\\src"], rc.source_paths(self.REPO))
        self.assertEqual([self.REPO, "D:\\src"], rc.source_paths(self.REPO + "\\"))

    def test_a_drive_root_is_not_a_forbidden_path(self):
        """A drive root names no folder. As a forbidden path, it refuses each native that a build on the drive makes."""
        self.assertEqual(["X:\\openlancer"], rc.source_paths("X:\\openlancer"))
        self.assertEqual(["/openlancer"], rc.source_paths("/openlancer"))
        self.assertEqual([], rc.work_root_problems("X:\\olrel", "X:\\openlancer"))
        native = b"x X:\\olrel\\src\\external\\Librelancer\\extern\\imgui\\imgui.h"
        patterns = rc.search_patterns(rc.forbidden_values(rc.source_paths("X:\\openlancer")))
        self.assertEqual([], rc.find_strings(native, patterns))

    def test_the_default_and_a_folder_on_another_drive_pass(self):
        for root in ("D:\\olrel", "E:\\rel", "D:\\other\\olrel"):
            self.assertEqual([], rc.work_root_problems(root, self.REPO), root)

    def test_a_work_root_in_the_repo_fails(self):
        for root in (self.REPO, "d:\\SRC\\OpenLancer\\out", self.REPO + "\\"):
            problems = rc.work_root_problems(root, self.REPO)
            self.assertEqual(1, len(problems), root)
            self.assertIn("is in the repo", problems[0])

    def test_a_work_root_next_to_the_repo_fails(self):
        """The old preflight let a folder next to the repo pass, and check c refused its natives at the end of the run.
        The preflight ignores case, because the build can use the case of the folders on the disk."""
        patterns = rc.search_patterns(rc.forbidden_values(rc.source_paths(self.REPO)))
        for root in ("D:\\src\\openlancer-rel", "D:\\src\\olrel", "d:\\Src\\rel"):
            problems = rc.work_root_problems(root, self.REPO)
            self.assertTrue(any("holds the path 'D:\\src'" in p for p in problems), (root, problems))
            native = f"x {root.upper()}\\SRC\\EXTERN\\IMGUI\\IMGUI.H".encode("utf-8")
            self.assertTrue(rc.find_strings(native, patterns), root)

    def test_the_user_name_and_the_word_fail(self):
        for root, label in (("E:\\someone\\rel", "user name"), ("E:\\My Decompiled\\rel", "word"),
                            ("E:\\decompiledx\\rel", "word")):
            problems = rc.work_root_problems(root, self.REPO)
            self.assertTrue(any(f"holds the {label}" in p for p in problems), (root, problems))
        self.assertEqual([], rc.work_root_problems("E:\\compiled\\rel", self.REPO))

    def test_the_command_prints_only_the_forbidden_paths(self):
        self.assertEqual((0, "D:\\src\\openlancer\nD:\\src\n"), self.work_root_command("D:\\olrel", "--repo", self.REPO))
        self.assertEqual((0, "X:\\openlancer\n"), self.work_root_command("X:\\olrel", "--repo", "X:\\openlancer"))

    def test_the_command_refuses_a_work_root_next_to_the_repo(self):
        code, text = self.work_root_command("D:\\src\\openlancer-rel", "--repo", self.REPO)
        self.assertEqual(1, code)
        self.assertIn("the work root D:\\src\\openlancer-rel holds the path 'D:\\src'.", text)


class NameAndHashTests(unittest.TestCase):
    def test_release_file_types_pass(self):
        for name in ("OpenLancer.Editor.exe", "coreclr.dll", "LibreLancer.Base.dll.config", "oltool.deps.json",
                     "oltool.runtimeconfig.json", "README-FIRST.txt", "NOTICE.txt", "LICENSE.txt", "x64/SDL3.dll",
                     "licenses/sdl3.txt"):
            self.assertTrue(rc.allowed_name(name), name)

    def test_game_data_and_other_files_fail(self):
        for name in ("freelancer.ini", "fonts/agencyfb.ttf", "DATA/ships.ini", "x64/sub/a.dll", "licenses/a.md",
                     "readme.txt", "save.olsave", "model.cmp", "x64/notes.txt", "editor.json", "a.pdb"):
            self.assertFalse(rc.allowed_name(name), name)
        self.assertEqual(["a: file type not allowed in a release: a.ini"], rc.check_names(["a.dll", "a.ini"]))

    def test_a_retail_file_fails_under_any_name_and_line_end(self):
        hashes = {hashlib.sha256(b"[Ship]\nnickname = x\n").hexdigest()}
        self.assertEqual(["b: identical to a retail file: renamed.dll"],
                         rc.check_retail([("renamed.dll", b"[Ship]\r\nnickname = x\r\n"), ("ok.dll", b"other")],
                                         hashes))


class StringTests(unittest.TestCase):
    def patterns(self, *values):
        return rc.search_patterns(list(values))

    def test_a_path_is_found_in_utf16_and_with_forward_slashes(self):
        patterns = self.patterns(("path", "D:\\Work\\My Repo"))
        self.assertTrue(rc.find_strings("x d:\\work\\my repo y".encode("utf-16-le"), patterns))
        self.assertTrue(rc.find_strings(b"cmake D:/Work/My Repo/src/a.c", patterns))
        self.assertFalse(rc.find_strings(b"D:\\Work\\Other", patterns))

    def test_the_word_matches_only_a_whole_word(self):
        patterns = self.patterns(("word", "decompiled"))
        self.assertFalse(rc.find_strings(b"get_IsDynamicCodeCompiled", patterns))
        self.assertFalse(rc.find_strings("IsDynamicCodeCompiled".encode("utf-16-le"), patterns))
        self.assertTrue(rc.find_strings(b"D:\\Freelancer Decompiled\\x", patterns))
        self.assertTrue(rc.find_strings("/decompiled/".encode("utf-16-le"), patterns))

    def test_a_short_text_matches_only_its_exact_lower_and_upper_case(self):
        patterns = self.patterns(("user name", "Abc"))
        self.assertTrue(rc.find_strings(b"C:\\Users\\Abc\\x", patterns))
        self.assertTrue(rc.find_strings(b"abc", patterns))
        self.assertFalse(rc.find_strings(b"aBc", patterns))

    def test_the_check_searches_the_embedded_pdb_and_uses_the_allow_list(self):
        pe = fake_pe(pdb=b"BSJB document Secret Folder name")
        patterns = self.patterns(("path", "Secret Folder"))
        self.assertEqual(["c: a.dll (embedded PDB) holds the path 'Secret Folder'"],
                         rc.check_strings([("a.dll", pe)], patterns, rc.Allow()))
        allow = rc.Allow([("c", "a.dll", "path")])
        self.assertEqual([], rc.check_strings([("a.dll", pe)], patterns, allow))
        self.assertEqual([], allow.unused())


class PeTests(unittest.TestCase):
    def test_imports_of_the_visual_cpp_runtime_fail(self):
        pe = fake_pe(imports=("KERNEL32.dll", "VCRUNTIME140.dll", "msvcp140_1.dll"))
        self.assertEqual(["KERNEL32.dll", "VCRUNTIME140.dll", "msvcp140_1.dll"], rc.PE(pe).imports())
        self.assertEqual(["e: x64/a.dll imports VCRUNTIME140.dll", "e: x64/a.dll imports msvcp140_1.dll"],
                         rc.check_imports([("x64/a.dll", pe), ("b.txt", b"text")]))
        self.assertEqual([], rc.check_imports([("x64/b.dll", fake_pe(imports=("KERNEL32.dll", "msvcrt.dll")))]))

    def test_the_embedded_pdb_is_decompressed(self):
        self.assertEqual([b"BSJB pdb text"], rc.PE(fake_pe(pdb=b"BSJB pdb text")).embedded_pdbs())
        self.assertEqual([], rc.PE(fake_pe()).embedded_pdbs())

    def test_the_version_resource(self):
        data = fake_version_resource("0.1.0.0", "0.1.0-preview+abc1234")
        self.assertEqual(("0.1.0.0", "0.1.0-preview+abc1234"), rc.version_info(data))


class MergeTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.root = self.folder.name

    def tearDown(self):
        self.folder.cleanup()

    def publish(self, name, files):
        folder = os.path.join(self.root, name)
        for path, data in files.items():
            write(os.path.join(folder, path), data)
        return folder

    def program_files(self, program, tag):
        return {program + suffix: tag for suffix in rc.PROGRAM_SUFFIXES}

    def test_program_files_come_from_their_publish_and_the_rest_must_be_equal(self):
        play = self.publish("play", {**self.program_files("OpenLancer.Play", "play"), "shared.dll": "same",
                                     "x64/SDL3.dll": "sdl", "createdump.exe": "x", "x86/old.dll": "x"})
        editor = self.publish("editor", {**self.program_files("OpenLancer.Editor", "editor"),
                                         **self.program_files("OpenLancer.Play", "copy in the editor"),
                                         "shared.dll": "same", "x64/SDL3.dll": "sdl", "a.pdb": "x",
                                         "BepuPhysics.xml": "x"})
        stage = os.path.join(self.root, "stage", "OpenLancer-v1")
        self.assertEqual([], rc.merge(stage, [("OpenLancer.Play", play), ("OpenLancer.Editor", editor)]))
        expected = [*self.program_files("OpenLancer.Play", ""), *self.program_files("OpenLancer.Editor", ""),
                    "shared.dll", "x64/SDL3.dll"]
        self.assertEqual(sorted(expected), rc.files_below(stage))
        with open(os.path.join(stage, "OpenLancer.Play.dll")) as f:
            self.assertEqual("play", f.read())

    def test_a_different_shared_file_stops_the_merge(self):
        a = self.publish("a", {**self.program_files("oltool", "a"), "shared.dll": "one"})
        b = self.publish("b", {**self.program_files("OpenLancer.Play", "b"), "shared.dll": "two"})
        problems = rc.merge(os.path.join(self.root, "stage"), [("oltool", a), ("OpenLancer.Play", b)])
        self.assertEqual(1, len(problems))
        self.assertIn("different bytes in two publishes: shared.dll", problems[0])
        self.assertFalse(os.path.exists(os.path.join(self.root, "stage")))

    def test_a_missing_program_file_stops_the_merge(self):
        a = self.publish("a", {"oltool.exe": "x"})
        self.assertIn("no publish has oltool.dll", rc.merge(os.path.join(self.root, "stage"), [("oltool", a)]))


class RepoFixture(unittest.TestCase):
    """A fake clone with the release data, a retail manifest, a project and a stage folder with a deps.json file."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.repo = os.path.join(self.folder.name, "repo")
        self.stage = os.path.join(self.folder.name, "stage", "OpenLancer-v1.0.0-test")
        os.makedirs(self.stage)
        write(os.path.join(self.repo, "LICENSE"), "MIT License\n")
        write(os.path.join(self.repo, "NOTICE"), "OpenLancer\nNo game files.\n")
        write(os.path.join(self.repo, "data-manifest", "vanilla-1.0.tsv"),
              f"exe/freelancer.exe\t9\t{hashlib.sha256(b'retail!!!').hexdigest()}\n")
        write(os.path.join(self.repo, "release", "README-FIRST.txt"),
              "OpenLancer @VERSION_TITLE@\n@TITLE_RULE@\nFolder OpenLancer-v@VERSION@\nA new feature. [if built]\n")
        write(os.path.join(self.repo, "release", "notes", "v1.0.0-test.md"), "Commit @COMMIT@, fork @FORK_COMMIT@.\n")
        write(os.path.join(self.repo, "lib", "a.c"), "/* Copyright (c) 2020 Alice\n * Free. */\nint a;\n")
        write(os.path.join(self.repo, "src", "App", "App.csproj"),
              '<Project><PropertyGroup><AssemblyName>app</AssemblyName></PropertyGroup>'
              '<ItemGroup><EmbeddedResource Include="data\\*.json" /></ItemGroup></Project>')
        write(os.path.join(self.repo, "src", "App", "data", "rules.json"), "{}")
        write(os.path.join(self.repo, "src", "App", "Program.cs"), "// Copyright (c) 2026 App authors\nclass P {}\n")
        self.data = {
            "natives": ["one.dll"], "prebuilt_natives": [],
            "openal_source": {"file": "x.tar.gz", "url": "https://example.invalid/x", "sha256": None},
            "native_sources": ["lib"],
            "components": [
                {"id": "app", "name": "App", "version": "1", "license": "MIT", "files": ["app.dll", "app.exe"],
                 "texts": ["repo:LICENSE"], "holders": ["App authors"]},
                {"id": "alice", "name": "Alice lib", "version": "2", "license": "MIT", "files": ["x64/one.dll"],
                 "copyright": ["Copyright (c) 2020 Alice"], "texts": ["header:lib/a.c"], "holders": ["Alice"]},
                {"id": "pkg", "name": "A package", "version": "3", "license": "MIT", "packages": ["Some.Package"],
                 "texts": [], "note": "No text is necessary."},
            ],
        }
        self.write_data()
        write(os.path.join(self.stage, "app.deps.json"), json.dumps({
            "targets": {"x": {"app/1.0.0": {"runtime": {"app.dll": {}}},
                              "Some.Package/3.0.0": {"runtime": {"lib/net8.0/Some.Package.dll": {}}}}},
            "libraries": {"app/1.0.0": {"type": "project"}, "Some.Package/3.0.0": {"type": "package"}}}))
        for name in ("app.dll", "app.exe", "Some.Package.dll", "x64/one.dll"):
            write(os.path.join(self.stage, name), "binary " + name)

    def tearDown(self):
        self.folder.cleanup()

    def write_data(self):
        write(os.path.join(self.repo, "release", "third-party.json"), json.dumps(self.data))

    def docs(self, dry_run=True):
        return rc.docs(self.stage, self.repo, "1.0.0-test", "c0ffee1", "f00d123",
                       os.path.join(self.folder.name, "notes.md"), dry_run)


class DocsTests(RepoFixture):
    def test_a_dry_run_writes_the_documents_with_crlf(self):
        self.assertEqual([], self.docs())
        with open(os.path.join(self.stage, "README-FIRST.txt"), "rb") as f:
            readme = f.read().decode("utf-8")
        self.assertTrue(readme.startswith("OpenLancer v1.0.0 (test)\r\n========================\r\n"))
        self.assertIn("Folder OpenLancer-v1.0.0-test\r\n", readme)
        with open(os.path.join(self.stage, "licenses", "alice.txt"), encoding="utf-8") as f:
            alice = f.read()
        self.assertIn("Copyright (c) 2020 Alice\n\n" + rc.RULE + "\n\nCopyright (c) 2020 Alice\nFree.\n", alice)
        self.assertTrue(os.path.isfile(os.path.join(self.stage, "licenses", "pkg.txt")))
        with open(os.path.join(self.stage, "NOTICE.txt"), encoding="utf-8") as f:
            self.assertIn("License: MIT. Text: licenses/alice.txt", f.read())
        with open(os.path.join(self.folder.name, "notes.md"), encoding="utf-8") as f:
            self.assertEqual("Commit c0ffee1, fork f00d123.\n", f.read())

    def test_a_release_refuses_marks_statuses_and_a_missing_archive_hash(self):
        self.data["components"][2]["status"] = "pending"
        self.write_data()
        problems = self.docs(dry_run=False)
        self.assertEqual(3, len(problems), problems)
        self.assertIn("[if built]", problems[0])
        self.assertIn("component pkg has the status 'pending'", problems[1])
        self.assertIn("openal_source has no sha256", problems[2])
        self.assertFalse(os.path.exists(os.path.join(self.stage, "README-FIRST.txt")))

    def remove_notes(self):
        os.remove(os.path.join(self.repo, "release", "notes", "v1.0.0-test.md"))

    def test_a_dry_run_without_notes_writes_the_documents_and_no_notes_file(self):
        self.remove_notes()
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual([], self.docs())
        self.assertIn("note: no release notes release/notes/v1.0.0-test.md. The dry run writes no notes file.",
                      out.getvalue())
        self.assertTrue(os.path.isfile(os.path.join(self.stage, "README-FIRST.txt")))
        self.assertFalse(os.path.exists(os.path.join(self.folder.name, "notes.md")))

    def test_a_release_without_notes_fails(self):
        self.remove_notes()
        problems = self.docs(dry_run=False)
        self.assertIn("no release notes: release/notes/v1.0.0-test.md", problems)
        self.assertFalse(os.path.exists(os.path.join(self.folder.name, "notes.md")))
        self.assertFalse(os.path.exists(os.path.join(self.stage, "README-FIRST.txt")))

    def test_a_missing_text_source_is_a_problem(self):
        self.data["components"][0]["texts"] = ["repo:NOPE", "header:src/App/data/rules.json", "nuget:Other/LICENSE",
                                               "web:x"]
        self.write_data()
        problems = self.docs()
        self.assertEqual(1, len(problems))
        self.assertIn("component app: repo:NOPE: the file does not exist", problems[0])


class RuntimePackTests(unittest.TestCase):
    def test_runtimepack_is_the_dotnet_pack_and_nuget_names_another_pack(self):
        with tempfile.TemporaryDirectory() as folder:
            write(os.path.join(folder, "microsoft.netcore.app.runtime.win-x64", "10.0.1", "LICENSE.TXT"), "core")
            write(os.path.join(folder, "microsoft.aspnetcore.app.runtime.win-x64", "10.0.2", "LICENSE.TXT"), "asp")
            versions = {"runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64": "10.0.2",
                        "runtimepack.Microsoft.NETCore.App.Runtime.win-x64": "10.0.1"}
            old = os.environ.get("NUGET_PACKAGES")
            os.environ["NUGET_PACKAGES"] = folder
            try:
                self.assertEqual("core", rc.resolve_text("runtimepack:LICENSE.TXT", folder, versions))
                self.assertEqual("asp", rc.resolve_text("nuget:Microsoft.AspNetCore.App.Runtime.win-x64/LICENSE.TXT",
                                                        folder, versions))
            finally:
                if old is None:
                    del os.environ["NUGET_PACKAGES"]
                else:
                    os.environ["NUGET_PACKAGES"] = old


class StageCheckTests(RepoFixture):
    def test_the_notice_check_finds_files_and_packages_without_a_component(self):
        self.docs()
        names = rc.files_below(self.stage)
        self.assertEqual([], rc.check_notices(self.stage, names, self.data))
        write(os.path.join(self.stage, "stray.dll"), "x")
        self.data["components"][2]["packages"] = []
        problems = rc.check_notices(self.stage, rc.files_below(self.stage), self.data)
        self.assertIn("f: no component in release/third-party.json for stray.dll", problems)
        self.assertIn("f: no component in release/third-party.json for the package Some.Package", problems)

    def test_the_native_check_wants_exactly_the_natives(self):
        self.assertEqual([], rc.check_natives(self.stage, self.repo, self.data))
        write(os.path.join(self.stage, "x64", "libmp3lame.dll"), "lame")
        self.assertEqual(["d: x64/libmp3lame.dll is not a native of the release"],
                         rc.check_natives(self.stage, self.repo, self.data))

    def test_a_prebuilt_native_must_be_the_file_of_the_engine(self):
        self.data["prebuilt_natives"] = ["one.dll"]
        write(os.path.join(self.repo, "external", "Librelancer", "deps", "x64", "one.dll"), "other bytes")
        self.assertEqual(["d: x64/one.dll is not the file of external/Librelancer/deps/x64"],
                         rc.check_natives(self.stage, self.repo, self.data))

    def test_an_embedded_resource_with_a_retail_hash_fails(self):
        hashes = rc.manifest_hashes(self.repo)
        self.assertEqual([], rc.check_embedded(self.stage, self.repo, hashes))
        write(os.path.join(self.repo, "src", "App", "data", "copy.json"), "retail!!!")
        self.assertEqual(1, len(rc.check_embedded(self.stage, self.repo, hashes)))

    def test_the_version_check(self):
        write(os.path.join(self.stage, "OpenLancer.Editor.exe"), fake_version_resource("1.0.0.0", "1.0.0-test+abc"))
        self.assertEqual([], rc.check_version(self.stage, "1.0.0-test"))
        self.assertEqual(2, len(rc.check_version(self.stage, "2.0.0")))

    def test_each_copyright_holder_needs_a_component(self):
        allow = rc.Allow()
        self.assertEqual([], rc.check_source_notices(self.stage, self.repo, self.data, allow))
        write(os.path.join(self.repo, "lib", "new.c"), "/* Copyright 2001-2005 by\n * Bob Builder. */\n")
        write(os.path.join(self.repo, "lib", "tests", "t.c"), "/* Copyright (c) 2001 Test Person */\n")
        problems = rc.check_source_notices(self.stage, self.repo, self.data, allow)
        self.assertEqual(1, len(problems), problems)
        self.assertIn("lib/new.c:1: no component names this holder: /* Copyright 2001-2005 by * Bob Builder. */",
                      problems[0])
        allow = rc.Allow([("i", "lib/new.c", "Bob Builder")])
        self.assertEqual([], rc.check_source_notices(self.stage, self.repo, self.data, allow))

    def test_the_stage_check_stops_at_the_first_failing_check(self):
        self.docs()
        write(os.path.join(self.stage, "OpenLancer.Editor.exe"), fake_version_resource("1.0.0.0", "1.0.0-test+abc"))
        self.data["components"][0]["files"].append("OpenLancer.Editor.exe")
        self.write_data()
        self.assertEqual([], rc.check_stage(self.stage, self.repo, "1.0.0-test"))
        write(os.path.join(self.stage, "game.ini"), "retail!!!")
        self.assertEqual(["a: file type not allowed in a release: game.ini"],
                         rc.check_stage(self.stage, self.repo, "1.0.0-test"))
        os.remove(os.path.join(self.stage, "game.ini"))
        write(os.path.join(self.stage, "game.dll"), "retail!!!")
        self.assertEqual(["b: identical to a retail file: game.dll"],
                         rc.check_stage(self.stage, self.repo, "1.0.0-test"))


class ZipTests(RepoFixture):
    def test_the_zip_is_the_same_for_the_same_stage_and_has_one_top_folder(self):
        self.docs()
        first, second = (os.path.join(self.folder.name, n) for n in ("a.zip", "b.zip"))
        rc.write_zip(self.stage, first, 1760000000)
        rc.write_zip(self.stage, second, 1760000000)
        with open(first, "rb") as a, open(second, "rb") as b:
            self.assertEqual(a.read(), b.read())
        with zipfile.ZipFile(first) as z:
            names = z.namelist()
            self.assertEqual(sorted(names), names)
            self.assertTrue(all(n.startswith("OpenLancer-v1.0.0-test/") for n in names))
            self.assertEqual(len({i.date_time for i in z.infolist()}), 1)
        self.assertEqual([], rc.check_zip(first, self.repo))

    def test_the_zip_check_finds_a_retail_file(self):
        bad = os.path.join(self.folder.name, "bad.zip")
        with zipfile.ZipFile(bad, "w") as z:
            z.writestr("OpenLancer-v1/app.dll", b"retail!!!")
        self.assertEqual(["b: identical to a retail file: app.dll"], rc.check_zip(bad, self.repo))


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def internal_ids(repo):
    """A pattern of the internal IDs of the project. It finds the word "milestone" and a decision ID such as D81 or
    R1-D8. It also finds an ID of the first column of ROADMAP.md, with or without a part suffix. A folder of a path is
    not an ID."""
    ids = set()
    with open(os.path.join(repo, "ROADMAP.md"), encoding="utf-8") as f:
        for line in f:
            cell = line.split("|")[1].strip() if line.startswith("|") else ""
            if re.match(r"[A-Za-z]+\d", cell):
                ids.add(re.sub(r"-\d+$", "", cell))
    roadmap = "|".join(sorted(map(re.escape, ids), key=len, reverse=True))
    return re.compile(rf"(?i:\bmilestone\b)|(?<![\w\\/])(?:{roadmap})(?:-\d+)?(?!\w)|\b(?:R\d+-)?D\d+\b")


class InternalIdTests(unittest.TestCase):
    """The texts for players and new contributors give plain reasons, not the internal IDs of the project (#56)."""

    def setUp(self):
        self.pattern = internal_ids(REPO)

    def test_the_pattern_finds_the_internal_ids(self):
        for text, found in (("(milestone MCP1)", "milestone"), ("MCP1-4", "MCP1-4"), ("R1-D8", "R1"),
                            ("decision D81", "D81"), ("the M3b part", "M3b")):
            self.assertEqual(found, self.pattern.search(text).group(0), text)
        for text in ("CC0", "SDL3", "OL-0037", "the milestones", r"DATA\MISSIONS\M11\m11.ini", "M02"):
            self.assertIsNone(self.pattern.search(text), text)

    def test_the_component_notes_name_no_internal_id(self):
        """Each note goes into licenses/<id>.txt of the release zip."""
        with open(os.path.join(REPO, "release", "third-party.json"), encoding="utf-8") as f:
            data = json.load(f)
        found = [(c["id"], m.group(0)) for c in data["components"] for m in self.pattern.finditer(c.get("note", ""))]
        self.assertEqual([], found)

    def test_the_readme_names_no_internal_id(self):
        with open(os.path.join(REPO, "README.md"), encoding="utf-8") as f:
            found = [(n, m.group(0)) for n, line in enumerate(f, 1) for m in self.pattern.finditer(line)]
        self.assertEqual([], found)

    def test_readme_first_names_no_internal_id(self):
        """README-FIRST.txt goes into the release zip."""
        with open(os.path.join(REPO, "release", "README-FIRST.txt"), encoding="utf-8") as f:
            found = [(n, m.group(0)) for n, line in enumerate(f, 1) for m in self.pattern.finditer(line)]
        self.assertEqual([], found)


class ReadmeFirstTests(unittest.TestCase):
    """The real release/README-FIRST.txt (#56). It fills with the values of each kind of version, and it makes no claim
    that is false for this version."""

    VERSIONS = ("1.0.0-dev", "1.0.0-beta.1", "1.0.0-rc.1", "1.0.0")

    def filled(self, version):
        text, problems = rc.readme_first(REPO, version)
        self.assertEqual([], problems, version)
        return text

    def section(self, text, heading):
        """The lines of a section: from its heading and its rule line to the next empty line pair."""
        match = re.search(rf"^{re.escape(heading)}\n-+\n(.*?)\n\n\n", text, re.M | re.S)
        self.assertIsNotNone(match, heading)
        return match.group(1)

    def test_each_kind_of_version_fills_each_placeholder(self):
        for version in self.VERSIONS:
            text = self.filled(version)
            title, rule = text.split("\n")[:2]
            self.assertEqual(f"OpenLancer {rc.version_title(version)}", title)
            self.assertEqual("=" * len(title), rule)
            self.assertIn(f"Open the folder OpenLancer-v{version}.", text)
            self.assertIsNone(rc.PLACEHOLDER.search(text), version)

    def test_the_text_makes_no_old_claim(self):
        text = self.filled("1.0.0-beta.1")
        for claim in ("Export. No tool installs", "bases, factions", "There is no mission editor",
                      "we did not test", "the English version 1.0"):
            self.assertNotIn(claim, text)

    def test_the_tested_games_are_freelancer_1_1_and_hd_edition_0_7_1(self):
        requirements = " ".join(self.section(self.filled("1.0.0"), "SYSTEM REQUIREMENTS").split())
        self.assertIn("We tested this version with the English Freelancer 1.1 and with Freelancer: HD Edition 0.7.1.",
                      requirements)

    def test_what_works_names_the_export_the_mission_editor_and_the_database(self):
        works = " ".join(self.section(self.filled("1.0.0"), "WHAT WORKS IN THIS PREVIEW").split())
        self.assertIn("as a drop-in zip and as an FLMM package, with Mod > Export mod...", works)
        self.assertIn("The mission editor shows a mission as a graph of triggers.", works)
        self.assertIn("Look at the star systems, ships, goods and equipment in the Database.", works)


class ReleaseScriptTests(unittest.TestCase):
    """The rules of the steps of tools/release.ps1 (#57). The tools tests cannot run the script, so they read its text."""

    @classmethod
    def setUpClass(cls):
        with open(os.path.join(REPO, "tools", "release.ps1"), encoding="utf-8") as f:
            cls.text = f.read()

    def step(self, heading):
        """The text of one step: from its heading comment to the next rule line."""
        match = re.search(rf"^# {re.escape(heading)}\n(.*?)^# -{{20,}}$", self.text, re.M | re.S)
        self.assertIsNotNone(match, heading)
        return match.group(1)

    def test_the_release_script_names_no_folder_of_a_machine(self):
        """The default work root of tools/release.ps1 is a rule, not a folder of one machine."""
        found = [(n, m.group(0)) for n, line in enumerate(self.text.splitlines(), 1)
                 for m in re.finditer(r"(?<![\w$])[A-Za-z]:[\\/][^\s'\"]*", line)]
        self.assertEqual([], found)

    def test_the_preflight_checks_the_work_root_and_names_the_zip(self):
        """Both checks stop the run before it writes a file or clones. The zip name comes only from the name command."""
        preflight = self.step("1. Preflight")
        work_root = preflight.index("'work-root', $script:Root")
        self.assertLess(work_root, preflight.index("New-Item -ItemType Directory"))
        self.assertIn("'name', '--version', $buildVersion", preflight)
        self.assertIn("$forbid = @($sourcePaths | ForEach-Object", self.text)
        self.assertNotIn("Split-Path -Parent $devFull", self.text)
        steps = self.text[self.text.index("# 1. Preflight"):]
        self.assertNotIn("win-x64.zip", steps)

    def test_the_tests_leave_out_the_tests_that_write_next_to_the_game_data(self):
        tests = self.step("4. Tests")
        left_out = re.search(r"\$notNextToData = '([^']*)'", tests).group(1).split("&")
        self.assertEqual(["FullyQualifiedName!~PlayCopyRetailTests", "FullyQualifiedName!~RandomEditTests",
                          "FullyQualifiedName!~SampleModTests"], left_out)
        runs = re.findall(r"Invoke-Native \$dotnet @\('test', '([^']+)'(.*?)\) \$src([^\n]*)", tests, re.S)
        projects = {project: (arguments, tail) for project, arguments, tail in runs}
        self.assertEqual({"OpenLancer.slnx", "tests\\OpenLancer.DataTests",
                          "external\\Librelancer\\src\\LibreLancer.Tests\\LibreLancer.Tests.csproj"}, set(projects))
        self.assertIn('"$notNextToData&Category!=Performance"', projects["OpenLancer.slnx"][0])
        gates, tail = projects["tests\\OpenLancer.DataTests"]
        self.assertIn('"Category=Performance&$notNextToData"', gates)
        self.assertEqual(" -Tries 2", tail)

    def test_the_publish_names_the_engine_commit(self):
        """Help > About shows the engine commit."""
        self.assertIn('"-p:OpenLancerEngineCommit=$forkSha"', self.step("5. Publish"))


if __name__ == "__main__":
    unittest.main()
