namespace OnvifWebhookBridge.Persistence
{
    using Microsoft.Data.Sqlite;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Rules;
    using System.Security.Cryptography;
    using static SchemaMigrator;

    // One process owns this directory. All durable mutations go through this coordinator.
    // No SQLite calls occur on the camera/event/dispatch hot path.
    public sealed class ConfigurationStore
    {
        private readonly object gate = new();
        private readonly string directory;
        private readonly string database;
        private readonly CameraPipeline pipeline;

        public ConfigurationStore(string directory, CameraPipeline pipeline)
        {
            this.directory = Path.GetFullPath(directory);
            database = Path.Combine(this.directory, "configuration.db");
            this.pipeline = pipeline;
        }

        public void Initialize() => Guard(() =>
        {
            lock (gate)
            {
                StorageFiles.Prepare(directory, database);
                if (!File.Exists(database))
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (OperatingSystem.IsLinux())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    }
                    using var file = new FileStream(database, options);
                }
                using var connection = Open();
                using (var check = Command(connection, null, "PRAGMA quick_check"))
                {
                    if (!Equals(check.ExecuteScalar(), "ok"))
                    {
                        throw new StorageException("Corrupt");
                    }
                }
                Migrate(connection);
                using (var wal = Command(connection, null, "PRAGMA journal_mode=WAL"))
                {
                    if (!Equals(wal.ExecuteScalar(), "wal"))
                    {
                        throw new StorageException("JournalMode");
                    }
                }
                StorageFiles.Prepare(directory, database);
                using var transaction = connection.BeginTransaction();
                var restored = Load(connection, transaction);
                transaction.Commit();
                pipeline.ConfigureCameras(restored.Cameras, restored.Rules.Select(r => r.Rule));
                return true;
            }
        });

        public ConfigurationView Read() => Guard(() =>
        {
            lock (gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var value = Load(connection, transaction);
                transaction.Commit();
                return new ConfigurationView(value.Revision,
                    Array.AsReadOnly(value.Cameras.Select(c => new CameraView(c.Id, c.Name, c.Endpoint, c.Enabled,
                        c.Username is not null, c.Password is not null)).ToArray()),
                    Array.AsReadOnly(value.Rules.Select(r => new RuleView(r.Rule.Match, r.Name, r.Rule.Action.Method,
                        r.Rule.Action.AdditionalAttempts, true, r.Rule.Action.Headers.Count != 0, r.Rule.Action.JsonBody is not null, r.Rule.Action.IgnoreCertificateValidation)).ToArray()),
                    value.HasAdministrator);
            }
        });

        public long Save(long expectedRevision, IEnumerable<CameraEdit> cameras, IEnumerable<RuleEdit> rules) => Guard(() =>
        {
            var cameraEdits = cameras.Take(65).ToArray();
            var ruleEdits = rules.Take(257).ToArray();
            lock (gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                var old = Load(connection, transaction);
                if (old.Revision != expectedRevision)
                {
                    throw new RevisionConflictException();
                }
                var next = new StoredConfiguration(checked(expectedRevision + 1),
                    cameraEdits.Select(c => c.Resolve(old.Cameras.SingleOrDefault(x => x.Id == c.Id))).ToArray(),
                    ruleEdits.Select(r => r.Resolve(old.Rules.SingleOrDefault(x => x.Rule.Match.Id == r.Id))).ToArray(), old.HasAdministrator);
                Validate(next);
                Execute(connection, transaction, "DELETE FROM Rules");
                Execute(connection, transaction, "DELETE FROM Cameras");
                foreach (var c in next.Cameras)
                {
                    Execute(connection, transaction, "INSERT INTO Cameras VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                        c.Id, c.Name, c.Endpoint, c.Username, c.Password, c.Enabled);
                }
                foreach (var stored in next.Rules)
                {
                    var r = stored.Rule.Match;
                    var a = stored.Rule.Action;
                    Execute(connection, transaction, "INSERT INTO Rules VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)",
                        r.Id, r.CameraId, stored.Name, r.Topic, r.Condition is { } f ? (int)f.Section : null,
                        r.Condition?.Name, r.Condition?.ExpectedValue, r.Condition is { } condition ? (int)condition.Comparison : null,
                        r.Enabled, (int)r.Mode, r.Cooldown.Ticks, a.Address.AbsoluteUri, a.Method, a.JsonBody, a.AdditionalAttempts, a.IgnoreCertificateValidation);
                    for (var i = 0; i < a.Headers.Count; i++)
                    {
                        Execute(connection, transaction, "INSERT INTO Headers VALUES($p0,$p1,$p2,$p3)", r.Id, i, a.Headers[i].Name, a.Headers[i].Value);
                    }
                }
                Execute(connection, transaction, "UPDATE Configuration SET Revision=$p0 WHERE Id=1", next.Revision);
                StorageFiles.Prepare(directory, database);
                transaction.Commit();
                pipeline.ConfigureCameras(next.Cameras, next.Rules.Select(r => r.Rule));
                return next.Revision;
            }
        });

        // Stores only an adaptive salted hash. Setup/session/reset APIs belong to phase 6.
        public bool BootstrapAdministrator(string username, string password) => Guard(() =>
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || password.Length is < 12 or > 4096)
            {
                throw new ArgumentException("Invalid administrator bootstrap.");
            }
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
            var encoded = "pbkdf2-sha256$600000$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
            lock (gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                using var insert = Command(connection, transaction,
                    "INSERT INTO Administrator SELECT 1,$p0,$p1 WHERE NOT EXISTS(SELECT 1 FROM Administrator)", username, encoded);
                var added = insert.ExecuteNonQuery() == 1;
                StorageFiles.Prepare(directory, database);
                transaction.Commit();
                return added;
            }
        });

        internal bool VerifyAdministrator(string username, string password) => Guard(() =>
        {
            string? name = null, encoded = null;
            lock (gate)
            {
                using var connection = Open();
                using var command = Command(connection, null, "SELECT Username,PasswordHash FROM Administrator WHERE Id=1");
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    name = reader.GetString(0);
                    encoded = reader.GetString(1);
                }
            }
            // Perform the adaptive work for unknown accounts too. Never return the hash.
            var parts = encoded?.Split('$');
            var valid = parts is { Length: 4 } && parts[0] == "pbkdf2-sha256" && parts[1] == "600000";
            byte[] salt, expected;
            try
            {
                salt = valid ? Convert.FromBase64String(parts![2]) : new byte[16];
                expected = valid ? Convert.FromBase64String(parts![3]) : new byte[32];
            }
            catch (FormatException) { throw new StorageException("InvalidAdministrator"); }
            if (salt.Length != 16 || expected.Length != 32)
            {
                throw new StorageException("InvalidAdministrator");
            }
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected) && valid && username == name;
        });

        // Offline, explicitly requested command only; no HTTP reset route.
        internal void ResetAdministrator(string username, string password) => Guard(() =>
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || password.Length is < 12 or > 4096)
            {
                throw new ArgumentException("Invalid administrator bootstrap.");
            }
            var salt = RandomNumberGenerator.GetBytes(16);
            var encoded = "pbkdf2-sha256$600000$" + Convert.ToBase64String(salt) + "$" +
                Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32));
            lock (gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                Execute(connection, transaction, "INSERT INTO Administrator VALUES(1,$p0,$p1) ON CONFLICT(Id) DO UPDATE SET Username=$p0,PasswordHash=$p1", username, encoded);
                StorageFiles.Prepare(directory, database);
                transaction.Commit();
                return true;
            }
        });

        private SqliteConnection Open()
        {
            StorageFiles.Prepare(directory, database);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                Pooling = false,
                DefaultTimeout = 2
            }.ToString());
            try
            {
                connection.Open();
                Execute(connection, null, "PRAGMA synchronous=FULL");
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        private static StoredConfiguration Load(SqliteConnection connection, SqliteTransaction transaction)
        {
            try
            {
                using var foreignKeys = Command(connection, transaction, "PRAGMA foreign_key_check");
                using (var reader = foreignKeys.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        throw new StorageException("InvalidReferences");
                    }
                }
                var value = ReadRows(connection, transaction);
                Validate(value);
                return value;
            }
            catch (Exception error) when (error is ArgumentException or InvalidCastException or OverflowException)
            {
                throw new StorageException("InvalidConfiguration");
            }
        }

        private static StoredConfiguration ReadRows(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var revision = Command(connection, transaction, "SELECT Revision FROM Configuration WHERE Id=1");
            var number = (long)(revision.ExecuteScalar() ?? throw new StorageException("InvalidSchema"));
            var cameras = new List<CameraConfiguration>();
            using (var command = Command(connection, transaction, "SELECT Id,Name,Endpoint,Username,Password,Enabled FROM Cameras LIMIT 65"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cameras.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), reader.GetBoolean(5)));
                }
            }
            var headers = new Dictionary<string, List<ActionHeader>>(StringComparer.Ordinal);
            using (var command = Command(connection, transaction, "SELECT RuleId,Name,Value FROM Headers ORDER BY RuleId,Position LIMIT 8449"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!headers.TryGetValue(id, out var list))
                    {
                        headers[id] = list = [];
                    }
                    list.Add(new(reader.GetString(1), reader.GetString(2)));
                }
            }
            var rules = new List<StoredRule>();
            using (var command = Command(connection, transaction, "SELECT Id,CameraId,Name,Topic,Section,FieldName,ExpectedValue,Comparison,Enabled,Mode,CooldownTicks,Url,Method,JsonBody,AdditionalAttempts,IgnoreCertificateValidation FROM Rules LIMIT 257"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var condition = reader.IsDBNull(4) ? null : new FieldCondition((FieldSection)reader.GetInt32(4),
                        reader.GetString(5), reader.GetString(6), (ComparisonMode)reader.GetInt32(7));
                    rules.Add(new(reader.GetString(2), new(new(id, reader.GetString(1), reader.GetString(3), condition,
                        reader.GetBoolean(8), (OccurrenceMode)reader.GetInt32(9), TimeSpan.FromTicks(reader.GetInt64(10))),
                        new(reader.GetString(11), reader.GetString(12), headers.GetValueOrDefault(id), Text(reader, 13), reader.GetInt32(14), reader.GetBoolean(15)))));
                }
            }
            using var admin = Command(connection, transaction, "SELECT COUNT(*) FROM Administrator");
            return new(number, cameras.ToArray(), rules.ToArray(), (long)admin.ExecuteScalar()! == 1);
        }

        private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object?[] values)
        {
            using var command = Command(connection, transaction, sql, values);
            command.ExecuteNonQuery();
        }
        private static void Validate(StoredConfiguration value)
        {
            if (value.Revision < 0 || value.Rules.Any(r => r.Name.Length > 4096))
            {
                throw new ArgumentException("Invalid configuration.");
            }
            var history = new EventHistory();
            var validator = new CameraPipeline(new RuleState(history), new DispatchQueue(), TimeProvider.System);
            validator.ConfigureCameras(value.Cameras, value.Rules.Select(r => r.Rule));
        }
        private static T Guard<T>(Func<T> operation)
        {
            try
            {
                return operation();
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                throw new StorageException("UnavailableOrInvalid");
            }
        }
    }
}
