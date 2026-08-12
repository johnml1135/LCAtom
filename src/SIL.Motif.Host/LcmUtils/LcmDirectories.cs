// Adapted from languageforge-lexbox's FwDataMiniLcmBridge/LcmUtils/LcmDirectories.cs (SIL Global, MIT).

using SIL.LCModel;

namespace SIL.Motif.Host.LcmUtils;

/// <summary>
/// Minimal <see cref="ILcmDirectories"/> implementation pointing LibLCM at the project and
/// template folders it needs during headless load.
/// </summary>
public sealed record LcmDirectories(string ProjectsDirectory, string TemplateDirectory) : ILcmDirectories;
