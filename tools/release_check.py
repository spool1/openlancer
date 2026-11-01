"""Merge, document, check and zip a binary release of OpenLancer (research r1-release-plan, sections 3.2, 5 and 7).

Usage:
  python tools/release_check.py name --version <v> --sha7 <commit> [--dry-run]
      Prints the file name of the zip. It refuses a release of a development version (a suffix that starts with "dev").
  python tools/release_check.py work-root <work root> --repo <dev repo>
      Prints the paths that check c forbids, one on each line. It refuses a work root that holds a text that check c
      forbids, because the natives keep the source paths of the build.
  python tools/release_check.py merge <stage> <program>=<publish folder>...
      Merges the publish folders of the programs into the stage folder.
  python tools/release_check.py docs <stage> --repo <clone> --version <v> --commit <sha> --fork-commit <sha>
                                     [--notes-out <file>] [--dry-run]
      Writes README-FIRST.txt, LICENSE.txt, NOTICE.txt and licenses/<component>.txt into the stage folder, and the
      release notes with the commits filled in. A dry run without a notes file writes no notes.
  python tools/release_check.py stage <stage> --repo <clone> --version <v> [--forbid <text>]...
      Runs the checks a to i on the stage folder. It stops at the first check that finds a problem and lists each
      problem of that check.
  python tools/release_check.py zip <stage> <zip> --repo <clone> --time <unix time> [--forbid <text>]...
      Writes the zip with one top folder, sorted entries and one fixed time. Then it runs the checks a, b and c on the
      zip entries.

tools/release.ps1 runs these steps in a clean clone. A real release needs every text without a "[if built]", "[W4]"
or "[F5]" mark, and every component of release/third-party.json without a "status". --dry-run allows both.
release/release_check_allow.txt holds the reviewed false hits of the checks c and i.
Exit code 0 means that the step passed. Exit code 1 means a problem.
The tests are in tools/test_release_check.py: python -B -m unittest discover -s tools
"""
import argparse
import fnmatch
import glob
import hashlib
import json
import ntpath
import os
import posixpath
import re
import shutil
import struct
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import content_scan  # noqa: E402

PROGRAM_SUFFIXES = (".exe", ".dll", ".deps.json", ".runtimeconfig.json")
# Upstream Librelancer removes the same files from its releases. A PDB is embedded in its assembly.
REMOVED_FILES = {"createdump.exe", "sos_readme.md", "bepuphysics.xml", "bepuutilities.xml", "litenetlib.xml"}
REMOVED_FOLDERS = {"x86"}
DOCUMENTS = ("README-FIRST.txt", "LICENSE.txt", "NOTICE.txt")
# A line of a release text that waits for work: "[if built]" (a milestone), "[W4]" (a work item), "[F5]" (a finding).
MARK = re.compile(r"\[(?:if built|W\d+|F\d+)\]")
PLACEHOLDER = re.compile(r"@[A-Z_]+@")
VC_RUNTIME = re.compile(r"^(?:vcruntime140|msvcp140)", re.IGNORECASE)
COPYRIGHT = re.compile(r"copyright\s*(?:\(c\)|©|\d{4})|\(c\)\s*\d{4}", re.IGNORECASE)
SOURCE_EXTENSIONS = {".cs", ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".hlsl"}
# Folders of a source tree that the release does not compile.
SKIPPED_SOURCE_FOLDER = re.compile(
    r"(?:^|/)(?:tests?|examples?|demos?|docs?|benchmarks?|samples?|tools|tutorials?|bin|obj|emscripten|\.git)(?:/|$)",
    re.IGNORECASE)
EMBEDDED_PDB = 17
RULE = "-" * 72


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def files_below(root):
    """The relative paths of all files below a folder, with "/" separators, sorted."""
    names = []
    for folder, _, files in os.walk(root):
        for name in files:
            names.append(os.path.relpath(os.path.join(folder, name), root).replace(os.sep, "/"))
    return sorted(names)


def read_text(path):
    """The text of a file in UTF-8. A few third-party files use Windows-1252, for example SMAA.hlsl."""
    with open(path, "rb") as f:
        data = f.read()
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = data.decode("cp1252", "replace")
    return text.replace("\r\n", "\n")


def write_text(path, text, crlf):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    text = text.replace("\r\n", "\n")
    if not text.endswith("\n"):
        text += "\n"
    with open(path, "w", encoding="utf-8", newline="\r\n" if crlf else "\n") as f:
        f.write(text)


# ---------------------------------------------------------------------------------------------------------------------
# Release data

class Allow:
    """The reviewed false hits of release/release_check_allow.txt: "<check>\\t<path glob>\\t<match>\\t<reason>".

    A hit is allowed when the check is the same, the path matches the glob and the text of the hit holds <match>.
    """

    def __init__(self, entries=()):
        self.entries = list(entries)
        self.used = set()

    @staticmethod
    def read(path):
        entries = []
        if os.path.isfile(path):
            for number, line in enumerate(read_text(path).split("\n"), 1):
                if not line.strip() or line.startswith("#"):
                    continue
                parts = line.split("\t")
                if len(parts) != 4 or not parts[3].strip():
                    raise SystemExit(f"{path}:{number}: write '<check><TAB><path glob><TAB><match><TAB><reason>'.")
                entries.append(tuple(parts[:3]))
        return Allow(entries)

    def permits(self, check, path, text):
        for entry in self.entries:
            if (entry[0] == check and fnmatch.fnmatch(path.lower(), entry[1].lower())
                    and entry[2].lower() in text.lower()):
                self.used.add(entry)
                return True
        return False

    def unused(self):
        return [f"allow-list entry allows nothing: {'  '.join(e)}" for e in self.entries if e not in self.used]


def load_release_data(repo):
    with open(os.path.join(repo, "release", "third-party.json"), encoding="utf-8") as f:
        return json.load(f)


def manifest_hashes(repo):
    """The SHA-256 hashes of each retail manifest in data-manifest/ (the paths, sizes and hashes of retail files)."""
    return content_scan.manifest_hashes(sorted(glob.glob(os.path.join(repo, "data-manifest", "*.tsv"))))


def version_title(version):
    """"0.1.0-preview" gives "v0.1.0 (preview)", the form of the README and the release title."""
    number, _, suffix = version.partition("-")
    return f"v{number} ({suffix})" if suffix else f"v{number}"


def is_development(version):
    """True for a development version: its suffix starts with "dev", for example "1.0.0-dev". Master holds such a
    version between two releases. BuildInfo.IsDevelopment of OpenLancer.Data uses the same rule."""
    return version.partition("-")[2].startswith("dev")


def zip_name(version, dry_run, sha7):
    """The file name of the release zip. A release gives "OpenLancer-v1.0.0-beta.1-win-x64.zip". A dry run also
    names its commit, for example "OpenLancer-1.0.0-dev-dryrun-abc1234-win-x64.zip"."""
    if dry_run:
        return f"OpenLancer-{version}-dryrun-{sha7}-win-x64.zip"
    return f"OpenLancer-v{version}-win-x64.zip"


def name_problems(version, dry_run):
    """The problems of the version of a release before the clone: a release of a development version."""
    if dry_run or not is_development(version):
        return []
    return [f"the version {version} is a development version. Only a dry run can build it. Set the version of the "
            "release in src/Directory.Build.props first."]


def deps_files(stage):
    return sorted(glob.glob(os.path.join(stage, "*.deps.json")))


def deps_libraries(stage):
    """{library id: version} and {file name in lower case: library id} of the package and runtime pack libraries in the
    deps.json files of the stage folder. The project libraries are in deps_projects."""
    versions, owners = {}, {}
    for path in deps_files(stage):
        with open(path, encoding="utf-8-sig") as f:
            deps = json.load(f)
        types = {key: value.get("type") for key, value in deps.get("libraries", {}).items()}
        for target in deps.get("targets", {}).values():
            for key, assets in target.items():
                if types.get(key) not in ("package", "runtimepack"):
                    continue
                library, _, version = key.partition("/")
                versions[library] = version
                for kind in ("runtime", "native"):
                    for asset in assets.get(kind, {}):
                        owners[posixpath.basename(asset).lower()] = library
    return versions, owners


def deps_projects(stage):
    """The names of the project libraries in the deps.json files: the assembly names of the shipped projects."""
    names = set()
    for path in deps_files(stage):
        with open(path, encoding="utf-8-sig") as f:
            deps = json.load(f)
        names.update(key.partition("/")[0] for key, value in deps.get("libraries", {}).items()
                     if value.get("type") == "project")
    return names


def project_files(repo):
    """{assembly name: project file} of the C# projects of OpenLancer and the engine fork."""
    found = {}
    for top in ("src", os.path.join("external", "Librelancer", "src"),
                os.path.join("external", "Librelancer", "extern")):
        for folder, dirs, files in os.walk(os.path.join(repo, top)):
            dirs[:] = [d for d in dirs if d.lower() not in ("bin", "obj", ".git")]
            for name in files:
                if not name.endswith(".csproj"):
                    continue
                path = os.path.join(folder, name)
                assembly = os.path.splitext(name)[0]
                try:
                    root = ET.parse(path).getroot()
                    for node in root.iter():
                        if node.tag.split("}")[-1] == "AssemblyName" and node.text and "$(" not in node.text:
                            assembly = node.text.strip()
                except ET.ParseError:
                    pass
                # The shortest path wins: a demo project of a submodule can use the same name.
                if assembly not in found or len(path) < len(found[assembly]):
                    found[assembly] = path
    return found


def nuget_root():
    return os.environ.get("NUGET_PACKAGES") or os.path.join(os.path.expanduser("~"), ".nuget", "packages")


def header_comment(text):
    """The first comment block of a source file, without the comment marks: a /* */ block or a run of // lines.
    Empty lines and #region lines before it are skipped."""
    lines = [line.rstrip() for line in text.lstrip("\ufeff").replace("\r\n", "\n").split("\n")]
    i = 0
    while i < len(lines) and (not lines[i].strip() or lines[i].strip().startswith("#region")):
        i += 1
    out = []
    if i < len(lines) and lines[i].lstrip().startswith("/*"):
        first = lines[i].lstrip()[2:].lstrip("*")
        if "*/" in first:
            return first[:first.index("*/")].strip() + "\n"
        out.append(first.strip())
        i += 1
        while i < len(lines) and "*/" not in lines[i]:
            out.append(re.sub(r"^\s*\* ?", "", lines[i]))
            i += 1
        if i < len(lines):
            out.append(re.sub(r"^\s*\* ?", "", lines[i][:lines[i].index("*/")]).rstrip())
    else:
        # An empty line can split a license comment, for example "// MIT License", "", "// Copyright ...".
        while i < len(lines) and (lines[i].lstrip().startswith("//") or not lines[i].strip()):
            out.append(re.sub(r"^\s*//+ ?", "", lines[i]))
            i += 1
    return "\n".join(out).strip("\n") + "\n" if any(line.strip() for line in out) else ""


def resolve_text(source, repo, versions):
    """The text of one source of release/third-party.json, or raise ValueError."""
    kind, _, rest = source.partition(":")
    if kind == "repo":
        path = os.path.join(repo, rest)
    elif kind == "header":
        path = os.path.join(repo, rest)
        if not os.path.isfile(path):
            raise ValueError(f"{source}: the file does not exist")
        text = header_comment(read_text(path))
        if not text:
            raise ValueError(f"{source}: the file starts with no comment block")
        return text
    elif kind in ("nuget", "runtimepack"):
        if kind == "runtimepack":
            # The .NET runtime pack. A program with a framework reference, for example the ASP.NET Core of the agent
            # server of the editor, adds a second runtime pack, and a nuget: source names that pack.
            packs = [lib for lib in versions if lib.startswith("runtimepack.")]
            library = next((lib for lib in packs if lib.startswith("runtimepack.Microsoft.NETCore.App.")),
                           packs[0] if packs else None)
            if library is None:
                raise ValueError(f"{source}: the deps.json files name no runtime pack")
            package, file = library[len("runtimepack."):], rest
        else:
            package, _, file = rest.partition("/")
            library = package if package in versions else "runtimepack." + package
            if library not in versions:
                raise ValueError(f"{source}: the deps.json files do not name the package {package}")
        path = os.path.join(nuget_root(), package.lower(), versions[library], file)
    else:
        raise ValueError(f"{source}: the source must start with repo:, header:, nuget: or runtimepack:")
    if not os.path.isfile(path):
        raise ValueError(f"{source}: the file does not exist ({path})")
    return read_text(path)


# ---------------------------------------------------------------------------------------------------------------------
# merge

def merge(stage, publishes):
    """Merges the publish folder of each program into the stage folder (section 3.2 of the plan).

    publishes is a list of (program, folder). The four files of a program come from the publish of that program. Each
    other path must have the same bytes in each publish that has it. Returns the problems.
    """
    owned = {p: {(p + s).lower() for s in PROGRAM_SUFFIXES} for p, _ in publishes}
    chosen, problems = {}, []
    for program, folder in publishes:
        for name in files_below(folder):
            key = name.lower()
            owner = next((p for p, names in owned.items() if key in names), None)
            if owner is not None and owner != program:
                continue
            top, _, rest = name.partition("/")
            if key in REMOVED_FILES or key.endswith(".pdb") or (rest and top.lower() in REMOVED_FOLDERS):
                continue
            path = os.path.join(folder, name)
            digest = sha256_file(path)
            if key in chosen and chosen[key][2] != digest:
                problems.append(f"different bytes in two publishes: {name} ({chosen[key][0]} and {path})")
                continue
            chosen.setdefault(key, (path, name, digest))
    for program, _ in publishes:
        for suffix in PROGRAM_SUFFIXES:
            if (program + suffix).lower() not in chosen:
                problems.append(f"no publish has {program}{suffix}")
    if problems:
        return problems
    if os.path.exists(stage) and os.listdir(stage):
        return [f"the stage folder is not empty: {stage}"]
    for path, name, _ in chosen.values():
        target = os.path.join(stage, *name.split("/"))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copyfile(path, target)
    return []


# ---------------------------------------------------------------------------------------------------------------------
# docs

def marks_problems(name, text):
    return [f"{name}:{n}: the line waits for work {MARK.search(line).group(0)}: {line.strip()}"
            for n, line in enumerate(text.split("\n"), 1) if MARK.search(line)]


def fill(name, text, values):
    for key, value in values.items():
        text = text.replace(key, value)
    left = sorted(set(PLACEHOLDER.findall(text)))
    return text, [f"{name}: unknown placeholder {p}" for p in left]


def component_text(component, repo, versions):
    """The text of licenses/<id>.txt of one component. Raises ValueError for a missing source."""
    head = [component["name"], "=" * len(component["name"]), f"Version: {component['version']}",
            f"License: {component['license']}"]
    if component.get("files"):
        head.append("Files: " + ", ".join(component["files"]))
    if component.get("packages"):
        head.append("NuGet packages: " + ", ".join(component["packages"]))
    if component.get("note"):
        head.append("Note: " + component["note"])
    parts = ["\n".join(head)]
    if component.get("copyright"):
        parts.append("\n".join(component["copyright"]))
    parts += [resolve_text(source, repo, versions).strip("\n") for source in component["texts"]]
    return f"\n\n{RULE}\n\n".join(parts) + "\n"


def notice_text(repo, data):
    lines = [read_text(os.path.join(repo, "NOTICE")).rstrip("\n"), "", "",
             "Components of this release", "--------------------------",
             "Each component has its license text in the folder \"licenses\".", ""]
    for c in data["components"]:
        lines.append(f"  {c['name']} {c['version']}")
        lines.append(f"    License: {c['license']}. Text: licenses/{c['id']}.txt")
    return "\n".join(lines) + "\n"


def docs(stage, repo, version, commit, fork_commit, notes_out=None, dry_run=False):
    """Writes the documents of the release into the stage folder. Returns the problems.

    With notes_out, a release needs its notes file release/notes/v<version>.md. A dry run without a notes file prints a
    note and writes no notes file.
    """
    data = load_release_data(repo)
    problems = []
    values = {"@VERSION@": version, "@VERSION_TITLE@": version_title(version)}
    readme = read_text(os.path.join(repo, "release", "README-FIRST.txt"))
    title = f"OpenLancer {version_title(version)}"
    readme, found = fill("release/README-FIRST.txt", readme.replace("@TITLE_RULE@", "=" * len(title)), values)
    problems += found
    notes_name = f"release/notes/v{version}.md"
    notes_path = os.path.join(repo, *notes_name.split("/"))
    notes = None
    if notes_out:
        if os.path.isfile(notes_path):
            notes, found = fill(notes_name, read_text(notes_path),
                                {"@COMMIT@": commit, "@FORK_COMMIT@": fork_commit})
            problems += found
        elif dry_run:
            print(f"note: no release notes {notes_name}. The dry run writes no notes file.")
        else:
            problems.append(f"no release notes: {notes_name}")
    if not dry_run:
        problems += marks_problems("release/README-FIRST.txt", readme)
        if notes is not None:
            problems += marks_problems(notes_name, notes)
        for c in data["components"]:
            if c.get("status"):
                problems.append(f"component {c['id']} has the status '{c['status']}': {c.get('note', '')}")
        if not (data.get("openal_source") or {}).get("sha256"):
            problems.append("release/third-party.json: openal_source has no sha256 (R1-D8, R1-D19)")
    versions, _ = deps_libraries(stage)
    texts = {}
    for c in data["components"]:
        try:
            texts[c["id"]] = component_text(c, repo, versions)
        except ValueError as e:
            problems.append(f"component {c['id']}: {e}")
    if problems:
        return problems
    write_text(os.path.join(stage, "README-FIRST.txt"), readme, crlf=True)
    write_text(os.path.join(stage, "LICENSE.txt"), read_text(os.path.join(repo, "LICENSE")), crlf=True)
    write_text(os.path.join(stage, "NOTICE.txt"), notice_text(repo, data), crlf=True)
    for cid, text in texts.items():
        write_text(os.path.join(stage, "licenses", cid + ".txt"), text, crlf=True)
    if notes is not None:
        write_text(notes_out, notes, crlf=False)
    return []


# ---------------------------------------------------------------------------------------------------------------------
# PE files

class PE:
    """The parts of a PE file (an exe or a DLL) that the checks need. Raises ValueError for another file."""

    def __init__(self, data):
        if data[:2] != b"MZ":
            raise ValueError("no MZ header")
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("no PE header")
        self.data = data
        coff = pe + 4
        sections = struct.unpack_from("<H", data, coff + 2)[0]
        optional_size = struct.unpack_from("<H", data, coff + 16)[0]
        optional = coff + 20
        magic = struct.unpack_from("<H", data, optional)[0]
        self.directories = optional + (112 if magic == 0x20B else 96)
        self.sections = []
        table = optional + optional_size
        for i in range(sections):
            size, address, raw_size, raw = struct.unpack_from("<IIII", data, table + 40 * i + 8)
            self.sections.append((address, max(size, raw_size), raw))

    def offset(self, rva):
        for address, size, raw in self.sections:
            if address <= rva < address + size:
                return rva - address + raw
        return None

    def directory(self, index):
        return struct.unpack_from("<II", self.data, self.directories + 8 * index)

    def imports(self):
        """The names of the DLLs that the file imports."""
        rva, _ = self.directory(1)
        names, at = [], self.offset(rva) if rva else None
        while at is not None and self.data[at:at + 20] != b"\0" * 20:
            name_at = self.offset(struct.unpack_from("<I", self.data, at + 12)[0])
            names.append(self.data[name_at:self.data.index(b"\0", name_at)].decode("ascii", "replace"))
            at += 20
        return names

    def embedded_pdbs(self):
        """The decompressed embedded portable PDBs: debug entries of type 17, "MPDB", size, then deflate data."""
        rva, size = self.directory(6)
        at = self.offset(rva) if rva else None
        out = []
        for i in range(size // 28 if at is not None else 0):
            kind, length, _, raw = struct.unpack_from("<IIII", self.data, at + 28 * i + 12)
            if kind == EMBEDDED_PDB and self.data[raw:raw + 4] == b"MPDB":
                out.append(zlib.decompress(self.data[raw + 8:raw + length], -15))
        return out


def pe_or_none(data):
    try:
        return PE(data)
    except (ValueError, struct.error):
        return None


def version_info(data):
    """(file version, product version) from the version resource, for example ("0.1.0.0", "0.1.0-preview+abc1234")."""
    at = data.find(b"\xbd\x04\xef\xfe")
    file_version = None
    if at >= 0:
        ms, ls = struct.unpack_from("<II", data, at + 8)
        file_version = f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"
    key = "ProductVersion\0".encode("utf-16-le")
    at = data.find(key)
    product = None
    if at >= 0:
        at += len(key)
        while data[at:at + 2] == b"\0\0":
            at += 2
        end = at
        while data[end:end + 2] != b"\0\0":
            end += 2
        product = data[at:end].decode("utf-16-le", "replace")
    return file_version, product


# ---------------------------------------------------------------------------------------------------------------------
# Checks

def allowed_name(name):
    """Check a: the file types that a release may hold. No game data type can pass."""
    lower = name.lower()
    if "/" not in lower:
        return (lower.endswith((".exe", ".dll", ".dll.config", ".deps.json", ".runtimeconfig.json"))
                or name in DOCUMENTS)
    top, _, rest = name.partition("/")
    if top == "x64":
        return "/" not in rest and lower.endswith(".dll")
    if top == "licenses":
        return "/" not in rest and lower.endswith(".txt")
    return False


def check_names(names):
    return [f"a: file type not allowed in a release: {n}" for n in names if not allowed_name(n)]


def check_retail(entries, hashes):
    """Check b. entries is a list of (name, bytes)."""
    return [f"b: identical to a retail file: {name}" for name, data in entries
            if hashes and content_scan.digests(data) & hashes]


def is_drive_root(path):
    """True for a path that names no folder, for example "X:\\" or "/"."""
    return re.fullmatch(r"(?:[A-Za-z]:)?[\\/]*", path) is not None


def source_paths(repo):
    """The paths of the dev repo that no release file may hold: the repo and its parent folder. A drive root names no
    folder, so it is not in the list. Else check c would refuse each native that a build on that drive makes."""
    repo = repo.rstrip("\\/")
    parent = ntpath.dirname(repo)
    return [repo] if is_drive_root(parent) else [repo, parent]


def work_root_problems(work_root, repo):
    """The problems of the work root of tools/release.ps1, before the clone. The natives keep the source paths of the
    build, and these paths are below the work root. So check c refuses a work root that holds a forbidden text, but
    only at the end of the run. This check finds the same texts in the path of the work root. It is stricter than
    check c in two ways. It ignores case, because a build can use the case of the folders on the disk. It also finds
    the word "decompiled" in a longer word, because a path holds no code names."""
    root = work_root.rstrip("\\/")
    key = ntpath.normcase(root) + "\\"
    if key.startswith(ntpath.normcase(repo.rstrip("\\/")) + "\\"):
        return [f"the work root {root} is in the repo. Use a folder outside the repo, for example the default work "
                "root."]
    data = (key + "|" + key.replace("\\", "/")).encode("utf-8")
    problems = []
    for label, text in forbidden_values(source_paths(repo)):
        patterns = [(l, t, "nocase" if mode == "word" else mode) for l, t, mode in search_patterns([(label, text)])]
        if find_strings(data, patterns):
            problems.append(f"the work root {root} holds the {label} '{text}'. The natives keep the source paths of "
                            "the build, and check c refuses a release file with this text. Use another work root, for "
                            "example the default work root.")
    return problems


def forbidden_values(extra):
    """The texts that no release file may hold: the extra texts (the source_paths of the dev repo), the user profile,
    the user name, the git e-mail and the word "decompiled". Each value is (label, text)."""
    values = [("path", e) for e in extra if e]
    for label, var in (("user profile", "USERPROFILE"), ("user name", "USERNAME"), ("home", "HOME")):
        if os.environ.get(var):
            values.append((label, os.environ[var]))
    try:
        mail = subprocess.run(["git", "config", "user.email"], capture_output=True, text=True).stdout.strip()
    except OSError:
        mail = ""
    if mail:
        values.append(("git e-mail", mail))
    values.append(("word", "decompiled"))
    return values


def search_patterns(values):
    """(label, text, mode) for each form of each value: as is, with "/" and with "\\\\" separators.
    The mode "word" matches a whole word in any case, so "decompiled" does not match "IsDynamicCodeCompiled". A text of
    fewer than 8 characters has the mode "exact": its exact case, its lower case and its upper case only, so that it
    does not match random bytes. Each other text has the mode "nocase"."""
    out = []
    for label, text in values:
        if label == "word":
            out.append((label, text, "word"))
            continue
        forms = {text, text.replace("\\", "/"), text.replace("\\", "\\\\")}
        for form in sorted(forms):
            if len(form) < 8:
                out += [(label, f, "exact") for f in sorted({form, form.lower(), form.upper()})]
            else:
                out.append((label, form, "nocase"))
    return out


def find_strings(data, patterns):
    """The labels and texts of the patterns that the bytes hold, in UTF-8 or in UTF-16LE."""
    lower = data.lower()
    hits = []
    for label, text, mode in patterns:
        for encoding in ("utf-8", "utf-16-le"):
            if mode == "exact":
                found = text.encode(encoding) in data
            elif mode == "nocase":
                found = text.lower().encode(encoding) in lower
            else:
                letter = b"[a-z]" if encoding == "utf-8" else b"[a-z]\x00"
                word = re.escape(text.lower().encode(encoding))
                found = re.search(b"(?<!" + letter + b")" + word + b"(?!" + letter + b")", lower) is not None
            if found:
                hits.append((label, text))
                break
    return hits


def check_strings(entries, patterns, allow):
    """Check c. Searches each file and each embedded PDB of a PE file."""
    problems = []
    for name, data in entries:
        blobs = [("", data)]
        pe = pe_or_none(data)
        if pe is not None:
            try:
                blobs += [(" (embedded PDB)", pdb) for pdb in pe.embedded_pdbs()]
            except (zlib.error, struct.error) as e:
                problems.append(f"c: {name}: the embedded PDB cannot be read: {e}")
        for where, blob in blobs:
            for label, text in find_strings(blob, patterns):
                if not allow.permits("c", name, label):
                    problems.append(f"c: {name}{where} holds the {label} '{text}'")
    return problems


def check_natives(stage, repo, data):
    """Check d: x64/ holds exactly the natives, and the prebuilt ones are the files of deps/x64 in the clone."""
    problems = []
    folder = os.path.join(stage, "x64")
    present = set(os.listdir(folder)) if os.path.isdir(folder) else set()
    expected = set(data["natives"])
    problems += [f"d: x64/{n} is not a native of the release" for n in sorted(present - expected)]
    problems += [f"d: x64/{n} is missing" for n in sorted(expected - present)]
    for name in data["prebuilt_natives"]:
        source = os.path.join(repo, "external", "Librelancer", "deps", "x64", name)
        target = os.path.join(folder, name)
        if name in present and os.path.isfile(source) and sha256_file(source) != sha256_file(target):
            problems.append(f"d: x64/{name} is not the file of external/Librelancer/deps/x64")
    return problems


def check_imports(entries):
    """Check e: no PE file needs the Visual C++ Redistributable (R1-D3)."""
    problems = []
    for name, data in entries:
        pe = pe_or_none(data)
        if pe is not None:
            problems += [f"e: {name} imports {dll}" for dll in pe.imports() if VC_RUNTIME.match(dll)]
    return problems


def component_of(name, data, owners):
    """The components of one file of the stage: by its package in the deps.json files, else by the file globs."""
    lower = name.lower()
    library = owners.get(lower) if "/" not in lower else None
    found = [c["id"] for c in data["components"] if library and library in c.get("packages", [])]
    found += [c["id"] for c in data["components"]
              if any(fnmatch.fnmatch(lower, g.lower()) for g in c.get("files", []))]
    return found


def check_notices(stage, names, data):
    """Check f: each DLL and exe has a component, each package has a component, and each component has its text."""
    versions, owners = deps_libraries(stage)
    problems = []
    for name in names:
        if name.lower().endswith((".dll", ".exe")) and not component_of(name, data, owners):
            problems.append(f"f: no component in release/third-party.json for {name}")
    packages = {p for c in data["components"] for p in c.get("packages", [])}
    problems += [f"f: no component in release/third-party.json for the package {lib}"
                 for lib in sorted(versions) if lib not in packages]
    problems += [f"f: no license text licenses/{c['id']}.txt" for c in data["components"]
                 if not os.path.isfile(os.path.join(stage, "licenses", c["id"] + ".txt"))]
    return problems


def embedded_sources(project):
    """The files of the EmbeddedResource items of a project file (simple paths and globs, no MSBuild properties)."""
    folder = os.path.dirname(project)
    out = []
    root = ET.parse(project).getroot()
    for node in root.iter():
        if node.tag.split("}")[-1] != "EmbeddedResource":
            continue
        for include in (node.get("Include") or "").split(";"):
            include = include.strip().replace("\\", "/")
            if not include or any(m in include for m in ("$(", "%(", "@(")):
                continue
            out += [p for p in glob.glob(os.path.join(folder, include), recursive=True) if os.path.isfile(p)]
    return sorted(set(out))


def check_embedded(stage, repo, hashes):
    """Check g: no embedded resource of a shipped project is a retail file."""
    projects = project_files(repo)
    problems = []
    for name in sorted(deps_projects(stage)):
        project = projects.get(name)
        if project is None:
            problems.append(f"g: no project file for the shipped project {name}")
            continue
        for path in embedded_sources(project):
            with open(path, "rb") as f:
                if hashes and content_scan.digests(f.read()) & hashes:
                    rel = os.path.relpath(path, repo)
                    problems.append(f"g: the embedded resource {rel} is identical to a retail file")
    return problems


def check_version(stage, version):
    """Check h: the version resource of the editor."""
    path = os.path.join(stage, "OpenLancer.Editor.exe")
    if not os.path.isfile(path):
        return ["h: OpenLancer.Editor.exe is missing"]
    with open(path, "rb") as f:
        file_version, product = version_info(f.read())
    number = version.partition("-")[0]
    expected = ".".join((number.split(".") + ["0", "0", "0", "0"])[:4])
    problems = []
    if file_version != expected:
        problems.append(f"h: OpenLancer.Editor.exe has the file version {file_version}, not {expected}")
    if not (product or "").startswith(version + "+"):
        problems.append(f"h: OpenLancer.Editor.exe has the product version {product}, not {version}+<commit>")
    return problems


def source_roots(stage, repo, data):
    """(folder, recursive) of the sources of the shipped binaries: the shipped projects and the native sources."""
    projects = project_files(repo)
    roots = [(os.path.dirname(projects[n]), True) for n in sorted(deps_projects(stage)) if n in projects]
    for entry in data.get("native_sources", []):
        recursive = not entry.endswith("/*")
        roots.append((os.path.join(repo, *entry.rstrip("/*").split("/")), recursive))
    return roots


def copyright_lines(root, repo, recursive=True):
    """(path relative to the repo, line number, text) of each copyright line of the source files below a folder. When
    the holder is on the next line ("Copyright 2001-2005 by" and then the names), the text holds both lines."""
    for folder, dirs, files in os.walk(root):
        rel_folder = os.path.relpath(folder, root).replace(os.sep, "/")
        if rel_folder != "." and (not recursive or SKIPPED_SOURCE_FOLDER.search(rel_folder)):
            dirs[:] = []
            continue
        for name in sorted(files):
            if os.path.splitext(name)[1].lower() not in SOURCE_EXTENSIONS:
                continue
            path = os.path.join(folder, name)
            with open(path, encoding="utf-8", errors="replace") as f:
                lines = f.read().split("\n")
            for number, line in enumerate(lines, 1):
                if COPYRIGHT.search(line):
                    text = line.strip()
                    if number < len(lines):
                        text += " " + lines[number].strip()
                    yield os.path.relpath(path, repo).replace(os.sep, "/"), number, text


def check_source_notices(stage, repo, data, allow):
    """Check i: each copyright holder in the sources of the shipped binaries has a component (V21)."""
    holders = [h.lower() for c in data["components"] for h in c.get("holders", [])]
    problems = []
    for root, recursive in source_roots(stage, repo, data):
        for path, number, line in copyright_lines(root, repo, recursive):
            if any(h in line.lower() for h in holders) or allow.permits("i", path, line):
                continue
            problems.append(f"i: {path}:{number}: no component names this holder: {line[:160]}")
    return problems


def stage_entries(stage, names):
    for name in names:
        with open(os.path.join(stage, *name.split("/")), "rb") as f:
            yield name, f.read()


def check_stage(stage, repo, version, forbid=()):
    """The checks a to i. Returns the problems of the first check that finds one."""
    data = load_release_data(repo)
    allow = Allow.read(os.path.join(repo, "release", "release_check_allow.txt"))
    names = files_below(stage)
    hashes = manifest_hashes(repo)
    if not hashes:
        return ["b: no retail manifest in data-manifest/. The hash checks need it."]
    patterns = search_patterns(forbidden_values(forbid))
    checks = [
        lambda: check_names(names),
        lambda: check_retail(stage_entries(stage, names), hashes),
        lambda: check_strings(stage_entries(stage, names), patterns, allow),
        lambda: check_natives(stage, repo, data),
        lambda: check_imports(stage_entries(stage, names)),
        lambda: check_notices(stage, names, data),
        lambda: check_embedded(stage, repo, hashes),
        lambda: check_version(stage, version),
        lambda: check_source_notices(stage, repo, data, allow),
    ]
    for check in checks:
        problems = check()
        if problems:
            return problems
    return allow.unused()


# ---------------------------------------------------------------------------------------------------------------------
# zip

def write_zip(stage, zip_path, unix_time):
    """Writes the zip: one top folder (the name of the stage folder), sorted "/" names, one fixed time."""
    top = os.path.basename(os.path.normpath(stage))
    stamp = time.gmtime(max(int(unix_time), 315532800))[:6]
    os.makedirs(os.path.dirname(os.path.abspath(zip_path)), exist_ok=True)
    with zipfile.ZipFile(zip_path, "w") as z:
        for name in files_below(stage):
            info = zipfile.ZipInfo(f"{top}/{name}", date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            info.create_system = 0
            with open(os.path.join(stage, *name.split("/")), "rb") as f:
                z.writestr(info, f.read(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def check_zip(zip_path, repo, forbid=()):
    """The checks a, b and c on the entries of the zip."""
    allow = Allow.read(os.path.join(repo, "release", "release_check_allow.txt"))
    with zipfile.ZipFile(zip_path) as z:
        entries = [(info.filename.partition("/")[2], z.read(info)) for info in z.infolist() if not info.is_dir()]
    names = [n for n, _ in entries]
    for problems in (check_names(names), check_retail(entries, manifest_hashes(repo)),
                     check_strings(entries, search_patterns(forbidden_values(forbid)), allow)):
        if problems:
            return problems
    return []


# ---------------------------------------------------------------------------------------------------------------------

def main(argv):
    parser = argparse.ArgumentParser(description="Merge, document, check and zip a binary release.")
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("name")
    p.add_argument("--version", required=True)
    p.add_argument("--sha7", required=True)
    p.add_argument("--dry-run", action="store_true")
    p = sub.add_parser("work-root")
    p.add_argument("work_root")
    p.add_argument("--repo", required=True)
    p = sub.add_parser("merge")
    p.add_argument("stage")
    p.add_argument("publishes", nargs="+", metavar="program=folder")
    p = sub.add_parser("docs")
    p.add_argument("stage")
    p.add_argument("--repo", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--commit", required=True)
    p.add_argument("--fork-commit", required=True)
    p.add_argument("--notes-out")
    p.add_argument("--dry-run", action="store_true")
    p = sub.add_parser("stage")
    p.add_argument("stage")
    p.add_argument("--repo", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--forbid", action="append", default=[])
    p = sub.add_parser("zip")
    p.add_argument("stage")
    p.add_argument("zip")
    p.add_argument("--repo", required=True)
    p.add_argument("--time", required=True, type=int)
    p.add_argument("--forbid", action="append", default=[])
    args = parser.parse_args(argv)

    if args.command == "name":
        problems = name_problems(args.version, args.dry_run)
        if not problems:
            # The only output is the name, so tools/release.ps1 can read it.
            print(zip_name(args.version, args.dry_run, args.sha7))
            return 0
    elif args.command == "work-root":
        problems = work_root_problems(args.work_root, args.repo)
        if not problems:
            # The only output is the paths, so tools/release.ps1 can give them to the checks with --forbid.
            print("\n".join(source_paths(args.repo)))
            return 0
    elif args.command == "merge":
        pairs = [item.partition("=")[::2] for item in args.publishes]
        problems = merge(args.stage, pairs)
    elif args.command == "docs":
        problems = docs(args.stage, args.repo, args.version, args.commit, args.fork_commit, args.notes_out,
                        args.dry_run)
    elif args.command == "stage":
        problems = check_stage(args.stage, args.repo, args.version, args.forbid)
    else:
        write_zip(args.stage, args.zip, args.time)
        problems = check_zip(args.zip, args.repo, args.forbid)
    for problem in problems:
        print(problem)
    print(f"release {args.command}: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
