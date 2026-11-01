"""Tests of the issue forms in .github/ISSUE_TEMPLATE (#54). Run them with: python -B -m unittest discover -s tools

GitHub shows a form in the issue chooser only when the form is valid. The tests cannot open an issue, so they parse each
form and check its fields with the rules of GitHub. The tests need PyYAML. Without PyYAML, they skip.
"""
import os
import re
import sys
import unittest

try:
    import yaml
except ImportError:
    yaml = None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from test_release_check import internal_ids  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FORMS = os.path.join(REPO, ".github", "ISSUE_TEMPLATE")
NO_YAML = ("PyYAML is not installed, so the tests of the issue forms skip. "
           "Install it with: python -m pip install PyYAML==6.0.3")
# Each form and the labels that it gives to a new issue. The labels exist on the repository.
FORM_LABELS = {
    "bug_report.yml": ["bug"],
    "feature_request.yml": ["enhancement"],
    "playtest_finding.yml": ["playtest"],
}
CONFIG = "config.yml"
SETUP_HELP = "https://discord.com/channels/1564795741589737543/1564810865251975270"
INVITE = "https://discord.gg/BUh3nucfmP"
# The areas of the play test plan of the 1.0 scope.
AREAS = [
    "Install", "First run and Game link", "Welcome", "Scene", "Hierarchy", "Inspector", "Database", "Assets",
    "Save gate", "Undo and the close prompt", "F5 and Ctrl+F5", "Mods window and export", "Help menu", "Missions",
    "Play from a trigger", "Cutscenes", "Agent server", "oltool", "Help pages", "Other",
]
# The keys of a form and of each field type that GitHub accepts.
FORM_KEYS = {"name", "description", "title", "labels", "assignees", "projects", "type", "body"}
REQUIRED_FORM_KEYS = {"name", "description", "body"}
ATTRIBUTES = {
    "markdown": {"value"},
    "input": {"label", "description", "placeholder", "value"},
    "textarea": {"label", "description", "placeholder", "value", "render"},
    "dropdown": {"label", "description", "multiple", "options", "default"},
    "checkboxes": {"label", "description", "options"},
}
FIELD_ID = re.compile(r"^[A-Za-z0-9_-]+$")


def form_problems(form):
    """The problems of one issue form, or an empty list. The rules are the rules that GitHub uses for a form."""
    if not isinstance(form, dict):
        return ["the form is not a mapping"]
    problems = [f"unknown key {k}" for k in sorted(set(form) - FORM_KEYS)]
    problems += [f"no {k}" for k in sorted(REQUIRED_FORM_KEYS - set(form))]
    problems += [f"{k} is empty" for k in ("name", "description") if not str(form.get(k) or "").strip()]
    body = form.get("body")
    if not isinstance(body, list) or not any(isinstance(f, dict) and f.get("type") != "markdown" for f in body):
        return problems + ["the body has no field"]
    ids, labels = set(), set()
    for index, field in enumerate(body):
        where = f"body[{index}]"
        kind = field.get("type") if isinstance(field, dict) else None
        if kind not in ATTRIBUTES:
            problems.append(f"{where}: unknown type {kind}")
            continue
        attributes = field.get("attributes") or {}
        problems += [f"{where}: unknown attribute {a}" for a in sorted(set(attributes) - ATTRIBUTES[kind])]
        if kind == "markdown":
            if not str(attributes.get("value") or "").strip():
                problems.append(f"{where}: the markdown has no value")
            problems += [f"{where}: a markdown has no {k}" for k in ("id", "validations") if k in field]
            continue
        field_id, label = field.get("id"), str(attributes.get("label") or "").strip()
        if not isinstance(field_id, str) or not FIELD_ID.match(field_id):
            problems.append(f"{where}: the id {field_id!r} is not letters, digits, - and _")
        elif field_id in ids:
            problems.append(f"{where}: the id {field_id} is not unique")
        ids.add(field_id)
        if not label:
            problems.append(f"{where}: no label")
        elif label in labels:
            problems.append(f"{where}: the label {label} is not unique")
        labels.add(label)
        validations = field.get("validations") or {}
        if set(validations) - {"required"} or not isinstance(validations.get("required", False), bool):
            problems.append(f"{where}: the validations hold more than the flag required")
        problems += option_problems(where, kind, attributes.get("options"))
    return problems


def option_problems(where, kind, options):
    """The problems of the options of a dropdown or of a group of check boxes."""
    if kind == "dropdown":
        if not options or not all(isinstance(o, str) and o.strip() for o in options):
            return [f"{where}: a dropdown needs options that are not empty texts"]
        if len(set(options)) != len(options):
            return [f"{where}: the options are not unique"]
    elif kind == "checkboxes":
        if not options or not all(isinstance(o, dict) and str(o.get("label") or "").strip() for o in options):
            return [f"{where}: each check box needs a label"]
        if any(set(o) - {"label", "required"} or not isinstance(o.get("required", False), bool) for o in options):
            return [f"{where}: a check box has an unknown key or a required flag that is not true or false"]
    elif options is not None:
        return [f"{where}: a {kind} has no options"]
    return []


def texts(value):
    """Each text in a parsed YAML value."""
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from texts(item)
    elif isinstance(value, list):
        for item in value:
            yield from texts(item)


class FormCheckTests(unittest.TestCase):
    """The checks of a form find each problem that GitHub refuses. These tests need no PyYAML."""

    def test_the_checks_find_the_problems_that_github_refuses(self):
        def field(kind="input", field_id="a", **attributes):
            return {"type": kind, "id": field_id, "attributes": {"label": "A", **attributes}}

        def problems(*body, **form):
            return form_problems({"name": "N", "description": "D", "body": list(body), **form})

        self.assertEqual([], problems(field()))
        self.assertEqual(["unknown key label"], problems(field(), label=["bug"]))
        self.assertEqual(["the body has no field"], problems({"type": "markdown", "attributes": {"value": "x"}}))
        self.assertEqual(["body[1]: the id a is not unique", "body[1]: the label A is not unique"],
                         problems(field(), field()))
        self.assertEqual(["body[0]: no label"], problems({"type": "textarea", "id": "a", "attributes": {}}))
        self.assertEqual(["body[0]: the id 'a b' is not letters, digits, - and _"], problems(field(field_id="a b")))
        self.assertEqual(["body[0]: unknown type checkbox"], problems({"type": "checkbox"}))
        self.assertEqual(["body[0]: a dropdown needs options that are not empty texts"],
                         problems(field("dropdown", options=[1, 2])))
        self.assertEqual(["body[0]: the options are not unique"], problems(field("dropdown", options=["x", "x"])))
        self.assertEqual(["body[0]: each check box needs a label"],
                         problems(field("checkboxes", options=[{"required": True}])))
        self.assertEqual(["body[0]: unknown attribute render"], problems(field(render="text")))
        self.assertEqual(["body[0]: the validations hold more than the flag required"],
                         problems({**field(), "validations": {"required": "yes"}}))


@unittest.skipIf(yaml is None, NO_YAML)
class IssueFormTests(unittest.TestCase):
    """The three forms and the config of the issue chooser."""

    @staticmethod
    def load(name):
        with open(os.path.join(FORMS, name), encoding="utf-8") as f:
            return yaml.safe_load(f)

    def fields(self, name):
        """The fields of a form by their id."""
        return {f["id"]: f for f in self.load(name)["body"] if f["type"] != "markdown"}

    def test_the_folder_holds_the_three_forms_and_the_config(self):
        """A new form needs its labels and its checks in this test."""
        self.assertEqual(set(FORM_LABELS) | {CONFIG}, set(os.listdir(FORMS)))

    def test_each_form_is_valid_and_gives_its_labels(self):
        for name, labels in FORM_LABELS.items():
            with self.subTest(name):
                form = self.load(name)
                self.assertEqual([], form_problems(form))
                self.assertEqual(labels, form["labels"])

    def test_the_bug_report_asks_for_the_build_the_game_the_steps_and_the_logs(self):
        fields = self.fields("bug_report.yml")
        self.assertEqual(["version", "game", "steps", "expected", "actual", "logs", "no-game-files"], list(fields))
        for field_id in ("version", "game", "steps", "expected", "actual"):
            self.assertTrue(fields[field_id]["validations"]["required"], field_id)
        version = fields["version"]["attributes"]["description"]
        self.assertIn("Help > About OpenLancer", version)
        logs = fields["logs"]["attributes"]["description"]
        for text in ("paths of your folders", "Windows user name", ".log.previous.txt"):
            self.assertIn(text, logs)
        # The public preview has no Help menu, so each field also tells a user of such a build what to do.
        self.assertIn("A build without a Help menu: write the version from the name of its zip file.", version)
        self.assertIn("A build without a Help menu: README-FIRST.txt names the log folder.", logs)

    def test_the_bug_report_needs_the_box_that_no_game_files_are_attached(self):
        box = self.fields("bug_report.yml")["no-game-files"]
        self.assertEqual("checkboxes", box["type"])
        self.assertEqual([{"label": "I did not attach game files.", "required": True}], box["attributes"]["options"])

    def test_the_play_test_finding_has_the_round_the_area_and_the_severity(self):
        fields = self.fields("playtest_finding.yml")
        self.assertEqual(["round", "version", "area", "step", "happened", "expected", "screenshot", "console",
                          "severity"], list(fields))
        options = {i: f["attributes"].get("options") for i, f in fields.items() if f["type"] == "dropdown"}
        self.assertEqual({"round": ["1", "2", "3"], "area": AREAS, "severity": ["Blocker", "Major", "Minor", "Idea"]},
                         options)
        for field_id in ("round", "version", "area", "step", "happened", "expected", "severity"):
            self.assertTrue(fields[field_id]["validations"]["required"], field_id)

    def test_the_severity_gives_one_line_to_each_option_and_minor_holds_the_rest(self):
        """The triage of the 1.0 scope defines blocker, major and idea. Minor holds all other problems."""
        severity = self.fields("playtest_finding.yml")["severity"]["attributes"]
        lines = severity["description"].splitlines()
        self.assertEqual([f"- **{o}**" for o in severity["options"]], [line.split(":")[0] for line in lines])
        [minor] = [line for line in lines if line.startswith("- **Minor**")]
        self.assertIn("a problem that is not a blocker or a major problem", minor)

    def test_the_chooser_sends_help_questions_to_the_setup_help_forum(self):
        config = self.load(CONFIG)
        self.assertEqual({"blank_issues_enabled", "contact_links"}, set(config))
        self.assertIs(False, config["blank_issues_enabled"])
        [link] = config["contact_links"]
        self.assertEqual({"name", "url", "about"}, set(link))
        self.assertEqual(SETUP_HELP, link["url"])
        self.assertIn(INVITE, link["about"])

    def test_the_forms_name_no_internal_id_and_no_folder(self):
        """The forms are texts for players. They give no internal ID of the project and no folder of a machine."""
        pattern = internal_ids(REPO)
        for name in [*FORM_LABELS, CONFIG]:
            for text in texts(self.load(name)):
                with self.subTest(name, text=text):
                    self.assertIsNone(pattern.search(text))
                    self.assertIsNone(re.search(r"(?<![\w$])[A-Za-z]:[\\/]", text))


if __name__ == "__main__":
    unittest.main()
