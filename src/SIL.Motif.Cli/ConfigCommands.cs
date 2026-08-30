using SIL.Motif.Contract.Responses;
using System;
using System.Linq;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Config;
using SIL.Motif.Projection.Rendering;

namespace SIL.Motif.Cli;

/// <summary>The <c>config</c> verb: showing a project's resolved Assessment configuration.</summary>
/// <remarks>
/// <see cref="ProjectConfigurationReader"/> is the seam that turns <c>&lt;project&gt;.motif.toml</c>, or its
/// absence, into one resolved <see cref="ProjectConfiguration"/> with every default already applied; this
/// class only renders that result, the same way every other read verb renders its projection.
/// </remarks>
public static class ConfigCommands
{
    /// <summary>The resolved project configuration, as text.</summary>
    public static CommandResult Show(string fwDataPath, string productVersion) =>
        ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
            TryResolve(project, out var configuration, out var refusal)
                ? new CommandResult(0, CommandTextRenderer.Render(ToProjection(configuration)))
                : refusal!);

    /// <summary>The <c>config show</c> report as JSON, rendered from the same projection as text.</summary>
    public static CommandResult ShowJson(string fwDataPath, string productVersion) =>
        ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
            TryResolve(project, out var configuration, out var refusal)
                ? new CommandResult(0, ProjectionJson.Serialize(ToProjection(configuration)) + Environment.NewLine)
                : refusal!);

    private static bool TryResolve(
        ProjectLocator project,
        out ProjectConfiguration configuration,
        out CommandResult? refusal)
    {
        try
        {
            configuration = new ProjectConfigurationReader().Read(project);
            refusal = null;
            return true;
        }
        catch (ProjectConfigurationException exception)
        {
            configuration = null!;
            refusal = ProjectStoreCommand.Refuse(FailureReason.Refused, exception.Message);
            return false;
        }
    }

    private static ProjectConfigurationProjection ToProjection(ProjectConfiguration configuration) => new(
        configuration.GateOnRegression,
        configuration.PurgeOnApply,
        configuration.Scopes
            .Select(scope => new AssessmentScopeProjection(
                scope.Name, scope.Query, scope.Assessor, scope.Engine, scope.Collect,
                (long)scope.PerWordLimit.TotalMilliseconds))
            .ToArray());
}
