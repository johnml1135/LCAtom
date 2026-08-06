using System.Xml.Linq;

namespace SIL.Motif.Generator.Model;

/// <summary>
/// Parses <c>MasterLCModel.xml</c> into <see cref="ModelClass"/>/<see cref="ModelField"/> records.
/// Verified structure (checked directly against the file shipped in the pinned package, not assumed
/// from the XSD): the root is <c>&lt;EntireModel version="7000072"&gt;</c>, classes are nested inside
/// one or more <c>&lt;CellarModule&gt;</c> wrappers, and each <c>&lt;class&gt;</c>'s fields sit inside
/// a <c>&lt;props&gt;</c> child (self-closing — <c>&lt;props/&gt;</c> — for the 13 classes with no
/// declared fields of their own, e.g. the bare <c>CmObject</c> root). <see cref="XContainer.Descendants"/>
/// is used rather than <see cref="XContainer.Elements"/> throughout so this does not depend on that
/// nesting depth staying exactly two levels if a future LibLCM version reshuffles it.
/// </summary>
public static class MasterLcModelParser
{
    public static ParsedModel Parse(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            throw new GeneratorException($"Could not read or parse '{path}' as MasterLCModel.xml: {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new GeneratorException($"'{path}' has no root element.");
        var version = (string?)root.Attribute("version")
            ?? throw new GeneratorException($"'{path}': <{root.Name}> has no version attribute.");

        var classes = new List<ModelClass>();
        var fields = new List<ModelField>();

        foreach (var classElement in root.Descendants("class"))
        {
            var id = (string?)classElement.Attribute("id")
                ?? throw new GeneratorException($"'{path}': a <class> element has no id attribute.");
            var baseClass = (string?)classElement.Attribute("base");
            var isAbstract = (bool?)classElement.Attribute("abstract") ?? false;
            classes.Add(new ModelClass(id, baseClass, isAbstract));

            foreach (var element in classElement.Descendants("basic"))
                fields.Add(ParseField(path, id, element, FieldKind.Basic));
            foreach (var element in classElement.Descendants("owning"))
                fields.Add(ParseField(path, id, element, FieldKind.Owning));
            foreach (var element in classElement.Descendants("rel"))
                fields.Add(ParseField(path, id, element, FieldKind.Rel));
        }

        return new ParsedModel(version, classes, fields);
    }

    private static ModelField ParseField(string path, string declaringClass, XElement element, FieldKind kind)
    {
        var name = (string?)element.Attribute("id")
            ?? throw new GeneratorException($"'{path}': a <{element.Name}> element under class '{declaringClass}' has no id attribute.");
        var sig = (string?)element.Attribute("sig")
            ?? throw new GeneratorException($"'{path}': {declaringClass}.{name} has no sig attribute.");

        // <basic> never carries card; <owning>/<rel> always do (docs/plan-motif.md MOT-2, and
        // verified above: zero owning/rel elements in the shipped file lack it).
        var cardText = (string?)element.Attribute("card");
        FieldCard? card = kind == FieldKind.Basic
            ? null
            : ParseCard(path, declaringClass, name, cardText);

        return new ModelField(declaringClass, name, kind, sig, card);
    }

    private static FieldCard ParseCard(string path, string declaringClass, string fieldName, string? text) => text switch
    {
        "atomic" => FieldCard.Atomic,
        "col" => FieldCard.Col,
        "seq" => FieldCard.Seq,
        _ => throw new GeneratorException(
            $"'{path}': {declaringClass}.{fieldName} has an owning/rel field with unrecognized card '{text}'."),
    };
}
