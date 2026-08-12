using SIL.LCModel;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// A file-backed <see cref="IProjectIdentifier"/>, the counterpart to
/// <see cref="SIL.Motif.Host.LcmUtils.MemoryOnlyProjectId"/>, needed because LibLCM's own
/// implementations are <c>internal</c> and the one public one drags NUnit and Moq in with it.
/// </summary>
/// <remarks>
/// Unlike the memory-only identifier, <see cref="ProjectFolder"/> here is deliberately real: creating a
/// blank project has to write writing systems into the project's own <c>WritingSystemStore</c>, and that
/// only happens when the folder is non-empty.
/// </remarks>
public sealed class LocalProjectId : IProjectIdentifier
{
    public LocalProjectId(string fwDataPath)
    {
        Path = System.IO.Path.GetFullPath(fwDataPath);
        Name = System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string UiName => Name;

    /// <inheritdoc />
    public string Path { get; set; }

    /// <inheritdoc />
    public string Handle => Path;

    /// <inheritdoc />
    public string PipeHandle => Path;

    /// <inheritdoc />
    public string ProjectFolder => System.IO.Path.GetDirectoryName(Path)!;

    /// <inheritdoc />
    public BackendProviderType Type => BackendProviderType.kXML;
}
