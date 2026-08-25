namespace SIL.Motif.Worker.Store;

/// <summary>
/// One legacy source that has been imported and is waiting to be archived. Archival is separated from import
/// because it is the one step a transaction cannot undo: a source renamed before its transaction commits is
/// still renamed after that transaction rolls back, leaving a cutover that took nothing and moved everything.
/// Holding it here lets the caller perform every archival after its single commit, in the order it chooses.
/// </summary>
public sealed record PendingSourceArchive
{
    internal PendingSourceArchive(string kind, string path, string digest)
    {
        Kind = kind;
        Path = path;
        Digest = digest;
    }

    /// <summary>The source discriminator, matching the migration ledger's <c>SourceKind</c>.</summary>
    public string Kind { get; }

    /// <summary>The fully qualified path of the source to archive.</summary>
    public string Path { get; }

    /// <summary>The digest the source must still carry for archival to proceed.</summary>
    public string Digest { get; }

    /// <summary>
    /// Moves the source aside, refusing if it no longer matches <see cref="Digest"/>. The refusal matters:
    /// a source that changed after being imported holds edits the destination never took.
    /// </summary>
    public void Archive()
    {
        switch (Kind)
        {
            case FileProposalStoreMigration.FileProposalsKind:
                FileProposalStoreMigration.ArchiveFileProposals(Path, Digest);
                return;
            case LegacyBulkStoreMigration.LegacyBulkKind:
                LegacyBulkStoreMigration.ArchiveLegacyBulk(Path, Digest);
                return;
            default:
                throw new InvalidOperationException("Unknown legacy source kind.");
        }
    }
}
