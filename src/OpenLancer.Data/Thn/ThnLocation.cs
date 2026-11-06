using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The place in a cutscene that a problem is about. A click on the problem in the Console opens the cutscene and
/// selects the element (section 9 of the M8 design).
/// </summary>
/// <param name="Path">The install path of the cutscene.</param>
/// <param name="Kind">The kind of the element, or null for a problem of the whole script.</param>
/// <param name="Index">The place of the element in its list in the file that the check read, from 0.</param>
/// <param name="Value">The path of the value in the element, for example <c>cameraprops.fovh</c>, or null.</param>
/// <param name="Key">The key of the element in that file, as a THN patch writes it in <c>ol_match</c>: the name text of
/// an entity, for example <c>"Cam_02"</c>, or the list <c>{ time, type, { targets } }</c> of an event. Null when the
/// element has no key.</param>
public sealed record ThnLocation(string Path, ThnElementKind? Kind = null, int Index = 0, string? Value = null,
    string? Key = null)
{
    /// <summary>
    /// The element of a script that this place names. The check reads the saved file, and the result comes some seconds
    /// after the save. An edit in that time can add or delete an element before the element of the problem. So the
    /// element at the index is the element only when it has the key. Else the one element with the key is the element.
    /// </summary>
    /// <returns>The element, or null for a problem of the whole script, and when no element or more than one element
    /// has the key.</returns>
    public ThnElement? ElementIn(ThnScriptModel script)
    {
        if (Kind is not { } kind)
            return null;
        var elements = script.Elements(kind);
        if (Index >= 0 && Index < elements.Count && KeyOf(kind, elements[Index].Table) == Key)
            return elements[Index];
        if (Key == null)
            return null;
        return elements.Where(e => KeyOf(kind, e.Table) == Key).Take(2).ToList() is [var one] ? one : null;
    }

    /// <summary>
    /// The place of an element in a version of W of its cutscene, for example W after an agent edit: its kind, its index
    /// in that version and its key. <see cref="ElementIn"/> finds the element in that version, and in a later version
    /// by its key.
    /// </summary>
    /// <param name="script">The version of W, or null when the cutscene does not exist in it.</param>
    /// <returns>The place of the element, or the place of the whole script when the version does not have the
    /// element.</returns>
    public static ThnLocation Of(ThnScriptModel? script, ThnRef element)
    {
        if (script?.Element(element.Kind, element.Id) is not { } found)
            return new ThnLocation(element.Path);
        return new ThnLocation(element.Path, element.Kind, script.Elements(element.Kind).IndexOf(found), null,
            KeyOf(element.Kind, found.Table));
    }

    /// <summary>The key text of an element (see <see cref="Key"/>), or null when it has no key.</summary>
    internal static string? KeyOf(ThnElementKind kind, ThnTable element) =>
        ThnKeys.Match(kind, element) is { } match ? ThnKeys.Text(match) : null;
}
