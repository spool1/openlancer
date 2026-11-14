"""Fail when the repo holds content that it must not hold.

Usage: python tools/content_scan.py           scans OpenLancer (the submodule is excluded)
       python tools/content_scan.py --fork    scans the changes of the engine fork against its base commit

The scan checks every file that git tracks or would add:
  1. Retail file types (disc images, cabinets, fonts, UTF models and textures, cutscenes) are not allowed. The
     thumbnails of the editor (.olthumb) are not allowed either, because the editor renders them from game data.
     The one exception is a THN patch of a mod (decision D70): an ASCII .thn file below the patches/ folder of a
     mod (the patches/ folder next to its mod.json) whose first statement after its comment block is
     "ol_thn_patch = 1". A patch holds only keys and changed values.
  2. No file starts with the signature of a retail container: "BINI", "UTF " or compiled Lua. No file starts with
     "OLTH", the signature of a thumbnail of the editor.
  3. No binary file, unless tools/content_scan_allow.txt allows it.
  4. No C, C++ or assembly source. Native code belongs in the engine fork.
  5. Only the Markdown files in MARKDOWN_FILES.
  6. No decompiler output in code files: automatic names, decompiler helpers, retail code addresses, and in
     comments the register notes of IDA, the names of decompilers and the word "decompiled". Markdown files get
     the rules that check the whole line.
  7. No file may have the same SHA-256 as a game file in a manifest of data-manifest/ (each <build id>.tsv, for
     example vanilla-1.0.tsv and hde-0.7.1.tsv). A text file is also checked in its LF form and its CRLF form,
     because git can change line ends.
--fork applies rules 1, 2, 3 and 7 to the files that the fork adds or changes after the base commit in
docs/UPSTREAM.md, and rule 6 to the lines that the fork adds.
Exit code 0 means clean. Exit code 1 means the scan found a problem.

The tests are in tools/test_content_scan.py: python -B -m unittest discover -s tools
"""
import fnmatch
import glob
import hashlib
import os
import posixpath
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST_FOLDER = os.path.join(REPO, "data-manifest")
SUBMODULE = "external/"
FORBIDDEN_EXTENSIONS = {
    ".iso", ".cab", ".ttf", ".fl", ".lrpk",
    ".utf", ".cmp", ".3db", ".mat", ".txm", ".vms", ".sph",
    ".ale", ".anm", ".dfm", ".thn", ".sur", ".wmv",
    ".olthumb",
}
# Why a forbidden file type that is not a file of the game is forbidden.
FORBIDDEN_REASONS = {
    ".olthumb": "a thumbnail that the editor renders from game data",
}
PATCHES_FOLDER = "patches"
MOD_MANIFEST = "mod.json"
# The bytes of ASCII text: the printable characters, tab and the line ends.
TEXT_BYTES = frozenset(range(0x20, 0x7F)) | {0x09, 0x0A, 0x0D}
THN_PATCH_START = re.compile(r"ol_thn_patch[ \t]*=[ \t]*1(?![0-9A-Za-z_.])")
ALLOW_FILE = os.path.join(REPO, "tools", "content_scan_allow.txt")
UPSTREAM_DOC = os.path.join(REPO, "docs", "UPSTREAM.md")
FORK = "external/Librelancer"
# A copy of a retail file keeps its first bytes under any name.
SIGNATURES = {
    b"BINI": "a binary INI file",
    b"UTF ": "a UTF file (model, texture, sound or font container)",
    b"\x1bLua": "compiled Lua (a retail script)",
    b"OLTH": "a thumbnail that the editor renders from game data",
}
# Git treats a file with a NUL byte in its first 8000 bytes as binary. The scan does the same.
BINARY_PROBE = 8000
NATIVE_EXTENSIONS = {".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".asm", ".s"}
CODE_EXTENSIONS = NATIVE_EXTENSIONS | {".cs"}
# The Markdown files of the repo. A new file needs the approval of the owner (CONTRIBUTING.md).
MARKDOWN_FILES = {
    "README.md", "CONTRIBUTING.md", "AGENTS.md", "CLAUDE.md", "ROADMAP.md", ".github/pull_request_template.md",
    "data-manifest/README.md", "docs/CUTSCENES.md", "docs/GAME_LINK.md", "docs/GETTING_STARTED.md", "docs/MCP.md",
    "docs/MISSIONS.md", "docs/MOD_FORMAT.md", "docs/PLAY.md", "docs/TROUBLESHOOTING.md", "docs/UPSTREAM.md",
    "docs/VENDORED.md", "release/notes/v0.1.0-preview.md",
}
# Before and after an automatic name: no letter and no digit. "_" is allowed, as in PTR_FUN_005e1c40.
NO_ALNUM_BEFORE = r"(?<![0-9A-Za-z])"
NO_ALNUM_AFTER = r"(?![0-9A-Za-z])"
# The decompiled-code rules: (rule, pattern, comments only). The rules that check the whole line also apply to
# Markdown files. No rule matches the code files, the Markdown files or the lines that the fork adds (2026-10-18).
# tools/test_content_scan.py holds typical lines of Ghidra and IDA output that each rule must find.
DECOMPILED = [
    ("auto-label", re.compile(
        NO_ALNUM_BEFORE + r"(?:FUN|LAB|DAT|PTR|switchD|caseD)_[0-9A-Fa-f]{6,8}" + NO_ALNUM_AFTER
        + r"|" + NO_ALNUM_BEFORE + r"(?:sub|loc|off|unk|byte|word|dword|qword|stru|asc|nullsub)_[0-9A-Fa-f]{5,8}"
        + NO_ALNUM_AFTER + r"|\bLABEL_[0-9]{1,4}\b"), False),
    ("auto-variable", re.compile(
        r"\blocal_(?:res)?[0-9A-Fa-f]{1,4}\b|\bparam_[0-9]{1,2}\b"
        r"|\b(?:p{1,3}a?|a)?[ubcisfdlwhv]Var[0-9]{1,3}\b|\bp{1,3}Var[0-9]{1,3}\b"
        r"|\b(?:au|ab|ac|af|ai|al|as)Stack_[0-9A-Fa-f]{1,4}\b"
        r"|\b(?:unaff|extraout)_\w+|\bin_(?:[A-Z]{2,3}|stack_[0-9A-Fa-f]{8}|[FG]S_OFFSET)\b"
        r"|\bundefined[1-8]\b|\bthunk_\w+"), False),
    ("decompiler-helper", re.compile(
        r"\b(?:CONCAT[0-9]{2}|SUB[0-9]{2}|ZEXT[0-9]{2,3}|SEXT[0-9]{2,3}|CARRY[0-9]|SCARRY[0-9]|SBORROW[0-9])\s*\("
        r"|\b(?:_DWORD|_BYTE|_WORD|_QWORD|_OWORD|LODWORD|HIDWORD|__usercall|__thiscall|__fastcall|float10)\b"
        r"|\b__(?:ROL|ROR)[1248]__|\b__S?PAIR(?:16|32|64|128)__|\b__(?:CF|OF)(?:ADD|SUB|SHL|SHR|RCL|RCR)__"
        r"|\b(?:BYTE|WORD|SBYTE|SWORD)[1-7]\s*\("
        r"|@<(?:[er]?(?:ax|bx|cx|dx|si|di|bp|sp)|[a-d][lh]|st0|xmm[0-7])>|\(code \*\*?\)"), False),
    # Freelancer.exe loads at 0x00400000 and the game DLLs between 0x06000000 and 0x06FFFFFF. Only the 8-digit
    # form of an exe address counts, because a 6-digit literal in that range is usually an RGB color.
    # decompiled_problem skips a power of two, because that is a bit flag or a size (WS_DLGFRAME, 4 MiB).
    ("retail-address", re.compile(r"\b0x(?:00[45][0-9A-Fa-f]{5}|0?6[0-9A-Fa-f]{6})(?:[uUlL]{1,3})?\b"), False),
    # IDA writes the register or the stack slot of a local variable in a comment: "// eax", "// [esp+10h] [ebp-8h]".
    ("ida-comment", re.compile(
        r"\[[er]?(?:sp|bp)[+-][0-9A-F]+h\]"
        r"|^\s*(?:[er]?(?:ax|bx|cx|dx|si|di)|[a-d][lh]|st[0-7]|xmm[0-9]{1,2})\s*(?:BYREF\s*)?$"), True),
    # Librelancer names its THN bytecode reader ThnDecompile, so these words count only in comments.
    ("tool-marker", re.compile(
        r"\bGhidra\b|\bIDA(?: Pro)?\b|Hex-?Rays|\bBinary Ninja\b|\bFreelancer-?Decomp\b"
        r"|\b[Dd]ecompil(?:e|ed|er|ers|ation|ing)\b|\b[Dd]isassembl(?:e|ed|er|y|ing)\b"), True),
]
BASE_ROW = re.compile(r"^\| Commit \| `([0-9a-f]{40})` \|$", re.MULTILINE)


class Allow:
    """The exceptions of tools/content_scan_allow.txt. Each line is "<path or glob>\\t<rule>\\t<reason>".

    The scan reports an entry that allowed nothing, so that the list does not keep stale entries.
    """

    def __init__(self, entries=()):
        self.entries = list(entries)
        self.used = set()

    @staticmethod
    def read(path=ALLOW_FILE):
        entries = []
        if os.path.isfile(path):
            with open(path, encoding="utf-8") as f:
                for number, line in enumerate(f, 1):
                    text = line.rstrip("\r\n")
                    if not text.strip() or text.startswith("#"):
                        continue
                    parts = text.split("\t")
                    if len(parts) != 3 or not parts[2].strip():
                        raise SystemExit(f"{path}:{number}: write '<path or glob><TAB><rule><TAB><reason>'.")
                    entries.append((parts[0], parts[1]))
        return Allow(entries)

    def permits(self, name, rule):
        for entry in self.entries:
            if entry[1] == rule and fnmatch.fnmatchcase(name, entry[0]):
                self.used.add(entry)
                return True
        return False

    def unused(self, fork=False):
        """The entries that allowed nothing: the fork entries with fork=True, else the others."""
        return [f"allow-list entry allows nothing: {glob}\t{rule}" for glob, rule in self.entries
                if (glob, rule) not in self.used and glob.startswith(FORK + "/") == fork]


NO_ALLOW = Allow()


def git(*args, cwd=REPO):
    return subprocess.run(["git", "-C", cwd, *args], check=True, capture_output=True).stdout


def repo_files():
    out = git("ls-files", "--cached", "--others", "--exclude-standard", "-z")
    names = [n.decode("utf-8") for n in out.split(b"\0") if n]
    return [n for n in names if not n.startswith(SUBMODULE) and os.path.isfile(os.path.join(REPO, n))]


def manifest_hashes(paths):
    """The SHA-256 hashes of the files in retail manifests: the third column of each line. tools/release_check.py uses
    it too, so both checks read the manifests the same way."""
    hashes = set()
    for path in paths:
        with open(path, encoding="utf-8") as f:
            hashes.update(line.rstrip("\n").split("\t")[2] for line in f if line.strip())
    return hashes


def manifests(folder=MANIFEST_FOLDER):
    """The manifest files of a folder: each <build id>.tsv, in sorted order."""
    return sorted(glob.glob(os.path.join(folder, "*.tsv")))


def retail_hashes(folder=MANIFEST_FOLDER):
    """The SHA-256 hashes of the game files of each manifest in the folder."""
    return manifest_hashes(manifests(folder))


def mod_folders(names):
    """The folders of the mods in a list of repo paths: each folder that holds a mod.json, relative to the repo."""
    return {posixpath.dirname(n) for n in names if posixpath.basename(n) == MOD_MANIFEST}


def in_mod_patches(name, mods):
    """True when the file is below the patches/ folder of a mod: the patches/ folder next to its mod.json.

    Another folder with the name patches/ does not count. For example, the overlay serves a file in files/DATA/patches/
    of a mod as a whole file.
    """
    parts = name.split("/")
    return any(parts[i] == PATCHES_FOLDER and "/".join(parts[:i]) in mods for i in range(len(parts) - 1))


def is_thn_patch(name, data, mods):
    """True for a THN patch of a mod: ASCII text in the patches/ folder of a mod that starts with "ol_thn_patch = 1".

    name is the path relative to the repo, with "/" separators. mods is the set of mod folders (see mod_folders). Lua
    3.2 has only line comments, so the comment block is the lines at the top that are empty or start with "--".
    """
    if not in_mod_patches(name, mods) or not all(b in TEXT_BYTES for b in data):
        return False
    for line in data.decode("ascii").splitlines():
        text = line.strip(" \t")
        if text and not text.startswith("--"):
            return THN_PATCH_START.match(text) is not None
    return False


def comment_parts(lines):
    """For each line, the text of its comments: after "//", and between "/*" and "*/" also over several lines.

    It does not parse strings, so a "//" in a string starts a comment. That gives a match too many, never too few.
    """
    in_block = False
    for line in lines:
        comment, rest = [], line
        while rest:
            if in_block:
                end = rest.find("*/")
                comment.append(rest if end < 0 else rest[:end])
                rest, in_block = ("", True) if end < 0 else (rest[end + 2:], False)
            else:
                line_at, block_at = rest.find("//"), rest.find("/*")
                if line_at >= 0 and (block_at < 0 or line_at < block_at):
                    comment.append(rest[line_at + 2:])
                    rest = ""
                elif block_at >= 0:
                    rest, in_block = rest[block_at + 2:], True
                else:
                    rest = ""
        yield " ".join(comment)


def is_power_of_two(literal):
    """True for a hex literal such as 0x00400000u with one bit set: a flag or a size, not a code address."""
    value = int(literal.rstrip("uUlL"), 16)
    return value & (value - 1) == 0


def decompiled_problem(name, lines, allow, first_line=1, code=True):
    """The first decompiled-code match in the lines of a file, or None.

    code=False is for Markdown: it skips the rules that count only in comments, because a doc can name a decompiler.
    """
    for number, (line, comment) in enumerate(zip(lines, comment_parts(lines)), first_line):
        for rule, pattern, comments_only in DECOMPILED:
            if comments_only and not code:
                continue
            for match in pattern.finditer(comment if comments_only else line):
                if rule == "retail-address" and is_power_of_two(match.group(0)):
                    continue
                if not allow.permits(name, rule):
                    return f"decompiled-code pattern ({rule}): {name}:{number}: {match.group(0)}"
                break
    return None


def digests(data):
    """The SHA-256 of the bytes. For a text file, also of its LF form and its CRLF form, because git can change the
    line ends of a text file at checkout (.gitattributes)."""
    forms = {data}
    if b"\0" not in data[:BINARY_PROBE]:
        lf = data.replace(b"\r\n", b"\n")
        forms.update((lf, lf.replace(b"\n", b"\r\n")))
    return {hashlib.sha256(form).hexdigest() for form in forms}


def file_problem(name, data, hashes, mods, allow, fork=False):
    """The problem of one file from its bytes, or None. With fork=True, only the rules that apply to the fork."""
    extension = os.path.splitext(name)[1].lower()
    if extension in FORBIDDEN_EXTENSIONS:
        if extension in FORBIDDEN_REASONS:
            return f"retail file type: {name} ({FORBIDDEN_REASONS[extension]})"
        if extension != ".thn":
            return f"retail file type: {name}"
        if not is_thn_patch(name, data, mods):
            return f"retail file type: {name} (a .thn file must be a THN patch in the patches/ folder of a mod)"
    for signature, kind in SIGNATURES.items():
        if data.startswith(signature):
            return f"retail file signature: {name} starts like {kind}"
    if b"\0" in data[:BINARY_PROBE] and not allow.permits(name, "binary"):
        return f"binary file: {name} (add it to tools/content_scan_allow.txt only with the approval of the owner)"
    if not fork:
        if extension in NATIVE_EXTENSIONS and not allow.permits(name, "native-source"):
            return f"native source: {name} (native code belongs in the engine fork)"
        if extension == ".md" and name not in MARKDOWN_FILES:
            return f"Markdown file not in the list: {name} (see CONTRIBUTING.md, or use notes/)"
        if extension in CODE_EXTENSIONS or extension == ".md":
            found = decompiled_problem(name, data.decode("utf-8", "replace").splitlines(), allow,
                                       code=extension in CODE_EXTENSIONS)
            if found:
                return found
    if hashes and digests(data) & hashes:
        return f"identical to a retail file: {name}"
    return None


def problem(name, path, hashes, mods, allow=NO_ALLOW):
    """The problem of one file, or None. path is the file on disk. mods is the set of mod folders (see mod_folders)."""
    with open(path, "rb") as f:
        return file_problem(name, f.read(), hashes, mods, allow)


def fork_base():
    """The base commit of the fork: the "Commit" row of "Base commit" in docs/UPSTREAM.md."""
    with open(UPSTREAM_DOC, encoding="utf-8") as f:
        match = BASE_ROW.search(f.read())
    if not match:
        raise SystemExit("docs/UPSTREAM.md has no 'Commit' row in 'Base commit'.")
    return match.group(1)


def added_lines(diff):
    """(path, first line number, lines) for each hunk of a "git diff -U0" output, with only the added lines.

    A "+++ " line is a file header only between "diff --git" and the first "@@" of the file. After that, it is an
    added line that starts with "++ ".
    """
    path, start, lines, in_header = None, 0, [], False
    for raw in diff.decode("utf-8", "replace").splitlines():
        if raw.startswith("diff --git "):
            if path and lines:
                yield path, start, lines
            path, lines, in_header = None, [], True
        elif in_header and raw.startswith("+++ "):
            path = raw[6:] if raw.startswith("+++ b/") else None
        elif raw.startswith("@@"):
            if path and lines:
                yield path, start, lines
            start, lines, in_header = int(re.search(r"\+(\d+)", raw).group(1)), [], False
        elif raw.startswith("+") and path and not in_header:
            lines.append(raw[1:])
    if path and lines:
        yield path, start, lines


def fork_problems(hashes, allow):
    fork = os.path.join(REPO, FORK)
    base = fork_base()
    if subprocess.run(["git", "-C", fork, "cat-file", "-e", base + "^{commit}"], capture_output=True).returncode:
        print(f"fetching the fork base commit {base}")
        git("fetch", "--depth", "1", "origin", base, cwd=fork)
    problems = []
    out = git("diff", "--no-renames", "--name-only", "--diff-filter=AM", "-z", base, "HEAD", cwd=fork)
    for name in (n.decode("utf-8") for n in out.split(b"\0") if n):
        data = git("show", f"HEAD:{name}", cwd=fork)
        found = file_problem(f"{FORK}/{name}", data, hashes, set(), allow, fork=True)
        if found:
            problems.append(found)
    diff = git("diff", "--no-renames", "-U0", "--no-color", base, "HEAD", "--",
               *(f"*{e}" for e in sorted(CODE_EXTENSIONS)), cwd=fork)
    for name, start, lines in added_lines(diff):
        found = decompiled_problem(f"{FORK}/{name}", lines, allow, start)
        if found:
            problems.append(found)
    return problems


def main(argv):
    hashes = retail_hashes()
    if not hashes:
        print(f"warning: no manifest in {MANIFEST_FOLDER}. The hash check is off.")
    allow = Allow.read()
    if "--fork" in argv:
        problems = fork_problems(hashes, allow) + allow.unused(fork=True)
    else:
        names = repo_files()
        mods = mod_folders(names)
        problems = [p for p in (problem(name, os.path.join(REPO, name), hashes, mods, allow) for name in names) if p]
        problems += allow.unused()
    for p in problems:
        print(p)
    print(f"content scan: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
