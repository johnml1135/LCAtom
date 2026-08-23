using System;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Projects;

/// <summary>Identifies one FieldWorks project without relying on its display name.</summary>
public sealed record ProjectLocator
{
    private string _fullFwDataPath = null!;
    private string _fieldWorksProjectIdentity = null!;

    [JsonConstructor]
    public ProjectLocator(string fullFwDataPath, string fieldWorksProjectIdentity)
    {
        FullFwDataPath = fullFwDataPath;
        FieldWorksProjectIdentity = fieldWorksProjectIdentity;
    }

    /// <summary>Gets the fully qualified path to the FieldWorks project data file.</summary>
    [JsonPropertyOrder(0)]
    public string FullFwDataPath
    {
        get => _fullFwDataPath;
        init => _fullFwDataPath = RequireFullWindowsPath(value);
    }

    /// <summary>Gets the stable FieldWorks identity paired with the data-file path.</summary>
    [JsonPropertyOrder(1)]
    public string FieldWorksProjectIdentity
    {
        get => _fieldWorksProjectIdentity;
        init => _fieldWorksProjectIdentity = RequireNonBlank(value,
            nameof(FieldWorksProjectIdentity));
    }

    private static string RequireFullWindowsPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A fully qualified Windows path is required.",
                nameof(value));

        var path = value!.Replace('/', '\\');
        var driveAbsolute = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
            path[2] == '\\';
        var uncSegments = path.StartsWith("\\\\", StringComparison.Ordinal)
            ? path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        var uncAbsolute = uncSegments.Length >= 2;
        if (!driveAbsolute && !uncAbsolute)
            throw new ArgumentException("A fully qualified Windows path is required.",
                nameof(value));

        return value;
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
        return value!;
    }
}
