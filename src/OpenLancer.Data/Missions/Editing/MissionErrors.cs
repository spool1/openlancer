using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The detail codes of the mission commands with their error kinds and their messages (section 4.7 of the M6b design).
/// Each message uses the strict form of Simplified Technical English: what is wrong, why the editor or the game cannot
/// use it, and the fix. The studio and the agent tools show the same text.
/// </summary>
/// <remarks>The codes after <see cref="UnknownCommand"/> are not in the table of the design. The commands need them
/// for refusals that the table of section 4.9 names without a code.</remarks>
public static class MissionErrors
{
    public const string NotEditable = "NotEditable";
    public const string Busy = "Busy";
    public const string ScriptChanged = "ScriptChanged";
    public const string ScriptNotFound = "ScriptNotFound";
    public const string TriggerNotFound = "TriggerNotFound";
    public const string ItemNotFound = "ItemNotFound";
    public const string RowNotFound = "RowNotFound";
    public const string RowChanged = "RowChanged";
    public const string ReadOnlySection = "ReadOnlySection";
    public const string RepeatedNickname = "RepeatedNickname";
    public const string NameInvalid = "NameInvalid";
    public const string NameTaken = "NameTaken";
    public const string NameIdTaken = "NameIdTaken";
    public const string UnknownKey = "UnknownKey";
    public const string WrongPlace = "WrongPlace";
    public const string NotAKey = "NotAKey";
    public const string ValueKind = "ValueKind";
    public const string ValueMissing = "ValueMissing";
    public const string WordNotInSet = "WordNotInSet";
    public const string WholeNumber = "WholeNumber";
    public const string PatchText = "PatchText";
    public const string RequiredBy = "RequiredBy";
    public const string NoShipBuildFile = "NoShipBuildFile";
    public const string NewErrors = "NewErrors";
    public const string NewWarnings = "NewWarnings";
    public const string SectionChanged = "SectionChanged";
    public const string KeyLine = "KeyLine";
    public const string SharedBuildRename = "SharedBuildRename";
    public const string SharedBuildChange = "SharedBuildChange";
    public const string PrefabMember = "PrefabMember";
    public const string BuiltInPrefab = "BuiltInPrefab";
    public const string LowerModPrefab = "LowerModPrefab";
    public const string NoOverride = "NoOverride";
    public const string FieldNotWritable = "FieldNotWritable";
    public const string UnknownCommand = "UnknownCommand";

    // Codes that the table of the design does not have.
    public const string NoSuchValue = "NoSuchValue";
    public const string NoCondition = "NoCondition";
    public const string BadPlace = "BadPlace";
    public const string NotAnAction = "NotAnAction";
    public const string LinkNotFound = "LinkNotFound";
    public const string NotALink = "NotALink";
    public const string NotInBase = "NotInBase";
    public const string NoBaseDifference = "NoBaseDifference";
    public const string NotShipBuildFile = "NotShipBuildFile";
    public const string NotWholeFile = "NotWholeFile";
    public const string PathInUse = "PathInUse";
    public const string NotTextId = "NotTextId";
    public const string MissingLines = "MissingLines";
    public const string NotMovable = "NotMovable";
    public const string NotAnItem = "NotAnItem";
    public const string NotADirective = "NotADirective";
    public const string BadJson = "BadJson";
    public const string UnknownField = "UnknownField";
    public const string ReplacesLowerFile = "ReplacesLowerFile";
    public const string WorldFile = "WorldFile";
    public const string BadPath = "BadPath";
    public const string FixNotFound = "FixNotFound";

    // Codes of the behavior prefabs that the table of the design does not have (milestone M6b-3).
    public const string FieldValue = "FieldValue";
    public const string TemplateLine = "TemplateLine";
    public const string PrefabNotFound = "PrefabNotFound";
    public const string PrefabProblems = "PrefabProblems";
    public const string InstanceNotFound = "InstanceNotFound";
    public const string FieldNotFound = "FieldNotFound";
    public const string PortNotFound = "PortNotFound";
    public const string RoleNotFound = "RoleNotFound";
    public const string PrefabInUse = "PrefabInUse";
    public const string FieldInUse = "FieldInUse";
    public const string NotATemplate = "NotATemplate";
    public const string PrefabInPrefab = "PrefabInPrefab";

    // Codes of the cutscene commands that the table of the design does not have (milestone MCP1-4).
    public const string TargetType = "TargetType";

    /// <summary>An event gets another number of targets than its type takes.</summary>
    public const string TargetCount = "TargetCount";

    /// <summary>An entity of a type without a place, for example a MONITOR, gets a position or a rotation.</summary>
    public const string NoPlace = "NoPlace";

    /// <summary>A script that has a SCENE entity gets another one.</summary>
    public const string OneScene = "OneScene";

    internal static EditError NotEditableError(string reason) => new(EditErrorKind.ReadOnly, NotEditable, NotEditableMessage(reason));

    /// <summary>The text of <see cref="NotEditable"/>. The Mission tab and the mission pages show it as their read-only
    /// note (section 8.11 of the M6b design).</summary>
    public static string NotEditableMessage(string reason) => $"The editor cannot change missions now. {reason}";

    internal static EditError BusyError() =>
        new(EditErrorKind.Busy, Busy, "A drag or a text edit is not complete. Complete it, then try again.");

    internal static EditError ScriptChangedError(string script) =>
        new(EditErrorKind.StaleRevision, ScriptChanged,
            $"Script {script} changed after you read it. Read the script again, then try again.") { Target = Script(script) };

    /// <summary>A script reference names the ship build file of a script (final finding 19 of MCP1-3). The code stays
    /// the code of a script that does not exist, and the text names the tools that read the file.</summary>
    internal static EditError ShipBuildFileNotScriptError(string path) => ScriptNotFoundError(path) with
    {
        Message = $"{path} is the ship build file of a script, not a mission script. Read a build with get_item and its " +
                  "sec: reference, or read the file with get_raw_ini.",
    };

    internal static EditError ScriptNotFoundError(string script) =>
        new(EditErrorKind.NotFound, ScriptNotFound,
            $"The game data and the mods have no mission script {script}. Pick a script from the Missions tab.")
        {
            Target = Script(script),
        };

    internal static EditError TriggerNotFoundError(string script, string name, string hint = "") =>
        new(EditErrorKind.NotFound, TriggerNotFound,
            $"Script {script} has no trigger '{name}'. Pick a trigger of this script.{hint}");

    internal static EditError ItemNotFoundError(string script, string kind, string name, string hint = "") =>
        new(EditErrorKind.NotFound, ItemNotFound,
            $"Script {script} has no {kind} '{name}'. Pick {MissionChecks.WithArticle(kind)} of this script.{hint}");

    internal static EditError RowNotFoundError(string owner, int row) =>
        new(EditErrorKind.NotFound, RowNotFound,
            $"{owner} has no line {Number(row)}. Read the item again, then try again.");

    internal static EditError RowChangedError(string owner, int row, string expected) =>
        new(EditErrorKind.StaleRevision, RowChanged,
            $"Line {Number(row)} of {owner} is not '{expected}' now. The script changed after you read it. Read the item " +
            "again, then try again.");

    internal static EditError ReadOnlySectionError(string section, string reason) =>
        new(EditErrorKind.Refused, ReadOnlySection, $"The editor cannot change {section}. {reason}");

    internal static EditError RepeatedNicknameError(string type, string name) =>
        new(EditErrorKind.Refused, RepeatedNickname,
            $"Two [{type}] sections have the nickname '{name}'. A patch cannot find one of them. Change the script in a " +
            "whole file of the mod.");

    internal static EditError NameInvalidError(string name, int max) =>
        new(EditErrorKind.InvalidValue, NameInvalid,
            $"'{name}' is not a valid name. Use letters, digits, '_' and '-', with at most {Number(max)} characters. A " +
            "name cannot be a number.");

    internal static EditError NameTakenError(string script, string kind, string name) =>
        new(EditErrorKind.NameTaken, NameTaken, $"Script {script} already has {MissionChecks.WithArticle(kind)} '{name}'. " +
                                                "Pick another name.");

    /// <summary>A fix of section 8.7 of the M6b design that does not apply to W now.</summary>
    internal static EditError FixNotFoundError(string script) =>
        new(EditErrorKind.Refused, FixNotFound,
            $"This fix does not apply to {script} now. The problem or the step changed. Read the problems again, then try " +
            "again.");

    internal static EditError NameIdTakenError(string name, string other) =>
        new(EditErrorKind.NameTaken, NameIdTaken,
            $"The game gives '{name}' the same id as trigger '{other}'. The game then keeps only the first of the two " +
            "triggers. Pick another name.");

    internal static EditError UnknownKeyError(string key) =>
        new(EditErrorKind.InvalidValue, UnknownKey,
            $"'{key}' is not an action or a condition of the game. Pick one from the list.");

    internal static EditError WrongPlaceError(string key, bool isCondition) =>
        new(EditErrorKind.InvalidValue, WrongPlace, isCondition
            ? $"{key} is a condition. A trigger has one condition. Use \"Set condition\"."
            : $"{key} is an action. Use \"Add action\".");

    internal static EditError NotAKeyError(string key, string type) =>
        new(EditErrorKind.InvalidValue, NotAKey, $"The game does not read '{key}' in a {type} section. Pick a field of the page.");

    internal static EditError ValueKindError(string value, string kind, string label, string what) =>
        new(EditErrorKind.InvalidValue, ValueKind, $"'{value}' is not {kind}. {label} needs {what}.");

    internal static EditError ValueMissingError(string label) =>
        new(EditErrorKind.InvalidValue, ValueMissing,
            $"{label} needs a value, because a later value is set. Give {label} a value.");

    internal static EditError WordNotInSetError(string value, string label, IEnumerable<string> words) =>
        new(EditErrorKind.InvalidValue, WordNotInSet,
            $"'{value}' is not a word of {label}. Use one of these words: {string.Join(", ", words)}.");

    internal static EditError WholeNumberError(string label) =>
        new(EditErrorKind.InvalidValue, WholeNumber, $"The game reads only the whole part of {label}. Use a whole number.");

    internal static EditError PatchTextError() =>
        new(EditErrorKind.InvalidValue, PatchText,
            "A value cannot hold a comma or a semicolon. The game splits or cuts the line there. Remove the character.");

    internal static EditError RequiredByError(string kind, string name, int count, string users) =>
        new(EditErrorKind.Refused, RequiredBy,
            $"{Capital(kind)} '{name}' is in use by {Number(count)} {users}. Remove them or change them first.");

    internal static EditError NoShipBuildFileError() =>
        new(EditErrorKind.Refused, NoShipBuildFile,
            "The mission has no ship build file. Set \"Ship build file\" on the Mission page first.");

    /// <summary>Strict mode refuses a batch (section 4.0.5 of the M6b design). With <c>RejectNewWarnings</c>, the new
    /// warnings count too. The code is <c>new_errors</c> for both. The detail is <see cref="NewWarnings"/> when only
    /// new warnings refuse, so a caller can tell a refused warning from an error.</summary>
    internal static EditError NewErrorsError(int errors, int warnings, string first)
    {
        static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{Number(n)} {word}s";
        var what = errors > 0 && warnings > 0 ? $"{Count(errors, "error")} and {Count(warnings, "warning")}"
            : warnings > 0 ? Count(warnings, "warning")
            : Count(errors, "error");
        return new(EditErrorKind.NewErrors, errors == 0 && warnings > 0 ? NewWarnings : NewErrors,
            $"The edit adds {what}, so the editor did not apply it. {first}");
    }

    internal static EditError SectionChangedError(string section) =>
        new(EditErrorKind.StaleRevision, SectionChanged,
            $"{section} changed after the edit that this inverse reverts. Read the item again, then change it back with " +
            "the mission commands.");

    internal static EditError KeyLineError(string key) =>
        new(EditErrorKind.InvalidArgument, KeyLine, $"{key} is the name of the item. Use Rename to change it.");

    internal static EditError SharedBuildRenameError(string name) =>
        new(EditErrorKind.Refused, SharedBuildRename,
            $"Ship build '{name}' is in a shared file that random encounters can use. The editor cannot rename it in a " +
            "mission.");

    internal static EditError SharedBuildChangeError(string name) =>
        new(EditErrorKind.NeedsConfirmation, SharedBuildChange,
            $"Ship build '{name}' is in a shared file. Random encounters can use it, so this change can change them too. " +
            "Use \"This ship only\", or confirm the change.");

    internal static EditError PrefabMemberError(string owner, string instance) =>
        new(EditErrorKind.Refused, PrefabMember,
            $"{owner} is a part of prefab instance '{instance}'. Change a field of the instance, open the prefab, or " +
            "unpack the instance first.");

    internal static EditError BuiltInPrefabError(string prefab) =>
        new(EditErrorKind.Refused, BuiltInPrefab,
            $"'{prefab}' is a built-in prefab, so it cannot change. Make a variant of it in the mod, then apply to the " +
            "variant.");

    internal static EditError LowerModPrefabError(string prefab, string mod) =>
        new(EditErrorKind.Refused, LowerModPrefab,
            $"Prefab '{prefab}' belongs to mod '{mod}'. Only a prefab of the active mod can change.");

    internal static EditError NoOverrideError(string field, string instance) =>
        new(EditErrorKind.InvalidArgument, NoOverride, $"Field {field} of '{instance}' has no override.");

    internal static EditError FieldNotWritableError(string field) =>
        new(EditErrorKind.Refused, FieldNotWritable, $"{field} shows the triggers of the prefab. Change it in the graph.");

    internal static EditError FieldValueError(string label, string problem) =>
        new(EditErrorKind.InvalidValue, FieldValue, $"The value of field '{label}' is not valid. {problem}");

    internal static EditError TemplateLineError(string role, string line, string problem) =>
        new(EditErrorKind.InvalidValue, TemplateLine,
            $"The prefab gives section '{role}' the line '{line}'. {problem} Change the field or the prefab.");

    /// <summary>A new file name of a prefab that a prefab of the library has. A prefab of the active mod with the id of a
    /// lower mod hides that prefab, and its instances then use the new prefab.</summary>
    internal static EditError PrefabIdTakenError(string id, string? mod, bool active) =>
        new(EditErrorKind.NameTaken, NameTaken, active || mod == null
            ? $"The active mod already has a prefab '{id}'. Pick another file name."
            : $"Mod '{mod}' already has a prefab '{id}'. A prefab of the active mod with this file name hides it, and its " +
              "instances then use the new prefab. Pick another file name.");

    /// <summary>A file name of a new prefab that is not valid (see <see cref="Prefabs.PrefabAsset.FileNameProblem"/>).</summary>
    internal static EditError PrefabFileNameError(string problem) => new(EditErrorKind.InvalidValue, NameInvalid, problem);

    internal static EditError PrefabNotFoundError(string prefab, string hint = "") =>
        new(EditErrorKind.NotFound, PrefabNotFound,
            $"No mod has prefab '{prefab}'. Pick a prefab of the library.{hint}");

    internal static EditError PrefabProblemsError(string prefab, int count, string first) =>
        new(EditErrorKind.Refused, PrefabProblems, count == 1
            ? $"Prefab '{prefab}' has 1 problem in its file, so the editor cannot use it. {first}"
            : $"Prefab '{prefab}' has {Number(count)} problems in its file, so the editor cannot use it. {first}");

    internal static EditError InstanceNotFoundError(string script, string id, string hint = "") =>
        new(EditErrorKind.NotFound, InstanceNotFound,
            $"Script {script} has no prefab instance '{id}'. Read the script, then pick an instance.{hint}");

    internal static EditError FieldNotFoundError(string prefab, string field, IEnumerable<string> fields) =>
        new(EditErrorKind.InvalidArgument, FieldNotFound,
            $"Prefab '{prefab}' has no field '{field}'. Use one of these fields: {string.Join(", ", fields)}.");

    internal static EditError PortNotFoundError(string prefab, string port, IEnumerable<string> ports) =>
        new(EditErrorKind.InvalidArgument, PortNotFound,
            $"Prefab '{prefab}' has no port '{port}'. Use one of these ports: {string.Join(", ", ports)}.");

    internal static EditError RoleNotFoundError(string prefab, string role, IEnumerable<string> roles) =>
        new(EditErrorKind.InvalidArgument, RoleNotFound,
            $"Prefab '{prefab}' has no section with the role '{role}'. Use one of these roles: {string.Join(", ", roles)}.");

    internal static EditError PrefabInUseError(string prefab, int count) =>
        new(EditErrorKind.NeedsConfirmation, PrefabInUse, count == 1
            ? $"Prefab '{prefab}' has 1 instance in the active mod. The instance keeps its lines and shows \"Missing " +
              "prefab\". Confirm the delete, or keep the prefab."
            : $"Prefab '{prefab}' has {Number(count)} instances in the active mod. The instances keep their lines and " +
              "show \"Missing prefab\". Confirm the delete, or keep the prefab.");

    internal static EditError FieldInUseError(string label, int count) =>
        new(EditErrorKind.Refused, FieldInUse, count == 1
            ? $"Field '{label}' is in 1 line of the prefab. Make the line a fixed value first."
            : $"Field '{label}' is in {Number(count)} lines of the prefab. Make each line a fixed value first.");

    internal static EditError NotATemplateError(string what) =>
        new(EditErrorKind.InvalidArgument, NotATemplate, $"{what} Open the prefab, then try again.");

    /// <summary>A prefab instance in the template of a prefab. The library of prefab mode does not show the prefabs for
    /// this reason.</summary>
    internal static EditError PrefabInPrefabError(string prefab) =>
        new(EditErrorKind.Refused, PrefabInPrefab,
            $"{prefab} is a prefab. A prefab cannot hold a prefab instance. Place the prefab in a mission script.");

    internal static EditError UnknownCommandError(string kind) =>
        new(EditErrorKind.InvalidArgument, UnknownCommand, $"'{kind}' is not a mission command.");

    internal static EditError BadJsonError(string reason) =>
        new(EditErrorKind.InvalidArgument, BadJson, $"The request is not a valid mission batch. {reason} Correct the JSON.");

    internal static EditError UnknownBatchFieldError(string field, bool options) =>
        new(EditErrorKind.InvalidArgument, UnknownField,
            $"{(options ? "The options of the batch have" : "The batch has")} no field '{field}'. Remove the field, or " +
            "correct its name.") { Field = field };

    internal static EditError UnknownFieldError(string kind, string field) =>
        new(EditErrorKind.InvalidArgument, UnknownField,
            $"Command '{kind}' has no field '{field}'. Remove the field, or correct its name.");

    internal static EditError NoSuchValueError(string key, string name, IEnumerable<string> names) =>
        new(EditErrorKind.InvalidArgument, NoSuchValue,
            $"{key} has no value '{name}'. Use one of these values: {string.Join(", ", names)}.");

    internal static EditError NoConditionError(string trigger) =>
        new(EditErrorKind.NotFound, NoCondition,
            $"Trigger '{trigger}' has no condition. Use \"Set condition\" first.");

    internal static EditError BadPlaceError() =>
        new(EditErrorKind.InvalidArgument, BadPlace,
            "An action cannot go before the nickname line or before the condition, because the game ignores it there. " +
            "Pick a place after the condition.");

    internal static EditError NotAnActionError(string owner, int row) =>
        new(EditErrorKind.InvalidArgument, NotAnAction,
            $"Line {Number(row)} of {owner} is not an action. Pick an action line.");

    internal static EditError LinkNotFoundError(string source, string target, string kind) =>
        new(EditErrorKind.NotFound, LinkNotFound,
            $"Trigger '{source}' has no {kind} link to '{target}'. Read the trigger again, then try again.");

    internal static EditError NotALinkError(string owner, int row) =>
        new(EditErrorKind.InvalidArgument, NotALink,
            $"Line {Number(row)} of {owner} names no trigger. Pick a line that starts, stops or watches a trigger.");

    internal static EditError NotInBaseError(string section) =>
        new(EditErrorKind.Refused, NotInBase,
            $"The active mod adds {section}, so the game data has no lines for it. Remove the section instead.");

    internal static EditError NoBaseDifferenceError(string section) =>
        new(EditErrorKind.Refused, NoBaseDifference,
            $"The base of {section} did not change after the edit. Nothing needs a decision.");

    internal static EditError NotWholeFileError(string script) =>
        new(EditErrorKind.Refused, NotWholeFile,
            $"Script {script} is not a whole file of the active mod. The editor cannot delete a script of the game data " +
            "or of another mod.");

    internal static EditError ReplacesLowerFileError(string path) =>
        new(EditErrorKind.Refused, ReplacesLowerFile,
            $"The active mod replaces {path} of the game data or of a lower mod with this file. Without this file, the " +
            "game uses that file again, so the editor cannot delete it. Change the lines of the file instead.");

    internal static EditError WorldFileError(string path) =>
        new(EditErrorKind.Refused, WorldFile,
            $"The mission commands cannot change {path}. Encounters and bases use the data of this file too. Change the " +
            "file in the Inspector.") { Target = Script(path) };

    /// <summary>A command points the <c>npc_ship_file</c> line of a script at a file that is not a ship build file
    /// (final finding 5 of MCP1-3).</summary>
    internal static EditError NotShipBuildFileError(string script, string file) =>
        new(EditErrorKind.InvalidValue, NotShipBuildFile,
            $"The npc_ship_file line of {script} cannot name {file}, because that file is not a ship build file. Other " +
            @"data of the game can use its sections. Name a file in a folder under DATA\MISSIONS outside the encounters " +
            "folder, with only [NPCShipArch] sections. Or name a new file.");

    /// <summary>A command puts a section other than <c>[NPCShipArch]</c> into the ship build file of a script (item 20
    /// of the user decisions of 2026-11-01 of MCP1-3). The code is the code of a bad <c>npc_ship_file</c> value, because
    /// the line of the script would then name a file that is not a ship build file.</summary>
    internal static EditError ShipBuildSectionError(string script, string file, string type) =>
        new(EditErrorKind.InvalidValue, NotShipBuildFile,
            $"{file} is the ship build file of {script}. A ship build file has only [NPCShipArch] sections, so it cannot " +
            $"get a [{type}] section. Put the section in another file.");

    internal static EditError PathInUseError(string path) =>
        new(EditErrorKind.NameTaken, PathInUse,
            $"The editor cannot make a script at {path}, because the edit view already has a file at this path. Pick " +
            "another path.");

    internal static EditError BadPathError(string path) =>
        new(EditErrorKind.InvalidArgument, BadPath,
            $"The editor cannot make a script at {path}. The game reads a mission script only from a folder under " +
            "DATA\\MISSIONS outside the encounters folder, with the extension .ini. Pick such a path.");

    /// <summary>The path of a new ship build file is not an install path of a file in a folder under
    /// <c>DATA\MISSIONS</c> (final finding 17 of MCP1-3).</summary>
    internal static EditError BadShipFilePathError(string path) =>
        new(EditErrorKind.InvalidArgument, BadPath,
            $"The editor cannot make a ship build file at {path}. Give the install path of a file in a folder under " +
            @"DATA\MISSIONS outside the encounters folder, with the extension .ini, for example " +
            @"DATA\MISSIONS\MY_MISSION\npcships.ini. The script then names it in its npc_ship_file line.");

    /// <summary>The name of a new ship build file of a script is empty or does not obey the name rule of New cutscene
    /// (#18).</summary>
    /// <param name="problem">The text of the name rule, for example <see cref="Thn.ThnEdits.BadName"/>.</param>
    internal static EditError ShipFileNameError(string problem) => new(EditErrorKind.InvalidValue, NameInvalid, problem);

    /// <summary>The text of an empty name of a new ship build file.</summary>
    public const string NoShipFileName = "Type a name for the ship build file.";

    /// <summary>The path of a new ship build file is not in the folder of its script (#18).</summary>
    internal static EditError ShipFileFolderError(string folder) =>
        new(EditErrorKind.InvalidArgument, BadPath,
            $@"A new ship build file goes into the folder of its script, with the extension .ini. Give a path such as " +
            $@"{folder}\npcships.ini.");

    /// <summary>A script directly in <c>DATA\MISSIONS</c> cannot get its own ship build file (#18), because a file
    /// directly in that folder is world data (review finding RB2).</summary>
    internal static EditError ShipFileOfScriptInMissionsError(string script) =>
        new(EditErrorKind.Refused, BadPath,
            $@"{script} is directly in DATA\MISSIONS. Encounters and bases use the files of that folder too, so this " +
            "script cannot get its own ship build file there. Pick a ship build file that exists.");

    /// <summary>The edit view or W has a file at the path of a new ship build file (#18).</summary>
    internal static EditError ShipFileInUseError(string path) =>
        new(EditErrorKind.NameTaken, PathInUse, $"A file exists at {path}. Use another name.");

    /// <summary>The most ship builds that <see cref="LostBuildsText"/> names.</summary>
    private const int LostBuildNames = 5;

    /// <summary>
    /// The warning of a change of the <c>npc_ship_file</c> line of a script after which the game does not find ship
    /// builds of the script (see <see cref="MissionEditor.LostBuilds"/>, review of #18). The field "Ship build file",
    /// its popup "New file..." and the result of a batch give it. The text names at most five builds.
    /// </summary>
    /// <param name="file">The file that the line names after the change, for example "this file", or null for no
    /// file.</param>
    /// <param name="old">The file that the line names before the change, which keeps the builds.</param>
    /// <param name="builds">The builds that the game does not find, at least one.</param>
    public static string LostBuildsText(string? file, string old, IReadOnlyList<string> builds)
    {
        var with = file != null ? $"With {file}" : "Without a ship build file";
        var names = string.Join(", ", builds.Take(LostBuildNames)) +
                    (builds.Count > LostBuildNames ? $" and {builds.Count - LostBuildNames} more" : "");
        return builds.Count == 1
            ? $"{with}, the game cannot find the ship build {names} of this mission. This build is only in {old}. " +
              "The game does not spawn the ships that use it."
            : $"{with}, the game cannot find {builds.Count} ship builds of this mission: {names}. These builds are only " +
              $"in {old}. The game does not spawn the ships that use them.";
    }

    internal static EditError NotTextIdError(string label) =>
        new(EditErrorKind.InvalidArgument, NotTextId,
            $"{label} is not a text id, so it cannot have text of the mod. Pick a value that is a text id.");

    internal static EditError MissingLinesError(int count) =>
        new(EditErrorKind.NeedsConfirmation, MissingLines, count == 1
            ? "After this edit, 1 line names an item that is gone. The game ignores such a line. Confirm the edit, or " +
              "keep the item."
            : $"After this edit, {Number(count)} lines name an item that is gone. The game ignores such lines. Confirm " +
              "the edit, or keep the item.");

    internal static EditError NotMovableError(string type) =>
        new(EditErrorKind.Refused, NotMovable,
            $"The place of a [{type}] section in the file has no effect in the game. Only a ship or a formation section " +
            "can move.");

    internal static EditError NotAnItemError(string reference, string what) =>
        new(EditErrorKind.InvalidArgument, NotAnItem, $"'{reference}' is not {what}. Pick {what}.");

    internal static EditError NotADirectiveError(string key) =>
        new(EditErrorKind.InvalidValue, NotADirective,
            $"'{key}' is not an order of the game. Pick an order from the list.");

    private static string Script(string script) => "msn:" + script;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
