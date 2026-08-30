using System.Text;

namespace SIL.Motif.Host.Config;

/// <summary>A project's <c>.motif.toml</c> is present and cannot be resolved into a configuration.</summary>
/// <remarks>
/// This is a file a human edits by hand, so the message is most of its usability: every throw site below
/// names the source path and the one-based line at fault, and never invents a value for what it could not
/// read. Raised for a syntax error, an unrecognised table, and a key the schema does not declare — a
/// silently-ignored key would let a misspelled policy pass as if it had taken effect.
/// </remarks>
public sealed class ProjectConfigurationException : Exception
{
    public ProjectConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parses and renders the small TOML subset <c>&lt;project&gt;.motif.toml</c> needs: two fixed tables
/// (<c>[regression]</c>, <c>[apply]</c>) and one array of tables (<c>[[scope]]</c>), each with a closed set
/// of keys. No nested tables, dotted keys, or multiline strings — the file this project writes never needs
/// them, and a hand-rolled reader can refuse an unknown key exactly, which is the one requirement a general
/// TOML library's forgiving-by-design parsing would fight rather than help with.
/// </summary>
public static class ProjectConfigurationFile
{
    private const string RegressionTable = "regression";
    private const string ApplyTable = "apply";
    private const string ScopeTable = "scope";

    private static readonly HashSet<string> RegressionKeys = new(StringComparer.Ordinal) { "gate" };
    private static readonly HashSet<string> ApplyKeys = new(StringComparer.Ordinal) { "purge-on-apply" };

    private static readonly HashSet<string> ScopeKeys = new(StringComparer.Ordinal)
        { "name", "query", "assessor", "engine", "collect", "per-word-limit-ms" };

    /// <summary>
    /// Resolves TOML text into a fully-defaulted configuration. Malformed input, an unrecognised table, or
    /// an undeclared key raises <see cref="ProjectConfigurationException"/> naming the one-based line.
    /// </summary>
    public static ProjectConfiguration Parse(string text, string path)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);

        bool? gate = null;
        bool? purge = null;
        var scopes = new List<AssessmentScopeConfiguration>();
        string? section = null;
        var inScope = false;
        ScopeBuilder current = new();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var trimmed = StripComment(lines[index]).Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                FlushScope(scopes, ref current, ref inScope, path, lineNumber);
                var name = HeaderName(trimmed, "[[", "]]", path, lineNumber);
                if (name != ScopeTable)
                    throw Malformed(path, lineNumber, $"unknown table '[[{name}]]'");
                inScope = true;
                section = ScopeTable;
                continue;
            }

            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                FlushScope(scopes, ref current, ref inScope, path, lineNumber);
                var name = HeaderName(trimmed, "[", "]", path, lineNumber);
                if (name == ScopeTable)
                    throw Malformed(path, lineNumber, "'scope' must be declared as '[[scope]]', not '[scope]'");
                if (name != RegressionTable && name != ApplyTable)
                    throw Malformed(path, lineNumber, $"unknown table '[{name}]'");
                section = name;
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
                throw Malformed(path, lineNumber, "expected 'key = value'");
            var key = trimmed[..equals].Trim();
            var rawValue = trimmed[(equals + 1)..].Trim();
            if (key.Length == 0 || rawValue.Length == 0)
                throw Malformed(path, lineNumber, "expected 'key = value'");

            switch (section)
            {
                case null:
                    throw Malformed(path, lineNumber, $"key '{key}' is not inside any table");
                case RegressionTable:
                    if (!RegressionKeys.Contains(key)) throw UnknownKey(path, lineNumber, key, "[regression]");
                    gate = ParseBool(path, lineNumber, key, rawValue);
                    break;
                case ApplyTable:
                    if (!ApplyKeys.Contains(key)) throw UnknownKey(path, lineNumber, key, "[apply]");
                    purge = ParseBool(path, lineNumber, key, rawValue);
                    break;
                case ScopeTable:
                    if (!ScopeKeys.Contains(key)) throw UnknownKey(path, lineNumber, key, "[[scope]]");
                    current.Set(key, rawValue, path, lineNumber);
                    break;
            }
        }
        FlushScope(scopes, ref current, ref inScope, path, lines.Length + 1);

        if (scopes.Count == 0) scopes.Add(AssessmentScopeConfiguration.Default());
        var duplicate = scopes.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ProjectConfigurationException(
                $"{path}: scope name '{duplicate.Key}' is declared more than once.");

        return new ProjectConfiguration(scopes, gate ?? false, purge ?? true);
    }

    /// <summary>Renders a resolved configuration back to the same TOML shape <see cref="Parse"/> reads.</summary>
    public static string Render(ProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var sb = new StringBuilder();
        sb.AppendLine($"[{RegressionTable}]");
        sb.AppendLine($"gate = {Bool(configuration.GateOnRegression)}");
        sb.AppendLine();
        sb.AppendLine($"[{ApplyTable}]");
        sb.AppendLine($"purge-on-apply = {Bool(configuration.PurgeOnApply)}");

        foreach (var scope in configuration.Scopes)
        {
            sb.AppendLine();
            sb.AppendLine($"[[{ScopeTable}]]");
            sb.AppendLine($"name = {Quote(scope.Name)}");
            sb.AppendLine($"query = {Quote(scope.Query)}");
            sb.AppendLine($"assessor = {Quote(scope.Assessor)}");
            sb.AppendLine($"engine = {Quote(scope.Engine)}");
            sb.AppendLine($"collect = [{string.Join(", ", scope.Collect.Select(Quote))}]");
            sb.AppendLine($"per-word-limit-ms = {(long)scope.PerWordLimit.TotalMilliseconds}");
        }
        return sb.ToString();
    }

    private static void FlushScope(
        List<AssessmentScopeConfiguration> scopes, ref ScopeBuilder current, ref bool inScope,
        string path, int lineNumber)
    {
        if (!inScope) return;
        scopes.Add(current.Build(path, lineNumber));
        current = new ScopeBuilder();
        inScope = false;
    }

    private static string HeaderName(string trimmed, string open, string close, string path, int lineNumber)
    {
        if (!trimmed.EndsWith(close, StringComparison.Ordinal) || trimmed.Length < open.Length + close.Length)
            throw Malformed(path, lineNumber, $"expected a table header closed with '{close}'");
        return trimmed[open.Length..^close.Length].Trim();
    }

    /// One line at a time, quote-aware so a query string may contain '#' without truncating the value.
    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\')) inQuotes = !inQuotes;
            else if (c == '#' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static bool ParseBool(string path, int lineNumber, string key, string rawValue) => rawValue switch
    {
        "true" => true,
        "false" => false,
        _ => throw Malformed(path, lineNumber, $"'{key}' must be 'true' or 'false', found '{rawValue}'"),
    };

    private static string ParseString(string path, int lineNumber, string key, string rawValue)
    {
        if (rawValue.Length < 2 || rawValue[0] != '"' || rawValue[^1] != '"')
            throw Malformed(path, lineNumber, $"'{key}' must be a quoted string, found '{rawValue}'");
        return Unquote(rawValue);
    }

    private static List<string> ParseStringArray(string path, int lineNumber, string key, string rawValue)
    {
        if (rawValue.Length < 2 || rawValue[0] != '[' || rawValue[^1] != ']')
            throw Malformed(path, lineNumber, $"'{key}' must be an array of strings, found '{rawValue}'");
        var inner = rawValue[1..^1].Trim();
        if (inner.Length == 0) return new List<string>();

        var items = new List<string>();
        foreach (var rawItem in SplitTopLevel(inner))
        {
            var item = rawItem.Trim();
            if (item.Length == 0) continue;
            if (item.Length < 2 || item[0] != '"' || item[^1] != '"')
                throw Malformed(path, lineNumber, $"'{key}' entries must be quoted strings, found '{item}'");
            items.Add(Unquote(item));
        }
        return items;
    }

    private static IEnumerable<string> SplitTopLevel(string inner)
    {
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '"' && (i == 0 || inner[i - 1] != '\\')) inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                yield return inner[start..i];
                start = i + 1;
            }
        }
        yield return inner[start..];
    }

    private static string Unquote(string quoted) =>
        quoted[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static int ParsePositiveMilliseconds(string path, int lineNumber, string key, string rawValue)
    {
        if (!int.TryParse(rawValue, out var value) || value <= 0)
            throw Malformed(path, lineNumber, $"'{key}' must be a positive whole number, found '{rawValue}'");
        return value;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static ProjectConfigurationException Malformed(string path, int lineNumber, string reason) =>
        new($"{path}, line {lineNumber}: {reason}.");

    private static ProjectConfigurationException UnknownKey(string path, int lineNumber, string key, string table) =>
        new($"{path}, line {lineNumber}: unknown key '{key}' in {table}.");

    /// Accumulates one <c>[[scope]]</c> table's raw keys until the next header flushes it into a value.
    private sealed class ScopeBuilder
    {
        private string? _name;
        private string? _query;
        private string? _assessor;
        private string? _engine;
        private List<string>? _collect;
        private int? _perWordLimitMs;

        public void Set(string key, string rawValue, string path, int lineNumber)
        {
            switch (key)
            {
                case "name": _name = ParseString(path, lineNumber, key, rawValue); break;
                case "query": _query = ParseString(path, lineNumber, key, rawValue); break;
                case "assessor": _assessor = ParseString(path, lineNumber, key, rawValue); break;
                case "engine": _engine = ParseString(path, lineNumber, key, rawValue); break;
                case "collect": _collect = ParseStringArray(path, lineNumber, key, rawValue); break;
                case "per-word-limit-ms": _perWordLimitMs = ParsePositiveMilliseconds(path, lineNumber, key, rawValue); break;
            }
        }

        public AssessmentScopeConfiguration Build(string path, int lineNumber)
        {
            if (_name is null)
                throw new ProjectConfigurationException($"{path}: a '[[scope]]' table must declare 'name'.");
            return new AssessmentScopeConfiguration(
                _name,
                _query ?? AssessmentScopeConfiguration.DefaultQueryText,
                _assessor ?? AssessmentScopeConfiguration.DefaultAssessorName,
                _engine ?? AssessmentScopeConfiguration.DefaultEngineName,
                (IReadOnlyList<string>?)_collect ?? Array.Empty<string>(),
                _perWordLimitMs is { } ms
                    ? TimeSpan.FromMilliseconds(ms)
                    : AssessmentScopeConfiguration.DefaultPerWordLimit);
        }
    }
}
