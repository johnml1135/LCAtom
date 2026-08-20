using SIL.Motif.Cli;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>Authors both required rationale fields through the same CLI commands production callers use.</summary>
internal static class DraftRationale
{
    /// <summary>Authors both required rationale fields for a Proposal fixture.</summary>
    public static void Author(
        string storeDir,
        string draftName,
        string shortDescription,
        string extendedExplanation)
    {
        var label = Commands.Label(storeDir, draftName, shortDescription);
        if (label.ExitCode != 0) throw new InvalidOperationException(label.Output);

        var comment = Commands.Comment(storeDir, draftName, extendedExplanation);
        if (comment.ExitCode != 0) throw new InvalidOperationException(comment.Output);
    }
}
