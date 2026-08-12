using SIL.LCModel;
using SIL.LCModel.Infrastructure;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// The small, known body of lexical data every test built on <see cref="NewLangProjFixture"/> starts
/// from: two entries, each with a lexeme form, a sense, a gloss and a stem MSA sharing one part of
/// speech.
/// </summary>
/// <remarks>
/// <para>
/// Two of everything rather than one, because a recurring shape in these tests is "operate on the
/// first, then prove a second is unaffected" — a single entry cannot express that, and a test that
/// silently loses its control still passes.
/// </para>
/// <para>
/// The guids are returned rather than looked up again, so a test names the object it means instead of
/// taking whatever sorts first. That is the substantive difference from reading a sample project: what
/// each test operates on is decided here, in writing, and cannot change underneath it.
/// </para>
/// </remarks>
public sealed record SeededProject(
    Guid FirstEntryId,
    Guid SecondEntryId,
    Guid FirstLexemeFormId,
    Guid SecondLexemeFormId,
    Guid FirstSenseId,
    Guid SecondSenseId,
    Guid PartOfSpeechId)
{
    /// <summary>The vernacular form written into the first entry's lexeme form.</summary>
    public const string FirstForm = "motifa";

    /// <summary>The vernacular form written into the second entry's lexeme form.</summary>
    public const string SecondForm = "motifb";

    /// <summary>The analysis-language gloss on the first sense.</summary>
    public const string FirstGloss = "first seeded gloss";

    /// <summary>The analysis-language gloss on the second sense.</summary>
    public const string SecondGloss = "second seeded gloss";

    /// <summary>
    /// Writes the seed into <paramref name="cache"/> in one non-undoable unit of work, and returns the
    /// identity of everything it made.
    /// </summary>
    public static SeededProject Seed(LcmCache cache)
    {
        var services = cache.ServiceLocator;
        var vernWs = cache.DefaultVernWs;
        var analWs = cache.DefaultAnalWs;

        var stemType = services.GetInstance<IMoMorphTypeRepository>()
            .GetObject(MoMorphTypeTags.kguidMorphStem);

        ILexEntry first = null!;
        ILexEntry second = null!;
        IPartOfSpeech pos = null!;

        NonUndoableUnitOfWorkHelper.Do(cache.ActionHandlerAccessor, () =>
        {
            pos = services.GetInstance<IPartOfSpeechFactory>().Create();
            cache.LangProject.PartsOfSpeechOA.PossibilitiesOS.Add(pos);
            pos.Name.set_String(analWs, "SeededNoun");

            first = MakeEntry(cache, stemType, FirstForm, FirstGloss, pos, vernWs, analWs);
            second = MakeEntry(cache, stemType, SecondForm, SecondGloss, pos, vernWs, analWs);
        });

        return new SeededProject(
            FirstEntryId: first.Guid,
            SecondEntryId: second.Guid,
            FirstLexemeFormId: first.LexemeFormOA!.Guid,
            SecondLexemeFormId: second.LexemeFormOA!.Guid,
            FirstSenseId: first.SensesOS[0].Guid,
            SecondSenseId: second.SensesOS[0].Guid,
            PartOfSpeechId: pos.Guid);
    }

    private static ILexEntry MakeEntry(
        LcmCache cache, IMoMorphType stemType, string form, string gloss, IPartOfSpeech pos, int vernWs, int analWs)
    {
        var services = cache.ServiceLocator;

        var entry = services.GetInstance<ILexEntryFactory>().Create();

        var lexemeForm = services.GetInstance<IMoStemAllomorphFactory>().Create();
        entry.LexemeFormOA = lexemeForm;
        lexemeForm.MorphTypeRA = stemType;
        lexemeForm.Form.set_String(vernWs, form);

        var msa = services.GetInstance<IMoStemMsaFactory>().Create();
        entry.MorphoSyntaxAnalysesOC.Add(msa);
        msa.PartOfSpeechRA = pos;

        var sense = services.GetInstance<ILexSenseFactory>().Create();
        entry.SensesOS.Add(sense);
        sense.Gloss.set_String(analWs, gloss);
        sense.MorphoSyntaxAnalysisRA = msa;

        return entry;
    }
}
