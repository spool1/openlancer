using OpenLancer.Data.Editing.Bases;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private BaseFiles? bases;

    /// <summary>The base files and the room files of the edit view, from the bases of <c>universe.ini</c> (decision
    /// D135 of the M8b design), and the place of <c>mBases.ini</c>.</summary>
    internal BaseFiles Bases => bases ??= new BaseFiles(this, () => FreelancerIni);

    /// <summary>True when a <c>[Room]</c> of a base file of the edit view names the file. The editor knows the schema of
    /// a room file, so the Inspector edits its <c>[Room_Info]</c>, <c>[CharacterPlacement]</c> and
    /// <c>[PlayerShipPlacement]</c>.</summary>
    public bool IsRoomFile(string path) => Bases.IsRoomFile(path);

    /// <summary>True when a <c>[Base]</c> of W of <c>universe.ini</c> names the file. The editor knows the schema of a
    /// base file, so the Inspector edits its <c>[BaseInfo]</c> and its <c>[Room]</c> sections.</summary>
    public bool IsBaseFile(string path) => Bases.IsBaseFile(path);

    /// <summary>True for <c>mBases.ini</c>, the bar and mission data of the bases. The editor knows its schema, so the
    /// Inspector edits its <c>[MBase]</c> sections and their children.</summary>
    public bool IsMBasesFile(string path) => Bases.IsMBasesFile(path);

    /// <summary>True for <c>news.ini</c>, the news items of the bases.</summary>
    public bool IsNewsFile(string path) => Bases.IsNewsFile(path);

    /// <summary>
    /// The parts of a base as W gives them now: its <c>[Base]</c> entry, its base file, its rooms, its bar data, its
    /// market lines and the objects that dock with it. Null when no <c>[Base]</c> of W of <c>universe.ini</c> has the
    /// nickname.
    /// </summary>
    public BaseView? Base(string nickname) => BaseView.Of(this, nickname);
}
