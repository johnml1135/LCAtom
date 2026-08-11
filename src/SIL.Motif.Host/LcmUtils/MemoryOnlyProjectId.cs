using SIL.LCModel;

namespace SIL.Motif.Host.LcmUtils;

/// <summary>
/// A minimal <see cref="IProjectIdentifier"/> for an in-memory scratch cache
/// (<see cref="BackendProviderType.kMemoryOnly"/>), as required by ADR 0016.
/// </summary>
/// <remarks>
/// <para>
/// liblcm's own implementation, <c>SIL.LCModel.Infrastructure.Impl.SimpleProjectId</c>, is
/// <c>internal</c>, and its only public one (<c>TestProjectId</c>) drags NUnit and Moq along — so a
/// consumer that wants a memory-only cache has to supply its own. That is all this is.
/// </para>
/// <para>
/// <b><see cref="Path"/> and <see cref="ProjectFolder"/> are deliberately empty, and that is load-bearing
/// rather than cosmetic.</b> <c>BackendProvider.InitializeWritingSystemManager</c> only reaches the
/// process-global <c>CoreGlobalWritingSystemRepository</c> singleton (via <c>SingletonsContainer</c>) when
/// the project folder is non-empty. Keeping it empty is what lets a scratch cache coexist with a live one
/// without touching shared writing-system state.
/// </para>
/// <para>
/// Note that a memory-only cache's writing systems are therefore synthesized from the bare language tag
/// and carry none of the source project's collation, valid-character, font, or keyboard settings. That is
/// a documented hazard of the scratch design, not a defect in this class — see ADR 0016's amendment.
/// </para>
/// </remarks>
public sealed class MemoryOnlyProjectId : IProjectIdentifier
{
    /// <param name="name">
    /// A label for the scratch. It never reaches a filesystem, so it only has to be recognisable in
    /// diagnostics; it does not have to be unique or valid as a file name.
    /// </param>
    public MemoryOnlyProjectId(string name = "motif-scratch")
    {
        Name = string.IsNullOrWhiteSpace(name) ? "motif-scratch" : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string UiName => Name;

    /// <summary>
    /// Always empty. See the class remarks: a non-empty value pulls in the global writing-system
    /// repository singleton. The setter is required by the interface and intentionally ignores writes.
    /// </summary>
    public string Path
    {
        get => string.Empty;
        set { /* intentionally ignored: a memory-only project has no path */ }
    }

    /// <inheritdoc />
    public string Handle => Name;

    /// <inheritdoc />
    public string PipeHandle => Name;

    /// <summary>Always empty — see the class remarks.</summary>
    public string ProjectFolder => string.Empty;

    /// <inheritdoc />
    public BackendProviderType Type => BackendProviderType.kMemoryOnly;
}
