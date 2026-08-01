using System;

namespace SIL.Motif.Contract.Model;

/// <summary>
/// Helpers over an operation's <c>kind</c> string. The contract group is the leading segment of
/// <c>kind</c> up to (not including) the first <c>/</c>; it is what <c>contractVersions</c> keys
/// name. See docs/change-set-contract.md, "Document shape".
/// </summary>
public static class OperationKind
{
    public static string GetGroup(string kind)
    {
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("Operation kind must not be empty.", nameof(kind));

        var separatorIndex = kind.IndexOf('/');
        if (separatorIndex <= 0)
        {
            throw new FormatException(
                $"Operation kind '{kind}' must have the form 'group/...' (a non-empty group, a " +
                "'/', then the rest of the kind).");
        }

        return kind.Substring(0, separatorIndex);
    }
}
