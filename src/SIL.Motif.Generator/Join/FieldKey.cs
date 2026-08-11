namespace SIL.Motif.Generator.Join;

/// <summary>
/// The join key: <c>(Class, Field)</c>, i.e. the declaring class and the field name
/// declared on it. A record rather than a tuple so join-failure messages can name it with a single
/// readable <see cref="ToString"/> rather than the caller re-formatting a pair every time.
/// </summary>
public sealed record FieldKey(string ClassName, string FieldName)
{
    public override string ToString() => $"{ClassName}.{FieldName}";
}
