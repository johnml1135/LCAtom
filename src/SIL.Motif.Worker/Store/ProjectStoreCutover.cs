using Microsoft.Data.Sqlite;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>
/// Moves one CLI store location into the worker-owned project database as a single event.
/// </summary>
/// <remarks>
/// <para>
/// Both legacy sources import inside one transaction the cutover owns, so the destination is never left
/// holding one source without the other. Per-importer transactions could not give that: a failure importing
/// the second source would leave the first committed, and a caller that then routed to the worker would read
/// a store that is half old and half new.
/// </para>
/// <para>
/// Archival happens only after that commit, and only after the legacy bulk source is detached, because an
/// attached source cannot be moved -- pinned by `AnAttachedSourceCannotBeMovedUntilItIsDetached`. A failed
/// archival is cleanup debt rather than a failed cutover: the
/// migration ledger already records what was taken, so a retry re-archives without re-importing, and the
/// destination is authoritative either way.
/// </para>
/// <para>
/// Archival is also ordered innermost-first. The CLI's bulk database lives at <c>&lt;store&gt;/motif.db</c>,
/// inside the very directory the file Proposal store archives, so moving the container first would carry the
/// nested source away unverified and leave its own archival looking at a path that no longer exists.
/// </para>
/// </remarks>
public static class ProjectStoreCutover
{
    /// <summary>The migration-ledger discriminator recording that one store location was cut over.</summary>
    public const string CutoverKind = "store-cutover";

    /// <summary>
    /// Imports every legacy source present at <paramref name="storeDirectory"/> and records the cutover.
    /// </summary>
    /// <param name="storeDirectory">The exact store location the caller selected, typically <c>.motif</c>.</param>
    /// <param name="destination">The worker-owned project database that becomes authoritative.</param>
    /// <param name="beforeCommit">Runs inside the transaction, immediately before it commits.</param>
    /// <param name="beforeArchive">Runs after the commit, before the first source is moved aside.</param>
    /// <param name="onBoundary">Receives each destination table an importer writes, as it writes it.</param>
    /// <exception cref="InvalidOperationException">The store directory is the destination database.</exception>
    public static ProjectStoreCutoverResult Run(
        string storeDirectory,
        MotifDatabase destination,
        Action? beforeCommit = null,
        Action? beforeArchive = null,
        Action<string>? onBoundary = null)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("A store directory is required.", nameof(storeDirectory));
        ArgumentNullException.ThrowIfNull(destination);
        var storePath = Path.GetFullPath(storeDirectory);
        var bulkPath = Path.Combine(storePath, LegacyBulkFileName);

        using var connection = destination.OpenConnection();
        var attachedBulk = File.Exists(bulkPath)
            ? LegacyBulkStoreMigration.Attach(bulkPath, connection, destination.FullPath, out _)
            : null;

        var pending = new List<PendingSourceArchive>();
        ProposalMigrationResult? proposals;
        LegacyMigrationResult? bulk;
        try
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                proposals = null;
                if (Directory.Exists(storePath))
                {
                    proposals = FileProposalStoreMigration.Import(new LegacyProposalStoreLayout(storePath),
                        connection, transaction, destination.FullPath, out var proposalArchive, onBoundary);
                    if (proposalArchive is not null)
                        pending.Add(proposalArchive);
                }

                bulk = null;
                if (attachedBulk is not null)
                {
                    bulk = LegacyBulkStoreMigration.Import(attachedBulk, connection, transaction,
                        out var bulkArchive, onBoundary);
                    if (bulkArchive is not null)
                        pending.Add(bulkArchive);
                }

                FileProposalStoreMigration.AddLedger(connection, transaction, CutoverKind, storePath,
                    CutoverDigest(proposals, bulk));
                beforeCommit?.Invoke();
                transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); }
                catch (Exception exception) when (exception is SqliteException or InvalidOperationException) { }
                throw;
            }
        }
        finally
        {
            if (attachedBulk is not null)
                LegacyBulkStoreMigration.Detach(connection);
        }

        if (pending.Count > 0)
            beforeArchive?.Invoke();
        var archived = new List<string>();
        var failures = new List<SourceArchiveFailure>();
        foreach (var source in ContainedFirst(pending))
        {
            try
            {
                source.Archive();
                archived.Add(source.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                failures.Add(new SourceArchiveFailure(source.Kind, source.Path, exception.Message));
            }
        }
        return new ProjectStoreCutoverResult(storePath, proposals, bulk, archived, failures);
    }

    /// <summary>The legacy bulk database filename inside a CLI store directory.</summary>
    internal const string LegacyBulkFileName = "motif.db";

    /// A contained path is always the longer one, which is all this ordering needs to know.
    private static IEnumerable<PendingSourceArchive> ContainedFirst(IEnumerable<PendingSourceArchive> pending) =>
        pending.OrderByDescending(source => source.Path.Length);

    private static string CutoverDigest(ProposalMigrationResult? proposals, LegacyMigrationResult? bulk) =>
        (proposals?.SourceDigest ?? "none") + "+" + (bulk?.SourceDigest ?? "none");
}

/// <summary>Reports one source that was imported but could not be moved aside.</summary>
/// <remarks>
/// A failure here does not undo the cutover. The destination already holds the source's rows and the ledger
/// already records it, so the only outstanding work is moving a file that nothing reads any more.
/// </remarks>
public sealed record SourceArchiveFailure(string Kind, string Path, string Reason);

/// <summary>Summarizes one store cutover.</summary>
public sealed record ProjectStoreCutoverResult(
    string StoreDirectory,
    ProposalMigrationResult? FileProposals,
    LegacyMigrationResult? LegacyBulk,
    IReadOnlyList<string> ArchivedPaths,
    IReadOnlyList<SourceArchiveFailure> ArchiveFailures)
{
    /// <summary>Whether every imported source was also moved aside.</summary>
    public bool ArchivalComplete => ArchiveFailures.Count == 0;
}
