using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.LcmUtils;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Prepares a <see cref="NewLangProjFixture"/>-shaped scratch cache so a real <c>pangloss</c> run can
/// actually segment its seeded vernacular forms, then saves so the change is visible on disk.
/// </summary>
/// <remarks>
/// <para>
/// Two gaps separate "a project LibLCM considers valid" from "a project PanGloss can parse a word
/// against", and <see cref="LcmCache.CreateCacheWithNewBlankLangProj"/> closes neither: unlike the
/// richer project-creation path FieldWorks' own UI uses, it seeds no phonology at all.
/// </para>
/// <para>
/// <b>No compound rules means two synthesized defaults, and those need a literal <c>+</c> boundary
/// marker.</b> Unless the project's <c>ParserParameters</c> says <c>NoDefaultCompounding</c>, PanGloss
/// joins the two synthesized rules' constituents with <c>+</c>, which panics unless the phoneme table
/// recognises that character.
/// </para>
/// <para>
/// <b>No phoneme set means no character definition at all.</b> A blank project's
/// <c>PhonologicalDataOA.PhonemeSetsOS</c> is empty, so every vernacular letter is unsegmentable until
/// something declares it a phoneme — there is no default inventory to mirror onto the vernacular
/// writing system here, unlike a project created through FieldWorks' own new-project wizard.
/// </para>
/// </remarks>
internal static class RealParserProject
{
    private const string ParserParametersXml =
        "<ParserParameters><HC><NoDefaultCompounding>true</NoDefaultCompounding><Strata /></HC></ParserParameters>";

    /// <param name="vernacularLetters">
    /// Every distinct character appearing in the vernacular words this test will hand to the parser.
    /// Each becomes its own one-segment phoneme.
    /// </param>
    public static void PrepareForParsing(LcmCache cache, params string[] vernacularLetters)
    {
        NonUndoableUnitOfWorkHelper.Do(cache.ActionHandlerAccessor, () =>
        {
            cache.LangProject.MorphologicalDataOA.ParserParameters = ParserParametersXml;

            var services = cache.ServiceLocator;
            var phonemeSets = cache.LangProject.PhonologicalDataOA.PhonemeSetsOS;
            if (phonemeSets.Count == 0)
                phonemeSets.Add(services.GetInstance<IPhPhonemeSetFactory>().Create());
            var phonemeSet = phonemeSets[0];

            foreach (var letter in vernacularLetters.Distinct())
            {
                var phoneme = services.GetInstance<IPhPhonemeFactory>().Create();
                phonemeSet.PhonemesOC.Add(phoneme);
                phoneme.Name.set_String(cache.DefaultVernWs, letter);

                var code = services.GetInstance<IPhCodeFactory>().Create();
                phoneme.CodesOS.Add(code);
                code.Representation.set_String(cache.DefaultVernWs, letter);
            }
        });

        new FwDataProjectLoader().Save(cache);
    }
}
