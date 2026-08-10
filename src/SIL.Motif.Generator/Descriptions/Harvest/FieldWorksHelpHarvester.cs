namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Reads every page <see cref="FieldWorksHelpFieldMap"/> names out of a decompiled FieldWorks help tree and
/// returns one harvested description per mapped field. Dev-time only: its output is written to
/// <c>manifest/fieldworks-help-descriptions.tsv</c> and everything downstream reads that file, so no build
/// and no test ever needs the <c>.chm</c> or the Windows-only tool that opens it.
/// </summary>
public static class FieldWorksHelpHarvester
{
    /// <summary>The <c>Source</c> value a row harvested this way carries.</summary>
    public const string SourceName = "FieldWorks/DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm";

    /// <param name="decompiledHelpRoot">
    /// The directory <c>hh.exe -decompile</c> wrote the help tree into (see
    /// <see cref="CompiledHelpExtractor"/>).
    /// </param>
    public static IReadOnlyList<HarvestedHelpDescription> Harvest(string decompiledHelpRoot)
    {
        var harvested = new List<HarvestedHelpDescription>();
        var missing = new List<string>();

        foreach (var entry in FieldWorksHelpFieldMap.Entries)
        {
            var fullPath = Path.Combine(
                decompiledHelpRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                missing.Add($"{entry.Class}.{entry.Field} -> {entry.RelativePath}");
                continue;
            }

            var page = FieldWorksHelpPageParser.ParseFile(entry.RelativePath, fullPath);
            harvested.Add(new HarvestedHelpDescription(
                entry.Class, entry.Field, page.Title, page.Description, entry.RelativePath, entry.Confidence,
                entry.VerifiedAgainst));
        }

        if (missing.Count > 0)
        {
            throw new GeneratorException(
                $"{missing.Count} mapped help page(s) are not in the decompiled tree at " +
                $"'{decompiledHelpRoot}'. Either the extraction is incomplete or FieldWorks moved the page — " +
                $"find where it went and update FieldWorksHelpFieldMap, rather than dropping the " +
                $"citation:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", missing));
        }

        return harvested;
    }
}

/// <summary>
/// One row of <c>manifest/fieldworks-help-descriptions.tsv</c>: a field, the sentence FieldWorks' own help
/// gives for it, and enough provenance for a reviewer to go and check.
/// </summary>
public sealed record HarvestedHelpDescription(
    string Class,
    string Field,
    string PageTitle,
    string Description,
    string HelpPage,
    string Confidence,
    string VerifiedAgainst)
{
    public string Key => $"{Class}.{Field}";

    /// <summary>What lands in the description row's <c>SourceDetail</c>.</summary>
    public string Citation => $"{HelpPage} (page title: \"{PageTitle}\"; {Confidence}; {VerifiedAgainst})";
}
