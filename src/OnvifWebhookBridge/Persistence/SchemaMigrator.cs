namespace OnvifWebhookBridge.Persistence
{
    using Microsoft.Data.Sqlite;

    internal static class SchemaMigrator
    {
        internal const int CurrentVersion = 4;
        internal static void Migrate(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var version = Command(connection, transaction, "PRAGMA user_version");
            var current = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (current is < 0 or > CurrentVersion)
            {
                throw new StorageException("UnsupportedSchema");
            }
            for (var next = current + 1; next <= CurrentVersion; next++)
            {
                using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream(
                    $"OnvifWebhookBridge.Persistence.Migrations.{next:000}.sql")!;
                using var reader = new StreamReader(stream);
                using var migration = Command(connection, transaction, reader.ReadToEnd());
                migration.ExecuteNonQuery();
                using var advance = Command(connection, transaction, $"PRAGMA user_version={next}");
                advance.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
            string sql, params object?[] values)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (var i = 0; i < values.Length; i++)
            {
                command.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
            }
            return command;
        }
    }
}
