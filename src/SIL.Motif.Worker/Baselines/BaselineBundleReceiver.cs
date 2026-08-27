using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SIL.Motif.Contract.Baselines;

namespace SIL.Motif.Worker.Baselines;

internal sealed record BaselinePublicationTarget
{
    public BaselinePublicationTarget(string baselineRoot, string projectIdentity)
    {
        BaselineRoot = RequireRoot(baselineRoot);
        ProjectIdentity = string.IsNullOrWhiteSpace(projectIdentity)
            ? throw new ArgumentException("A project identity is required.", nameof(projectIdentity))
            : projectIdentity;
    }

    public string BaselineRoot { get; }

    public string ProjectIdentity { get; }

    private static string RequireRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A managed Baseline root is required.", nameof(root));
        return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal sealed record BaselinePublication(
    string RootDirectory,
    string FwDataPath,
    BaselineToken Token);

internal sealed record BaselinePublicationOutcome(
    BaselinePublication Publication,
    bool Created);

internal sealed class BaselineBundleReceiver
{
    private const int CopyBufferSize = 32 * 1024;
    private const int EndOfCentralDirectoryLength = 22;
    private const int CentralDirectoryFileHeaderLength = 46;
    private const int MaximumZipCommentLength = ushort.MaxValue;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint CentralDirectoryFileHeaderSignature = 0x02014b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private readonly int _maximumEntries;
    private readonly long _maximumExtractedBytes;
    private readonly Action? _beforeArchiveMaterialization;
    private readonly Action<string>? _beforePublicationMove;

    public BaselineBundleReceiver(int maximumEntries = 4096,
        long maximumExtractedBytes = 512L * 1024 * 1024,
        Action? beforeArchiveMaterialization = null,
        Action<string>? beforePublicationMove = null)
    {
        if (maximumEntries < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumExtractedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumExtractedBytes));
        _maximumEntries = maximumEntries;
        _maximumExtractedBytes = maximumExtractedBytes;
        _beforeArchiveMaterialization = beforeArchiveMaterialization;
        _beforePublicationMove = beforePublicationMove;
    }

    public async Task<BaselinePublication> PublishVerifiedAsync(
        VerifiedBinaryTransfer transfer,
        BaselineToken declaredToken,
        BaselinePublicationTarget target,
        CancellationToken cancellationToken) =>
        (await PublishVerifiedWithOutcomeAsync(
            transfer, declaredToken, target, cancellationToken).ConfigureAwait(false)).Publication;

    internal async Task<BaselinePublicationOutcome> PublishVerifiedWithOutcomeAsync(
        VerifiedBinaryTransfer transfer,
        BaselineToken declaredToken,
        BaselinePublicationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(declaredToken);
        ArgumentNullException.ThrowIfNull(target);
        var temporaryDirectory = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyProjectIdentity(declaredToken, target);
            VerifyTransfer(transfer, declaredToken);
            var root = PrepareManagedRoot(target.BaselineRoot);
            ReclaimIncomingDirectories(root);
            var destination = Path.Combine(root, declaredToken.BundleDigest.Substring("sha256:".Length));
            if (Directory.Exists(destination))
                return new BaselinePublicationOutcome(
                    ExistingPublication(destination, declaredToken), false);

            temporaryDirectory = Path.Combine(root, ".incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var fwDataPath = await ExtractValidatedAsync(
                transfer.TemporaryPath, temporaryDirectory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _beforePublicationMove?.Invoke(destination);
                Directory.Move(temporaryDirectory, destination);
                temporaryDirectory = string.Empty;
                return new BaselinePublicationOutcome(new BaselinePublication(destination,
                    Path.Combine(destination, Path.GetFileName(fwDataPath)), declaredToken), true);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                DeleteIncoming(temporaryDirectory);
                temporaryDirectory = string.Empty;
                return new BaselinePublicationOutcome(
                    ExistingPublication(destination, declaredToken), false);
            }
        }
        finally
        {
            if (temporaryDirectory.Length != 0)
                DeleteIncoming(temporaryDirectory);
            DeleteTransport(transfer.TemporaryPath);
        }
    }

    internal static void DeletePublicationIfOwned(
        BaselinePublication publication, BaselinePublicationTarget target)
    {
        try
        {
            var expected = Path.Combine(target.BaselineRoot,
                publication.Token.BundleDigest.Substring("sha256:".Length));
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(publication.RootDirectory), Path.GetFullPath(expected)) ||
                !Directory.Exists(expected) ||
                (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0)
                return;
            var validated = ExistingPublication(expected, publication.Token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(validated.FwDataPath), Path.GetFullPath(publication.FwDataPath)))
                return;
            var writingSystems = Path.Combine(expected, "WritingSystemStore");
            foreach (var path in Directory.GetFiles(writingSystems, "*", SearchOption.TopDirectoryOnly))
                File.Delete(path);
            Directory.Delete(writingSystems);
            // Pre-created alongside WritingSystemStore by our own publish, but not by every layout seen here.
            var sharedSettings = Path.Combine(expected, "SharedSettings");
            if (Directory.Exists(sharedSettings))
            {
                foreach (var path in Directory.GetFiles(sharedSettings, "*", SearchOption.TopDirectoryOnly))
                    File.Delete(path);
                Directory.Delete(sharedSettings);
            }
            foreach (var path in Directory.GetFiles(expected, "*", SearchOption.TopDirectoryOnly))
                File.Delete(path);
            Directory.Delete(expected);
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void VerifyTransfer(VerifiedBinaryTransfer transfer, BaselineToken token)
    {
        if (!StringComparer.Ordinal.Equals("sha256:" + transfer.Sha256, token.BundleDigest))
            throw new InvalidDataException("The declared Baseline token does not match the verified bundle.");
        using var stream = new FileStream(transfer.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.None);
        if ((File.GetAttributes(transfer.TemporaryPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A reparse-point Baseline transport is refused.");
        if (stream.Length != transfer.ByteCount || transfer.ByteCount <= 0 ||
            transfer.ByteCount > BaselineBundleBounds.MaximumBundleBytes)
            throw new InvalidDataException("The Baseline transport length is invalid.");
        using var sha = SHA256.Create();
        var actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(actual, transfer.Sha256))
            throw new InvalidDataException("The Baseline transport no longer matches its verified bytes.");
    }

    private static void VerifyProjectIdentity(BaselineToken token, BaselinePublicationTarget target)
    {
        if (!StringComparer.Ordinal.Equals(token.ProjectIdentity, target.ProjectIdentity))
            throw new InvalidDataException("The declared Baseline token identifies another project.");
    }

    private static string PrepareManagedRoot(string path)
    {
        if (File.Exists(path)) throw new InvalidDataException("The managed Baseline root is invalid.");
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A reparse-point Baseline root is refused.");
        return path;
    }

    private void ReclaimIncomingDirectories(string root)
    {
        var examined = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root, ".incoming-*", SearchOption.TopDirectoryOnly))
        {
            if (++examined > _maximumEntries)
                throw new InvalidDataException("Too many incomplete Baseline publications were found.");
            if (!IsOwnedIncomingName(path))
                continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A reparse-point incomplete Baseline publication is refused.");
            if (!Directory.Exists(path))
                continue;
            DeleteIncomingStrict(path);
        }
    }

    private static bool IsOwnedIncomingName(string path)
    {
        var name = Path.GetFileName(path);
        const string prefix = ".incoming-";
        return name.Length == prefix.Length + 32 &&
            name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.AsSpan(prefix.Length).ToString().All(Uri.IsHexDigit);
    }

    private void DeleteIncomingStrict(string path)
    {
        var entries = Directory.GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);
        if (entries.Length > _maximumEntries)
            throw new InvalidDataException("An incomplete Baseline publication exceeds its cleanup bound.");
        foreach (var entry in entries)
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A reparse point in an incomplete Baseline publication is refused.");
            if (File.Exists(entry))
            {
                File.Delete(entry);
                continue;
            }
            var entryName = Path.GetFileName(entry);
            if (!StringComparer.Ordinal.Equals(entryName, "WritingSystemStore") &&
                !StringComparer.Ordinal.Equals(entryName, "SharedSettings"))
                throw new InvalidDataException("An incomplete Baseline publication has an invalid layout.");
            var files = Directory.GetFileSystemEntries(entry, "*", SearchOption.TopDirectoryOnly);
            if (entries.Length + files.Length > _maximumEntries || files.Any(item => !File.Exists(item) ||
                    (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("An incomplete Baseline publication exceeds its cleanup bound.");
            foreach (var file in files)
                File.Delete(file);
            Directory.Delete(entry);
        }
        Directory.Delete(path);
    }

    private async Task<string> ExtractValidatedAsync(
        string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var declaredEntries = ValidateCentralDirectoryEnvelope(stream, _maximumEntries);
        _beforeArchiveMaterialization?.Invoke();
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        if (archive.Entries.Count != declaredEntries)
            throw new InvalidDataException("The Baseline bundle entry count is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? fwData = null;
        var writingSystems = new List<ZipArchiveEntry>();
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLinkOrReparsePoint(entry) || !names.Add(entry.FullName) ||
                !IsAllowedEntry(entry.FullName, out var kind))
                throw new InvalidDataException("The Baseline bundle contains an unsupported entry.");
            totalBytes = AddExtractedLength(totalBytes, entry.Length, _maximumExtractedBytes);
            if (kind == EntryKind.FwData)
            {
                if (fwData is not null)
                    throw new InvalidDataException("The Baseline bundle must contain exactly one .fwdata file.");
                fwData = entry;
            }
            else
            {
                writingSystems.Add(entry);
            }
        }
        if (fwData is null || writingSystems.Count == 0)
            throw new InvalidDataException("The Baseline bundle requires one .fwdata and writing-system content.");

        var fwDataPath = await ExtractEntryAsync(fwData, destination, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(destination, "WritingSystemStore"));
        // Pre-create so a project open never mutates this immutable published Baseline.
        Directory.CreateDirectory(Path.Combine(destination, "SharedSettings"));
        foreach (var entry in writingSystems.OrderBy(item => item.FullName, StringComparer.Ordinal))
            await ExtractEntryAsync(entry, destination, cancellationToken).ConfigureAwait(false);
        return fwDataPath;
    }

    internal static long AddExtractedLength(long total, long length, long maximum)
    {
        if (total < 0 || length < 0 || length > maximum || total > maximum - length)
            throw new InvalidDataException("The Baseline bundle expands beyond its bound.");
        return total + length;
    }

    private static int ValidateCentralDirectoryEnvelope(Stream stream, int maximumEntries)
    {
        if (!stream.CanSeek || stream.Length < EndOfCentralDirectoryLength)
            throw new InvalidDataException("The Baseline bundle has an invalid ZIP envelope.");
        var tailLength = (int)Math.Min(stream.Length,
            EndOfCentralDirectoryLength + MaximumZipCommentLength);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        ReadExactly(stream, tail);
        var eocd = -1;
        for (var index = tailLength - EndOfCentralDirectoryLength; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) !=
                    EndOfCentralDirectorySignature)
                continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));
            if (index + EndOfCentralDirectoryLength + commentLength == tailLength)
            {
                eocd = index;
                break;
            }
        }
        if (eocd < 0)
            throw new InvalidDataException("The Baseline bundle has no valid ZIP end record.");
        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20, 4)) ==
                Zip64EndOfCentralDirectoryLocatorSignature)
            throw new InvalidDataException("ZIP64 Baseline bundles are refused.");

        var disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 4, 2));
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 6, 2));
        var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 8, 2));
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
        if (disk != 0 || centralDisk != 0 || diskEntries != totalEntries)
            throw new InvalidDataException("Multi-disk Baseline bundles are refused.");
        if (totalEntries == ushort.MaxValue || centralSize == uint.MaxValue ||
            centralOffset == uint.MaxValue)
            throw new InvalidDataException("ZIP64 Baseline bundles are refused.");
        if (totalEntries == 0 || totalEntries > maximumEntries)
            throw new InvalidDataException("The Baseline bundle entry count is invalid.");
        var eocdOffset = stream.Length - tailLength + eocd;
        if (centralOffset > eocdOffset || centralSize != eocdOffset - centralOffset)
            throw new InvalidDataException("The Baseline bundle central directory is invalid.");
        var parsedEntries = ValidateClassicCentralDirectory(
            stream, centralOffset, centralSize, maximumEntries);
        if (parsedEntries != totalEntries)
            throw new InvalidDataException("The Baseline bundle entry count is invalid.");
        return parsedEntries;
    }

    private static int ValidateClassicCentralDirectory(
        Stream stream, uint centralOffset, uint centralSize, int maximumEntries)
    {
        var end = (long)centralOffset + centralSize;
        var header = new byte[CentralDirectoryFileHeaderLength];
        stream.Position = centralOffset;
        var count = 0;
        while (stream.Position < end)
        {
            if (end - stream.Position < header.Length)
                throw new InvalidDataException("The Baseline bundle central directory is truncated.");
            ReadExactly(stream, header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) !=
                    CentralDirectoryFileHeaderSignature)
                throw new InvalidDataException("The Baseline bundle central directory is invalid.");
            if (++count > maximumEntries)
                throw new InvalidDataException("The Baseline bundle entry count is invalid.");

            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
            var extractedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2));
            var localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42, 4));
            if (disk != 0)
                throw new InvalidDataException("Multi-disk Baseline bundles are refused.");
            if (compressedSize == uint.MaxValue || extractedSize == uint.MaxValue ||
                localHeaderOffset == uint.MaxValue)
                throw new InvalidDataException("ZIP64 Baseline bundles are refused.");
            var variableLength = (long)nameLength + extraLength + commentLength;
            if (variableLength > end - stream.Position || localHeaderOffset >= centralOffset)
                throw new InvalidDataException("The Baseline bundle central directory is invalid.");
            stream.Position += nameLength;
            ValidateClassicExtraFields(stream, extraLength);
            stream.Position += commentLength;
        }
        if (stream.Position != end)
            throw new InvalidDataException("The Baseline bundle central directory is invalid.");
        return count;
    }

    private static void ValidateClassicExtraFields(Stream stream, int extraLength)
    {
        var end = stream.Position + extraLength;
        var header = new byte[4];
        while (stream.Position < end)
        {
            if (end - stream.Position < header.Length)
                throw new InvalidDataException("The Baseline bundle extra fields are invalid.");
            ReadExactly(stream, header);
            var identifier = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            if (identifier == 0x0001)
                throw new InvalidDataException("ZIP64 Baseline bundles are refused.");
            if (dataLength > end - stream.Position)
                throw new InvalidDataException("The Baseline bundle extra fields are invalid.");
            stream.Position += dataLength;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new InvalidDataException("The Baseline bundle ended before its ZIP record.");
            offset += read;
        }
    }

    private static bool IsAllowedEntry(string name, out EntryKind kind)
    {
        kind = EntryKind.FwData;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260 || name.Contains('\\') ||
            name.Contains(':') || name.StartsWith('/') || name.EndsWith('/') ||
            name.Split('/').Any(segment => segment is "" or "." or ".."))
            return false;
        var parts = name.Split('/');
        if (parts.Length == 1 && name.EndsWith(".fwdata", StringComparison.OrdinalIgnoreCase))
            return true;
        if (parts.Length == 2 && StringComparer.Ordinal.Equals(parts[0], "WritingSystemStore") &&
            parts[1].EndsWith(".ldml", StringComparison.OrdinalIgnoreCase))
        {
            kind = EntryKind.WritingSystem;
            return true;
        }
        return false;
    }

    private static bool IsLinkOrReparsePoint(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return unixMode == unixSymbolicLink ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private async Task<string> ExtractEntryAsync(
        ZipArchiveEntry entry, string destination, CancellationToken cancellationToken)
    {
        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(destination, relative));
        var prefix = destination + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Baseline entry leaves the managed extraction root.");
        using var source = entry.Open();
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[CopyBufferSize];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            written += read;
            if (written > entry.Length || written > _maximumExtractedBytes)
                throw new InvalidDataException("A Baseline entry expands beyond its declared bound.");
            await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
        }
        if (written != entry.Length)
            throw new InvalidDataException("A Baseline entry length did not match its archive metadata.");
        return path;
    }

    private static BaselinePublication ExistingPublication(string root, BaselineToken token)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A reparse-point Baseline publication is refused.");
        var entries = Directory.GetFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly);
        var fwData = entries.Where(File.Exists)
            .Where(path => path.EndsWith(".fwdata", StringComparison.OrdinalIgnoreCase)).ToArray();
        var writingSystemRoot = Path.Combine(root, "WritingSystemStore");
        var sharedSettingsRoot = Path.Combine(root, "SharedSettings");
        if (fwData.Length != 1 ||
            (File.GetAttributes(fwData[0]) & FileAttributes.ReparsePoint) != 0 ||
            !Directory.Exists(writingSystemRoot) ||
            (File.GetAttributes(writingSystemRoot) & FileAttributes.ReparsePoint) != 0 ||
            Directory.GetFiles(writingSystemRoot, "*.ldml", SearchOption.TopDirectoryOnly).Length == 0 ||
            Directory.GetFileSystemEntries(writingSystemRoot, "*", SearchOption.TopDirectoryOnly).Any(path =>
                !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !path.EndsWith(".ldml", StringComparison.OrdinalIgnoreCase)) ||
            // Optional: pre-created by our own publish, but older or rival layouts may not have it.
            (Directory.Exists(sharedSettingsRoot) &&
                ((File.GetAttributes(sharedSettingsRoot) & FileAttributes.ReparsePoint) != 0 ||
                    // No extension is trusted here, so every entry must at least be a real, non-reparse file.
                    Directory.GetFileSystemEntries(sharedSettingsRoot, "*", SearchOption.TopDirectoryOnly).Any(
                        path => !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))) ||
            entries.Any(path => !StringComparer.OrdinalIgnoreCase.Equals(path, fwData[0]) &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, writingSystemRoot) &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, sharedSettingsRoot)))
            throw new InvalidDataException("The existing Baseline publication has an invalid layout.");
        return new BaselinePublication(root, fwData[0], token);
    }

    private static void DeleteIncoming(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            // Both are pre-created by ExtractValidatedAsync, so both must go before the parent can.
            foreach (var name in new[] { "WritingSystemStore", "SharedSettings" })
            {
                var sub = Path.Combine(path, name);
                if (Directory.Exists(sub) && (File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (var file in Directory.GetFiles(sub, "*", SearchOption.TopDirectoryOnly))
                        File.Delete(file);
                    Directory.Delete(sub);
                }
            }
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
                File.Delete(file);
            Directory.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void DeleteTransport(string path)
    {
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private enum EntryKind { FwData, WritingSystem }
}
