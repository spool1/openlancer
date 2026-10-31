# Rules for AI coding agents

These rules apply to each AI agent that works in this repository. [CONTRIBUTING.md](CONTRIBUTING.md) has the full rules and the reasons.

## Never

1. Never add game files or game data. Never copy text or bytes from a retail file into the repository, also not into a test fixture.
2. Never write, paste or port decompiled or disassembled code. This rule includes code from Freelancer-Decomp and from all other decompilation projects. Never copy their names, structure or comments.
3. Never write names that a decompiler makes, such as a `FUN_` or `DAT_` name with an address, or a `param_` or `uVar` name with a number. Never write retail code addresses.
4. Never open, write or comment on issues or pull requests for Librelancer. `external/Librelancer/AGENTS.md` is the rule of the Librelancer project, and you must obey it for Librelancer itself. Engine changes for OpenLancer go only to the OpenLancer fork, as CONTRIBUTING.md tells.
5. Never add GPL code to OpenLancer or to the engine fork. Never remove a license notice. Add the notice of each new third-party file or package to `NOTICE`.
6. Never use "Freelancer" in the name of a project, program, package or feature.
7. Never change `tools/content_scan.py`, `tools/content_scan_allow.txt` or `.github/` only to make a check pass. Report the problem to the person who gave you the task.
8. Never add a Markdown file outside the list in CONTRIBUTING.md. Write your working notes in `notes/`. Git ignores that folder.
9. Never write paths of your machine, user names or private URLs in code, docs or tests.

## Always

1. You can use facts about how the retail game works. Write each fact in your own plain words. Example: "Retail draws the cloud at the zone radii."
2. Add a test for each change.
3. Before you finish, run `dotnet test OpenLancer.slnx` and `python tools/content_scan.py`. Report the counts of passed, failed and skipped tests.
4. Write prose in Simplified Technical English: short sentences, active voice, no semicolons and no contractions. This rule applies to docs, comments, UI text and commit messages.
5. Follow Unity when Unity has an equivalent behavior.
6. Put each engine change in a separate commit in `external/Librelancer`, with the subject `OL-NNNN: <the change>`. Add its row to `docs/UPSTREAM.md`.
7. When a comment cites a decision (such as `D81`) or a design that you cannot read, ask. Do not guess the rule.
8. When your task has a GitHub issue, read the issue before you start. Each box of the issue is a task. Before you finish, check each box against the code and the tests. Report each box as done or not done. For a done box, name the commit or the test that proves it. For an open box, give the reason.

## Editor UI rules

1. Give each field a plain name that says what it does, for example "Enemy ships" or "Spawn when". Do not show a raw INI key name as the label.
2. Use a picker or a dropdown with values from the loaded data for each field that names other data. Check each value, and give the reason for each error.
3. Never use a comma-separated text field for a list of ships, enemies, factions, loadouts or waypoints. Use a list widget with Add, Remove and reorder, one row for each entry, with named sub-fields. Write the retail line format only when the editor saves.
4. Do not show a field that has no effect in the current setup. Put raw and advanced keys in a closed "Advanced" section and in the raw INI block.
5. Show the higher-level item first, for example a battle beat or an event. Show the raw trigger, condition and action lines only on request.
6. Make linked items easy to follow. When the user selects an item in one view, highlight the items that it uses in the other views.
7. Make each repeated pattern a prefab. A prefab instance can override a field. Show the override in bold with a violet mark, and give "Revert" and "Apply to prefab". Blue means an unsaved edit, so an unsaved override shows both marks. Never change the base prefab until the user applies the override.
8. Model retail data as components, and show components as high-level UI parts. Do not show loose keys when a component can show them.
9. Never leave a field ambiguous. Each field has a clear name and says what it does. Give a hover tooltip for each field that is hard to understand.
10. Give each high-level item (a component, a beat, an event, a prefab) an always-visible info icon that explains what it is and what it writes to the game files.
11. Always give access to the raw INI. The raw editor highlights the syntax and marks errors and warnings with the same checks as the fields, with the reason for each.
12. For a system that links many parts (a mission, a cutscene, a trade lane, an encounter, a prefab), show a completeness panel at the top of the Inspector, for example "4 of 5 steps complete". Each missing step shows what is missing, why it is necessary, and a button that goes to it or fixes it.

## Writing style: Simplified Technical English (STE)

All prose in this repository uses ASD-STE100 Simplified Technical English: docs, code comments, UI text, error messages, commit messages, pull request text and release notes. It does not apply to code, identifiers or command syntax.

Check each text before you finish:

1. Sentences: at most 20 words for an instruction, at most 25 words for a description. Split longer sentences.
2. No semicolons. Write two sentences.
3. No contractions. Write "do not", not "don't".
4. Active voice when the actor is known. Write "the parser reads the file", not "the file is read by the parser".
5. Use a verb for an action. Write "check the data", not "perform a check of the data". No phrasal verbs such as "spin up" or "set up" when a single verb works ("start", "make").
6. Use the short common word: start (not begin or initiate), use (not utilize or leverage), make sure (not ensure), before (not prior to), about (not regarding), get (not obtain), show (not demonstrate), also (not additionally).
7. Use one name for one thing in a text. Give each word one meaning.
8. No marketing words, for example seamless, robust, powerful or effortless.
9. Paragraphs have one topic and at most six sentences. For steps, use a numbered list with one action per item in the imperative, and put a condition before its command.
10. UI error messages and procedures use the strict form: every rule above, and each error that blocks a save or Play says what the game does with the data. Example: "The game stops loading with this value. Pick another value."
