using System.Text.Json;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker.Projects;

internal sealed class LiveHostObservationCommandHandler : IWorkerCommandHandler
{
    private readonly ProjectRuntimeRegistry _runtimes;
    private readonly Func<JsonElement, ProjectLocator> _project;
    private readonly Func<JsonElement, bool> _apply;

    internal LiveHostObservationCommandHandler(string command, ProjectRuntimeRegistry runtimes,
        Func<JsonElement, ProjectLocator> project, Func<JsonElement, bool> apply)
    {
        Command = command;
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public string Command { get; }

    public async Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var project = _project(payload);
        var key = ProjectWorkspaceKey.Compute(project);
        if (!_runtimes.TryGet(key, out var runtime))
            throw new InvalidOperationException("The project runtime is not ready.");
        using var operation = await runtime.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);
        var response = new LiveHostObservationResponse(key, _apply(payload));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WorkerJson.CreateOptions()));
        return document.RootElement.Clone();
    }

    internal static T Deserialize<T>(JsonElement payload) where T : class =>
        JsonSerializer.Deserialize<T>(payload.GetRawText(), WorkerJson.CreateOptions()) ??
        throw new InvalidDataException("The live-host request was empty.");
}
