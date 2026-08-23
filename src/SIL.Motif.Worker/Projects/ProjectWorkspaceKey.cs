using System;
using System.Text;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Worker.Projects;

/// <summary>Computes the worker workspace identity for one exact project locator.</summary>
public static class ProjectWorkspaceKey
{
    /// <summary>Hashes the canonical path-and-identity tuple into a portable workspace key.</summary>
    public static string Compute(ProjectLocator project)
    {
        if (project is null)
            throw new ArgumentNullException(nameof(project));

        return IntentDigest.Sha256Of(CanonicalBytes(project));
    }

    /// <summary>
    /// Returns UTF-8 bytes for two ordered unsigned big-endian length-framed values: normalized
    /// full Windows path, then the exact opaque FieldWorks identity.
    /// </summary>
    public static byte[] CanonicalBytes(ProjectLocator project)
    {
        if (project is null)
            throw new ArgumentNullException(nameof(project));

        var path = project.FullFwDataPath.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(project.FieldWorksProjectIdentity))
            throw new ArgumentException("A nonblank FieldWorks project identity is required.",
                nameof(project.FieldWorksProjectIdentity));

        using var tuple = new MemoryStream();
        WriteFrame(tuple, path);
        WriteFrame(tuple, project.FieldWorksProjectIdentity);
        return tuple.ToArray();
    }

    /// <summary>Returns a Windows-safe segment for derived worker workspace paths.</summary>
    public static string StorageSegment(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey))
            throw new ArgumentException("A workspace key is required.", nameof(workspaceKey));
        if (workspaceKey.StartsWith("sha256:", StringComparison.Ordinal))
            return "sha256-" + workspaceKey.Substring("sha256:".Length);
        if (workspaceKey.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
            throw new ArgumentException("A workspace key is not a safe storage segment.", nameof(workspaceKey));
        return workspaceKey;
    }

    private static void WriteFrame(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = bytes.Length;
        stream.WriteByte((byte)(length >> 24));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(bytes, 0, bytes.Length);
    }
}
