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
    BaselineToken Token,
    bool Created);

internal sealed class BaselineBundleReceiver
{
    private const int CopyBufferSize = 32 * 1024;
    private readonly int _maximumEntries;
    private readonly long _maximumExtractedBytes;

    public BaselineBundleReceiver(int maximumEntries = 4096,
        long maximumExtractedBytes = 512L * 1024 * 1024)
    {
        if (maximumEntries < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumExtractedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumExtractedBytes));
        _maximumEntries = maximumEntries;
        _maximumExtractedBytes = maximumExtractedBytes;
    }

    public async Task<BaselinePublication> PublishVerifiedAsync(
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
            var destination = Path.Combine(root, declaredToken.BundleDigest.Substring("sha256:".Length));
            if (Directory.Exists(destination))
                return ExistingPublication(destination, declaredToken);

            temporaryDirectory = Path.Combine(root, ".incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var fwDataPath = await ExtractValidatedAsync(
                transfer.TemporaryPath, temporaryDirectory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(temporaryDirectory, destination);
                temporaryDirectory = string.Empty;
                return new BaselinePublication(destination,
                    Path.Combine(destination, Path.GetFileName(fwDataPath)), declaredToken, true);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                DeleteIncoming(temporaryDirectory);
                temporaryDirectory = string.Empty;
                return ExistingPublication(destination, declaredToken);
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
            transfer.ByteCount > BaselineTransferOfferCommandHandler.MaximumBundleBytes)
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

    private async Task<string> ExtractValidatedAsync(
        string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        if (archive.Entries.Count == 0 || archive.Entries.Count > _maximumEntries)
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
            totalBytes = checked(totalBytes + entry.Length);
            if (entry.Length < 0 || entry.Length > _maximumExtractedBytes ||
                totalBytes > _maximumExtractedBytes)
                throw new InvalidDataException("The Baseline bundle expands beyond its bound.");
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
        foreach (var entry in writingSystems.OrderBy(item => item.FullName, StringComparer.Ordinal))
            await ExtractEntryAsync(entry, destination, cancellationToken).ConfigureAwait(false);
        return fwDataPath;
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
        if (fwData.Length != 1 || !Directory.Exists(writingSystemRoot) ||
            (File.GetAttributes(writingSystemRoot) & FileAttributes.ReparsePoint) != 0 ||
            Directory.GetFiles(writingSystemRoot, "*.ldml", SearchOption.TopDirectoryOnly).Length == 0 ||
            Directory.GetFileSystemEntries(writingSystemRoot, "*", SearchOption.TopDirectoryOnly).Any(path =>
                !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !path.EndsWith(".ldml", StringComparison.OrdinalIgnoreCase)) ||
            entries.Any(path => !StringComparer.OrdinalIgnoreCase.Equals(path, fwData[0]) &&
                !StringComparer.OrdinalIgnoreCase.Equals(path, writingSystemRoot)))
            throw new InvalidDataException("The existing Baseline publication has an invalid layout.");
        return new BaselinePublication(root, fwData[0], token, false);
    }

    private static void DeleteIncoming(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            var writingSystems = Path.Combine(path, "WritingSystemStore");
            if (Directory.Exists(writingSystems) &&
                (File.GetAttributes(writingSystems) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var file in Directory.GetFiles(writingSystems, "*", SearchOption.TopDirectoryOnly))
                    File.Delete(file);
                Directory.Delete(writingSystems);
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
