namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Writes an inline fixture string to a uniquely-named temp file so harvesters that load by path
/// (matching how they're actually invoked against the real FieldWorks checkout) can be exercised without
/// depending on that checkout being present. Deletes itself on dispose.
/// </summary>
public sealed class TestFile : IDisposable
{
    public string Path { get; }

    public TestFile(string fileName, string contents)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
        File.WriteAllText(Path, contents);
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best-effort */ }
    }
}
