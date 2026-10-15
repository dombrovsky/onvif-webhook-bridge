namespace OnvifWebhookBridge.Tests.Persistence
{
    using Microsoft.Data.Sqlite;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Rules;

    // Shared storage acceptance checks run unchanged on JIT and actual Linux NativeAOT.
    internal static class StoragePath
    {
        internal static CameraPipeline Pipeline() => new(new RuleState(new EventHistory()), new DispatchQueue(), TimeProvider.System);
        internal static SecretEdit<T> Replace<T>(T value) where T : class => new(SecretOperation.Replace, value);
        internal static CameraEdit Camera(bool enabled = true) => new("camera'", "Synthetic camera", "https://camera.example/device", enabled,
            Replace("synthetic-user"), Replace("synthetic-password"));
        internal static RuleEdit Rule() => new(new("rule'", "camera'", "Synthetic/Detection",
            new(FieldSection.Source, "Synthetic", "true", ComparisonMode.Boolean), true, OccurrenceMode.Transition, TimeSpan.FromSeconds(5)),
            "Synthetic rule", "POST", 2, Replace("https://destination.example/action?synthetic=secret"),
            Replace(new[] { new ActionHeader("X-Synthetic", "synthetic-token") }), Replace("{\"synthetic\":\"secret\"}"));

        public static void Verify()
        {
            using var temp = new TemporaryStorage();
            var originalQueue = new DispatchQueue();
            var pipeline = new CameraPipeline(new RuleState(new EventHistory()), originalQueue, TimeProvider.System);
            var store = new ConfigurationStore(temp.Path, pipeline);
            store.Initialize();
            Check(store.Read() is { Revision: 0, HasAdministrator: false }, "EmptyDatabase");
            Check(store.Save(0, [Camera()], [Rule()]) == 1, "FirstCommit");
            Check(store.BootstrapAdministrator("synthetic-admin", "synthetic-admin-password"), "Bootstrap");
            Check(!store.BootstrapAdministrator("other-synthetic", "other-synthetic-password"), "BootstrapPreserves");
            var view = store.Read();
            Check(view.Rules.Single().Match == new ForwardingRule("rule'", "camera'", "Synthetic/Detection",
                new(FieldSection.Source, "Synthetic", "true", ComparisonMode.Boolean), true, OccurrenceMode.Transition, TimeSpan.FromSeconds(5)) &&
                view.Rules.Single() is { Method: "POST", AdditionalAttempts: 2 }, "FullRuleRoundTrip");
            Check(view.HasAdministrator && view.Cameras.Single() is { HasPassword: true, HasUsername: true } &&
                view.Rules.Single() is { HasUrl: true, HasHeaders: true, HasJsonBody: true, Name: "Synthetic rule" }, "MaskedPresence");
            Check(!string.Join(" ", view.Rules).Contains("synthetic-token", StringComparison.Ordinal) &&
                !string.Join(" ", view.Cameras).Contains("synthetic-password", StringComparison.Ordinal), "MaskedReads");
            var original = pipeline.Cameras.Single();
            Check(pipeline.ManualTest("rule'"), "QueuedBeforeSave");
            var job = originalQueue.TakeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var queue = new DispatchQueue();
            // A fresh host restores config but never previous jobs/status/history/state.
            var history = new EventHistory();
            var restarted = new CameraPipeline(new RuleState(history), queue, TimeProvider.System);
            var restartStore = new ConfigurationStore(temp.Path, restarted);
            restartStore.Initialize();
            Check(restarted.Cameras.Single() == original && restarted.Status.Count == 0 && history.Snapshot().Count == 0,
                "RestoredConfigurationTransientRuntime");
            Check(queue.Stop().Count == 0, "QueueNeverRestored");
            var match = view.Rules.Single().Match;
            store.Save(1, [new("camera'", "Synthetic camera", original.Endpoint)], [new(match, "Synthetic rule", "POST", 2)]);
            Check(ReferenceEquals(pipeline.Cameras.Single(), original), "UnchangedCameraSnapshot");
            Check(pipeline.Eligible(job), "EquivalentSavePreservesQueuedEligibility");
            using (var connection = temp.Open())
            {
                Check(Scalar(connection, "SELECT Password FROM Cameras") == "synthetic-password", "PreservedPassword");
                Check(Scalar(connection, "SELECT Value FROM Headers") == "synthetic-token", "PreservedHeaders");
                var hash = Scalar(connection, "SELECT PasswordHash FROM Administrator");
                Check(hash.StartsWith("pbkdf2-sha256$600000$", StringComparison.Ordinal) && !hash.Contains("synthetic-admin-password", StringComparison.Ordinal), "HashOnly");
                var parts = hash.Split('$');
                Check(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("synthetic-admin-password", Convert.FromBase64String(parts[2]),
                    600_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32).SequenceEqual(Convert.FromBase64String(parts[3])), "RecoverableHashFormat");
                Execute(connection, "CREATE TRIGGER FailSave BEFORE INSERT ON Rules BEGIN SELECT RAISE(ABORT,'synthetic-secret-must-not-escape'); END");
            }
            var failure = Throws<StorageException>(() => store.Save(2, [Camera(false)], [Rule()]));
            Check(!failure.ToString().Contains("synthetic-secret-must-not-escape", StringComparison.Ordinal), "SafeStorageError");
            Check(ReferenceEquals(pipeline.Cameras.Single(), original) && store.Read().Revision == 2, "RollbackKeepsActive");
            using (var connection = temp.Open())
            {
                Execute(connection, "DROP TRIGGER FailSave");
                Throws<SqliteException>(() => Execute(connection, "DELETE FROM Cameras"));
                Throws<SqliteException>(() => Execute(connection, "INSERT INTO Headers VALUES('missing',0,'X-Test','value')"));
            }
            Throws<RevisionConflictException>(() => restartStore.Save(1, [], []));
            Throws<ArgumentException>(() => store.Save(2, [], [Rule()]));
            Throws<ArgumentException>(() => store.Save(2, [new("camera'", "Synthetic", original.Endpoint,
                password: new(SecretOperation.Clear))], [Rule()]));
            Throws<ArgumentException>(() => store.Save(2, [Camera()], [new(match, "Synthetic", url: new(SecretOperation.Clear))]));
            Throws<ArgumentException>(() => store.Save(2, [new("camera'", "Synthetic", original.Endpoint,
                password: new((SecretOperation)99))], [Rule()]));
            Throws<ArgumentException>(() => store.Save(2, Enumerable.Range(0, 65).Select(i =>
                new CameraEdit("oversized" + i, "Synthetic", "https://camera.example/device")), []));
            Throws<ArgumentException>(() => store.Save(2, [Camera()], [new(match with { Mode = (OccurrenceMode)99 }, "Synthetic", url: Replace("https://destination.example"))]));
            Check(store.Read().Revision == 2 && ReferenceEquals(pipeline.Cameras.Single(), original), "InvalidUnchanged");
            store.Save(2, [new("camera'", "Synthetic camera", original.Endpoint,
                username: new(SecretOperation.Clear), password: new(SecretOperation.Clear))],
                [new(match, "Synthetic rule", "POST", url: Replace("https://destination.example/replaced"),
                    headers: new(SecretOperation.Clear), jsonBody: new(SecretOperation.Clear))]);
            Check(store.Read().Cameras.Single() is { HasPassword: false, HasUsername: false } &&
                store.Read().Rules.Single() is { HasHeaders: false, HasJsonBody: false }, "ExplicitClear");
            Check(!pipeline.Eligible(job), "CommittedEditInvalidatesQueuedJob");
            using (var connection = temp.Open())
            {
                Check(Scalar(connection, "SELECT Url FROM Rules") == "https://destination.example/replaced", "ExplicitReplace");
            }

            // SQLite online backup creates a consistent standalone snapshot, including WAL content.
            using var backup = new TemporaryStorage();
            Directory.CreateDirectory(backup.Path);
            using (var source = temp.Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup.Database, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
            }
            var restored = new ConfigurationStore(backup.Path, Pipeline());
            restored.Initialize();
            Check(restored.Read().Revision == 3 && restored.Read().HasAdministrator, "BackupRestore");
            VerifyMigration();
            VerifyMigrationRollback();
            VerifyCorruption();
            if (OperatingSystem.IsLinux())
            {
                VerifyLinux(temp, store);
            }
            using var versionConnection = temp.Open();
            Check(versionConnection.ServerVersion.Length > 0, "NativeLoading");
            Console.WriteLine("PASS: SQLite " + versionConnection.ServerVersion + "; storage transactions, migrations, masking, backup/restore and platform permissions.");
        }

        private static void VerifyMigration()
        {
            using var temp = new TemporaryStorage();
            Directory.CreateDirectory(temp.Path);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.Database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var stream = typeof(ConfigurationStore).Assembly.GetManifestResourceStream("OnvifWebhookBridge.Persistence.Migrations.001.sql")!;
                using var reader = new StreamReader(stream);
                Execute(connection, reader.ReadToEnd());
                Execute(connection, "PRAGMA user_version=1; INSERT INTO Cameras VALUES('earlier','Synthetic earlier','https://camera.example/device',NULL,NULL,0)");
            }
            var store = new ConfigurationStore(temp.Path, Pipeline());
            store.Initialize();
            Check(store.Read().Cameras.Single().Id == "earlier" && store.Read().Rules.Count == 0, "MigrationPreserves");
            using var verify = temp.Open();
            Check(Scalar(verify, "PRAGMA user_version") == "2", "MigrationVersion");
            Execute(verify, "PRAGMA user_version=99");
            Throws<StorageException>(() => store.Initialize());
            Check(Scalar(verify, "PRAGMA user_version") == "99", "FutureSchemaUntouched");
        }

        private static void VerifyCorruption()
        {
            using var temp = new TemporaryStorage();
            Directory.CreateDirectory(temp.Path);
            var bytes = System.Text.Encoding.UTF8.GetBytes("synthetic-corrupt-secret");
            File.WriteAllBytes(temp.Database, bytes);
            var failure = Throws<StorageException>(() => new ConfigurationStore(temp.Path, Pipeline()).Initialize());
            Check(!failure.ToString().Contains("synthetic-corrupt-secret", StringComparison.Ordinal) &&
                File.ReadAllBytes(temp.Database).SequenceEqual(bytes), "CorruptionPreserved");
        }

        private static void VerifyMigrationRollback()
        {
            using var temp = new TemporaryStorage();
            Directory.CreateDirectory(temp.Path);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp.Database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var stream = typeof(ConfigurationStore).Assembly.GetManifestResourceStream("OnvifWebhookBridge.Persistence.Migrations.001.sql")!;
                using var reader = new StreamReader(stream);
                Execute(connection, reader.ReadToEnd());
                // A conflicting table forces migration 2 to fail. Migration 1/version/data must survive.
                Execute(connection, "PRAGMA user_version=1; CREATE TABLE Headers(Synthetic TEXT); INSERT INTO Headers VALUES('synthetic-preserved')");
            }
            Throws<StorageException>(() => new ConfigurationStore(temp.Path, Pipeline()).Initialize());
            using var verify = temp.Open();
            Check(Scalar(verify, "PRAGMA user_version") == "1" && Scalar(verify, "SELECT Synthetic FROM Headers") == "synthetic-preserved" &&
                Scalar(verify, "SELECT COUNT(*) FROM sqlite_master WHERE name='Rules'") == "0", "MigrationRollback");
        }

        private static void VerifyLinux(TemporaryStorage temp, ConfigurationStore store)
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }
            Check(File.GetUnixFileMode(temp.Path) == (UnixFileMode)448, "Directory0700");
            // Keep a WAL connection alive and force SHM creation, then inspect actual sidecars.
            using (var connection = temp.Open())
            {
                Execute(connection, "UPDATE Configuration SET Revision=Revision");
                foreach (var path in new[] { temp.Database, temp.Database + "-wal", temp.Database + "-shm" })
                {
                    Check(File.Exists(path) && File.GetUnixFileMode(path) == (UnixFileMode)384, "Files0600");
                }
            }
            File.SetUnixFileMode(temp.Database, UnixFileMode.UserRead);
            Throws<StorageException>(() => store.Save(3, [], []));
            File.SetUnixFileMode(temp.Database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Throws<StorageException>(() => store.Initialize());
            File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(temp.Database, UnixFileMode.None);
            Throws<StorageException>(() => store.Initialize());
            File.SetUnixFileMode(temp.Database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var link = new TemporaryStorage();
            Directory.CreateDirectory(link.Path);
            File.CreateSymbolicLink(link.Database, temp.Database);
            Throws<StorageException>(() => new ConfigurationStore(link.Path, Pipeline()).Initialize());
            Check(store.Read().Revision == 3, "PermissionFailuresPreserveData");
            // The Docker runner supplies root-owned fixtures while this executable runs as APP_UID.
            var ownershipFixtures = Environment.GetEnvironmentVariable("ONVIF_TEST_OWNERSHIP_FIXTURES");
            if (ownershipFixtures is not null)
            {
                foreach (var name in new[] { "directory", "database", "wal", "shm" })
                {
                    var failure = Throws<StorageException>(() => new ConfigurationStore(System.IO.Path.Combine(ownershipFixtures, name), Pipeline()).Initialize());
                    Check(failure.Message == "Configuration storage: Ownership", "ForeignOwnerRejected");
                }
            }
        }

        internal static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        internal static string Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }
        internal static T Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T error) { return error; }
            throw new InvalidOperationException("ExpectedFailure:" + typeof(T).Name);
        }
        internal static void Check(bool value, string category)
        {
            if (!value)
            {
                throw new InvalidOperationException(category);
            }
        }
    }

    internal sealed class TemporaryStorage : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bridge-storage-" + Guid.NewGuid().ToString("N"));
        internal string Database => System.IO.Path.Combine(Path, "configuration.db");
        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Database,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            connection.Open();
            return connection;
        }
        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
