using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>Legacy store opener retained while the CLI transitions to the worker-owned database.</summary>
internal static class SqliteMotifDatabase
{
    public static SqliteConnection OpenConnection(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        MotifSchema.ConfigureConnection(connection);
        MotifSchema.EnsureLegacyTables(connection);
        return connection;
    }
}
