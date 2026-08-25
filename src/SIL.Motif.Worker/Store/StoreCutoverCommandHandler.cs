using System.Text.Json;
using SIL.Motif.Contract.Store;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Projects;

namespace SIL.Motif.Worker.Store;

/// <summary>
/// Takes one CLI store location into the worker-owned database, under a lease that excludes every other user
/// of that project for the whole cutover.
/// </summary>
/// <remarks>
/// <para>
/// The exclusive lease is what makes the cutover a single visible event. It is writer-preferring, so once
/// this handler is waiting no new shared operation may start, and the cutover proceeds only after every
/// operation already running has finished. Anything submitted afterwards waits, then reads the committed
/// post-cutover state -- never a database mid-import.
/// </para>
/// <para>
/// The lease is held across the first archival attempt as well as the commit, so no later operation can
/// observe a destination that is authoritative while its legacy sources still sit where a client would look
/// for them.
/// </para>
/// </remarks>
public sealed class StoreCutoverCommandHandler : IWorkerCommandHandler
{
    private readonly ProjectRuntimeRegistry _runtimes;

    /// <summary>Creates a cutover handler bound to the worker's runtime registry.</summary>
    public StoreCutoverCommandHandler(ProjectRuntimeRegistry runtimes)
    {
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));
    }

    /// <inheritdoc />
    public string Command => WorkerCommands.StoreCutover;

    /// <inheritdoc />
    public async Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<StoreCutoverRequest>(payload.GetRawText(),
            WorkerJson.CreateOptions())
            ?? throw new InvalidDataException("The store cutover request was empty.");
        var workspaceKey = ProjectWorkspaceKey.Compute(request.Project);
        if (!_runtimes.TryGet(workspaceKey, out var runtime))
            throw new InvalidOperationException("The project runtime is not ready.");

        using var exclusive = await runtime.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);
        var result = ProjectStoreCutover.Run(request.StoreDirectory, runtime.Database);
        var response = new StoreCutoverResponse(
            workspaceKey,
            result.FileProposals is null && result.LegacyBulk is null,
            result.FileProposals?.ProposalIds.Count ?? 0,
            result.LegacyBulk?.RowCount ?? 0,
            result.ArchivedPaths,
            result.ArchiveFailures.Select(failure => failure.Path).ToList());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WorkerJson.CreateOptions()));
        return document.RootElement.Clone();
    }
}
