namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// One harvested (class, field) -&gt; label fact, before de-duplication and confidence resolution.
/// </summary>
/// <param name="Class">The FieldWorks/LCM class the label concerns. Empty only never — every record names
/// at least a class; <paramref name="Field"/> is empty for class-level (not field-level) labels.</param>
/// <param name="Field">The field the label names, or empty when the label is class-level only (a
/// <c>strings-en.xml</c> class name, a possibility-list "kind" name, or a tool-config label).</param>
/// <param name="Label">The English label text as FieldWorks shows it.</param>
/// <param name="Tooltip">The tooltip/help text shown alongside the label, or empty when none was found.
/// Only the <c>&lt;slice&gt;</c> mechanism carries tooltips.</param>
/// <param name="Source">Which of the three harvest mechanisms produced this record: <c>strings-en</c>,
/// <c>slice</c> (the <c>.fwlayout</c> / <c>Parts/*.xml</c> registry), or <c>tool-config</c>.</param>
/// <param name="SourceDetail">The file and layout/part/tool name, so a reviewer can trace the label back to
/// its exact origin.</param>
public sealed record RawLabel(string Class, string Field, string Label, string Tooltip, string Source, string SourceDetail);

/// <summary>
/// One output row: a de-duplicated (class, field, label) fact with its confidence resolved against every
/// other label found for the same (class, field) pair.
/// </summary>
public sealed record LabelRow(
    string Class,
    string Field,
    string Label,
    string Tooltip,
    string Source,
    string SourceDetail,
    string Confidence);
