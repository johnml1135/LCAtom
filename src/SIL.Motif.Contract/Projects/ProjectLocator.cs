using System;
using System.Collections.Generic;
using System.Text;
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
        if (path.EndsWith("\\", StringComparison.Ordinal))
            throw new ArgumentException("A project data-file path cannot have a trailing separator.",
                nameof(value));

        var segments = new List<string>();
        string root;
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            root = path.Substring(0, 3);
            var driveSegments = SplitNonEmpty(path.Substring(3));
            RejectFinalDirectorySegment(driveSegments, nameof(value));
            AddCanonicalSegments(driveSegments, 0, segments, nameof(value));
        }
        else if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var uncSegments = SplitNonEmpty(path.Substring(2));
            if (uncSegments.Count < 3 || IsDotSegment(uncSegments[0]) || IsDotSegment(uncSegments[1]))
                throw new ArgumentException("A UNC project path must include a server and share.",
                    nameof(value));

            root = "\\\\" + uncSegments[0] + "\\" + uncSegments[1];
            RejectFinalDirectorySegment(uncSegments, nameof(value));
            AddCanonicalSegments(uncSegments, 2, segments, nameof(value));
        }
        else
        {
            throw new ArgumentException("A fully qualified Windows path is required.",
                nameof(value));
        }

        if (segments.Count == 0 || IsDotSegment(segments[segments.Count - 1]) ||
            !segments[segments.Count - 1].EndsWith(".fwdata", StringComparison.OrdinalIgnoreCase) ||
            segments[segments.Count - 1].Length == ".fwdata".Length)
            throw new ArgumentException("A project data-file path must name a .fwdata file.",
                nameof(value));

        var canonical = new StringBuilder(root);
        foreach (var segment in segments)
        {
            if (canonical.Length > 0 && canonical[canonical.Length - 1] != '\\')
                canonical.Append('\\');
            canonical.Append(segment);
        }
        return canonical.ToString();
    }

    private static void AddCanonicalSegments(
        IReadOnlyList<string> input,
        int start,
        List<string> output,
        string parameterName)
    {
        for (var index = start; index < input.Count; index++)
        {
            var segment = input[index];
            if (IsDotSegment(segment))
            {
                if (segment == "..")
                {
                    if (output.Count == 0)
                        throw new ArgumentException("A project path cannot traverse above its root.",
                            parameterName);
                    output.RemoveAt(output.Count - 1);
                }
                continue;
            }
            output.Add(segment);
        }
    }

    private static void RejectFinalDirectorySegment(
        IReadOnlyList<string> segments,
        string parameterName)
    {
        if (segments.Count == 0 || IsDotSegment(segments[segments.Count - 1]))
            throw new ArgumentException("A project data-file path must name a file.", parameterName);
    }

    private static List<string> SplitNonEmpty(string path) => new(path.Split(
        new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsDotSegment(string segment) => segment == "." || segment == "..";

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
        return value!;
    }
}
