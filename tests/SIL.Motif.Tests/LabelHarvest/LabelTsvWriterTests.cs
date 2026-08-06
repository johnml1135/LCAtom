using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers the output dialect requirement: tab-separated, every field double-quoted, CRLF line endings —
/// matching <c>manifest/liblcm-inventory.tsv</c> exactly so the same downstream tooling reads both.
/// </summary>
public class LabelTsvWriterTests
{
    [Fact]
    public void Write_produces_quoted_tab_separated_CRLF_rows_with_the_expected_header()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tsv");
        try
        {
            var rows = new[]
            {
                new LabelRow("LexEntry", "LexemeForm", "Lexeme Form", "", "slice", "LexEntry.fwlayout", "exact"),
            };

            LabelTsvWriter.Write(path, rows);
            var text = File.ReadAllText(path);

            Assert.Contains("\r\n", text);
            Assert.DoesNotContain("\r\r\n", text); // no double line endings
            var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("\"Class\"\t\"Field\"\t\"Label\"\t\"Tooltip\"\t\"Source\"\t\"SourceDetail\"\t\"Confidence\"", lines[0]);
            Assert.Equal("\"LexEntry\"\t\"LexemeForm\"\t\"Lexeme Form\"\t\"\"\t\"slice\"\t\"LexEntry.fwlayout\"\t\"exact\"", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_doubles_embedded_quotes()
    {
        // strings-en.xml has <string id="ProdRestrict-Plural" txt='Exception "Features"'/> — a label with a
        // literal embedded quote. The writer must escape it, not break the row.
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tsv");
        try
        {
            var rows = new[]
            {
                new LabelRow("MoBinaryCompoundRule", "ProdRestrict", "Exception \"Features\"", "", "slice", "x", "exact"),
            };

            LabelTsvWriter.Write(path, rows);
            var text = File.ReadAllText(path);

            Assert.Contains("\"Exception \"\"Features\"\"\"", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
