// Adapted from FwDataMiniLcmBridge, Copyright (c) SIL Global, licensed under the MIT License.
// Source: languageforge-lexbox/backend/FwLite/FwDataMiniLcmBridge/LcmUtils/LcmDirectories.cs
// https://github.com/sillsdev/languageforge-lexbox

using SIL.LCModel;

namespace SIL.LCAtom.Host.LcmUtils;

/// <summary>
/// Minimal <see cref="ILcmDirectories"/> implementation pointing LibLCM at the project and
/// template folders it needs during headless load.
/// </summary>
public sealed record LcmDirectories(string ProjectsDirectory, string TemplateDirectory) : ILcmDirectories;
