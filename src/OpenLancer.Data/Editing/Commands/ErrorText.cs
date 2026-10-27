using System;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>
/// The text of an exception for a user (decision D130 of the M8b design). The data layer throws its refusals with the
/// name of the parameter, so that a stack trace names the argument. .NET then adds the text " (Parameter 'name')" to
/// the message, and for an <see cref="ArgumentOutOfRangeException"/> also a line with the actual value. A user does not
/// need that text.
/// </summary>
public static class ErrorText
{
    /// <summary>The message of an exception for a user, without the parameter text that .NET adds.</summary>
    public static string Of(Exception e)
    {
        var message = e.Message;
        if (e is not ArgumentException { ParamName: { Length: > 0 } name })
            return message;
        // The text that .NET adds, in the language of the runtime: the message of an exception with an empty text.
        var added = new ArgumentException(string.Empty, name).Message;
        var index = message.LastIndexOf(added, StringComparison.Ordinal);
        // The cut also removes the line of the actual value, which comes after the parameter text.
        return index > 0 ? message[..index] : message;
    }
}
