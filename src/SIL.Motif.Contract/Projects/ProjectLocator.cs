using System;
using System.IO;

namespace SIL.Motif.Contract.Projects;

/// <summary>Identifies one FieldWorks project without relying on its display name.</summary>
public sealed record ProjectLocator(string FullFwDataPath, string FieldWorksProjectIdentity)
{
    /// <summary>Gets the full path to the FieldWorks project data file.</summary>
    public string FullFwDataPath { get; init; } = FullFwDataPath;

    /// <summary>Gets the stable FieldWorks identity paired with the data-file path.</summary>
    public string FieldWorksProjectIdentity { get; init; } = FieldWorksProjectIdentity;
}
