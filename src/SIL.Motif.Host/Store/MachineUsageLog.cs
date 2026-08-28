using System.Text.Json;
using SIL.Motif.Projection.Usage;

namespace SIL.Motif.Host.Store;

/// <summary>
/// Writes <see cref="UsageLogEntry"/> rows to the machine store's <c>Usage</c> table, the database home
/// ADR 0021 decision 4's usage log moves to once <c>--store</c> is deleted. It is a database rather than
/// the file <see cref="UsageLogFile"/> still writes because several <c>motif</c> invocations may run at
/// once, and two processes appending to one file interleave where two connections into one SQLite
/// database do not.
/// </summary>
public sealed class MachineUsageLog
{
    private readonly MachineDatabase _database;

    public MachineUsageLog(MachineDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public void Append(UsageLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Usage (TimestampUtc, Command, ArgumentShapeJson)
            VALUES ($timestamp, $command, $shape);
            """;
        command.Parameters.AddWithValue("$timestamp", entry.TimestampUtc);
        command.Parameters.AddWithValue("$command", entry.Command);
        command.Parameters.AddWithValue("$shape", JsonSerializer.Serialize(entry.ArgumentShape));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<UsageLogEntry> ReadAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TimestampUtc, Command, ArgumentShapeJson FROM Usage ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var entries = new List<UsageLogEntry>();
        while (reader.Read())
        {
            var shape = JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(2))
                ?? throw new InvalidDataException("The persisted usage row has a malformed argument shape.");
            entries.Add(new UsageLogEntry(reader.GetString(0), reader.GetString(1), shape));
        }
        return entries;
    }
}
