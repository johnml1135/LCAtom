using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SIL.Motif.Projection.Usage;

/// <summary>
/// Persists a <see cref="UsageLog"/> as newline-delimited JSON so it accumulates across CLI
/// invocations — each <c>motif</c> call is its own process, so an in-memory-only log would never
/// outlive the command that produced it. One line per <see cref="UsageLogEntry"/>; append-only, so a
/// concurrent writer can never corrupt an entry another process already wrote.
/// </summary>
public static class UsageLogFile
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Append(string path, UsageLogEntry entry)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.AppendAllText(path, JsonSerializer.Serialize(entry, Options) + Environment.NewLine);
    }

    public static IReadOnlyList<UsageLogEntry> ReadAll(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<UsageLogEntry>();

        var entries = new List<UsageLogEntry>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entry = JsonSerializer.Deserialize<UsageLogEntry>(line, Options);
            if (entry is not null)
                entries.Add(entry);
        }

        return entries;
    }
}
