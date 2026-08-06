using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers reading <c>manifest/liblcm-inventory.tsv</c>'s own dialect back in, read-only, for coverage
/// computation — this tool must never write to that file.
/// </summary>
public class ManifestReaderTests
{
    [Fact]
    public void Read_extracts_class_field_and_scope_from_a_quoted_CRLF_tsv()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tsv");
        try
        {
            // Mirrors the real header order/dialect: quoted, tab-separated, CRLF, extra columns present but
            // not needed by this tool.
            var content =
                "\"Class\"\t\"Base\"\t\"Scope\"\t\"Field\"\r\n" +
                "\"LexEntry\"\t\"CmObject\"\t\"in\"\t\"LexemeForm\"\r\n" +
                "\"ChkRef\"\t\"CmObject\"\t\"out\"\t\"KeyWord\"\r\n";
            File.WriteAllText(path, content);

            var rows = ManifestReader.Read(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new ManifestRow("LexEntry", "LexemeForm", "in"), rows[0]);
            Assert.Equal(new ManifestRow("ChkRef", "KeyWord", "out"), rows[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_throws_when_expected_columns_are_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tsv");
        try
        {
            File.WriteAllText(path, "\"Foo\"\t\"Bar\"\r\n\"1\"\t\"2\"\r\n");

            Assert.Throws<InvalidOperationException>(() => ManifestReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
