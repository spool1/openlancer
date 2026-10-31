# Contributing to OpenLancer

OpenLancer is a mod editor for Freelancer (2003). This file tells you how to build OpenLancer, how to run the tests and what a pull request must do. Read the legal rules first. The owner does not merge a pull request that breaks one of them.

## Before you start

1. Read [ROADMAP.md](ROADMAP.md). Each pull request belongs to one milestone of the roadmap. Each open milestone has an issue with the label `roadmap`, and the boxes of the issue are its tasks.
2. For work that is not in a milestone, open an issue first. Wait for an answer from the owner before you start.
3. For a new milestone or a large change, write a short design in the issue. The owner reviews the design before you build it.

Each milestone goes through four steps: design, adversarial review, build and verify.

## Legal rules

1. Do not add game files to the repository.
2. Do not add game data to the repository. Each user links their own game folder.
3. Do not copy text or bytes from a retail file into a test fixture. Write the fixture by hand, or read the retail data when the test runs.
4. Do not add decompiled code or disassembled code. This rule includes code from Freelancer-Decomp and from all other decompilation projects. Do not port such code, and do not copy its names, its structure or its comments.
5. You can use facts about how the retail game works. Write each fact only in your own plain words. Example: "Retail draws the cloud at the zone radii."
6. Do not write names or addresses from a decompiler or a disassembler. Examples are a `FUN_` or `DAT_` name with an address, a `param_` or `uVar` name with a number, and a code address of the game.
7. Keep all third-party license notices. When you add a third-party file or package, add its notice to `NOTICE`. When you copy a file from Librelancer, add it to `docs/VENDORED.md`.
8. Do not add GPL code to the OpenLancer projects or to the engine fork.
9. Do not use "Freelancer" as a name of OpenLancer or of a part of OpenLancer. Use "Freelancer" only to identify the 2003 game.
10. Do not send changes, issues or comments to Librelancer.

When you send a pull request, you agree that your contribution is available under the MIT License of this repository.

CI runs `tools/content_scan.py` in each run. The scan finds retail files, binary files and decompiler output. It cannot find all problems, so the owner also reads each change.

## Build

You need these tools:

- Windows.
- The .NET SDK that `global.json` names.
- Visual Studio 2022 or 2026 with the Desktop C++ workload.
- CMake 4.2 or later.
- Git and Python 3.

Fork the repository on GitHub. Then follow "Set up > Developers" in [README.md](README.md), and clone your fork in step 1. These steps build the engine submodule, run the tests and start the editor.

Do not use `dotnet build --no-incremental`. The engine projects then fail with CS1566. This is a known problem.

## Run the tests

Run these commands before each pull request:

```
dotnet test OpenLancer.slnx
python -B -m unittest discover -s tools
python tools/content_scan.py
```

`dotnet test OpenLancer.slnx` runs the unit tests (`tests/OpenLancer.Tests`) and the data tests (`tests/OpenLancer.DataTests`).

The retail tests in the data tests need your own game data. CI has no game data, so the retail tests skip in CI. To run them:

1. Extract the game data from your own retail disc of the game.
2. Set `OPENLANCER_BASE` to the folder that holds `DATA` and `EXE`. Or put the folder at `..\GameData\base`, next to the repository.
3. Check the folder with `python tools\verify_base.py <folder>`. The retail tests expect the data of the English retail 1.0 disc, which gives the digest in README.md. With the data of another version, some retail tests can fail.

Without game data, the retail tests skip. A skipped test is not a passed test. Write the passed, failed and skipped counts of your retail run in the pull request.

Some retail tests have a timing gate, for example "the run of the 14 story scripts takes less than 2 s". These tests have the trait `Category=Performance`. Each measure runs with a high priority. The gates are of two kinds:

- A gate on a first run measures one run. Examples are the first run of the story scripts and the first build of an index. A second run is faster, because the runtime compiled the code and filled the caches. So the gate does not try again. A gate on a step that the test cannot run again, for example a delete, also measures one run.
- A gate on a step that runs often takes the best of its tries. While the best try is over the limit, the gate tries again. All gates of one test try again for up to 60 s in total. So a short burst of other work, for example the unit tests or another build, does not fail these gates.

A first run with cold code is slower than a first run with warm code, so the two runs have different limits. For example, the catch-up test of the retail systems (P06 in `SceneApplierRetailTests`) has two limits for the first catch-up of a system:

- The cold first catch-up is the first catch-up of the test. No warm-up runs before it. The gate measures one run with no retry, and the limit is 250 ms.
- The first catch-up of each other system runs with warm code. The limit is 100 ms, and the gate can try again.

A burst of other work can fail a gate that measures one run. A long build can also slow the computer for longer than 60 s. When a gate fails on a busy computer, run the gates alone:

```
dotnet test tests/OpenLancer.DataTests --filter Category=Performance
```

When you change the engine fork, also run the fork tests:

```
dotnet test external\Librelancer\src\LibreLancer.Tests\LibreLancer.Tests.csproj
```

## Pull requests

- Put one topic in each pull request.
- Name the milestone, for example `Milestone: M23`.
- Write the title as a commit subject: imperative and in STE. Example: "Add prop groups to the asset browser (M23)". The owner squashes the pull request into one commit with this title.
- Add a test for each change. A bug fix starts with a test that fails without the fix.
- A raw string literal takes the line ends of its source file. When a test compares a literal with text that the code writes, call `.ReplaceLineEndings("\n")` on the literal. Then the test passes with LF and with CRLF line ends.
- Update the docs that describe the changed behavior.
- Write prose in Simplified Technical English (STE): short sentences, active voice, no semicolons and no contractions. This rule applies to docs, comments, UI text, error messages, commit messages and pull request text. The full checklist is in the section "Writing style" of AGENTS.md.
- When Unity has an equivalent behavior, follow Unity.
- Make sure that CI passes.
- Do not write paths of your machine, user names or private URLs in code, docs or tests.
- Do not add Markdown files. The repository keeps only `README.md`, `CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, `ROADMAP.md`, `.github/pull_request_template.md`, `data-manifest/README.md`, the files in `docs/` and the release notes in `release/notes/`. A new file in `docs/` or `release/notes/` needs the approval of the owner. Put your own notes in `notes/`. Git ignores that folder.

Comments and docs cite decisions such as `D81` and designs such as "the M21 design". These records are not public. If a rule is not clear, ask in the pull request. In new code, write the rule itself in plain words.

## Engine changes

The engine is our fork of Librelancer at `external/Librelancer`: the repository `spool1/openlancer-engine`, branch `openlancer`.

1. Make each engine change a separate commit in the fork.
2. Start the commit subject with the next free change number from [docs/UPSTREAM.md](docs/UPSTREAM.md). Example of an earlier commit: `OL-0028: Nebulas fill their whole zone`.
3. Open a pull request on `spool1/openlancer-engine` against `openlancer`.
4. Open a pull request on `spool1/openlancer` that moves the submodule pointer to your fork commit. Add the row of the change to `docs/UPSTREAM.md` in the same pull request.
5. Link the two pull requests.

Do not change `.gitmodules`. The submodule URL stays `https://github.com/spool1/openlancer-engine.git`. CI gets your fork commit from the pull request of step 3.

CI builds and tests the pair. The owner merges the fork pull request so that your commit keeps its ID. Then the owner merges the OpenLancer pull request. If another change takes your number first, the owner tells you the new number.

## Review checklist

The owner uses this list for each pull request.

1. The pull request has one topic. It names its milestone or its issue.
2. The diff adds no game file and no game data. Test fixtures are small and written by hand.
3. The diff has no decompiled code. Look for decompiler style also when the scan passes. Signs are names that look made by a machine, unexplained offsets and casts between unrelated types. Other signs are chains of goto and code that does not match the files around it.
4. Facts about the retail game are in plain words, without decompiler names or addresses.
5. `NOTICE` has each new third-party file or package, and `docs/VENDORED.md` has each file copied from Librelancer. The diff has no GPL code. No new name uses "Freelancer".
6. Each change has a test, and the test fails without the change.
7. CI passed. For a change to data handling, saves, Play or the engine, the pull request gives the counts of a local retail run.
8. For a change to data handling, saves, Play or the engine, run the retail tests yourself. Check out the pull request and use your own game data.
9. Each engine change is a separate OL commit in the fork, with a row in `docs/UPSTREAM.md`. The fork tests pass. The diff does not change `.gitmodules`.
10. The behavior follows Unity where Unity has an equivalent. The editor never leaves data that can crash the game.
11. The docs are up to date, and the prose is in STE.
12. The diff adds no Markdown file outside the list.
