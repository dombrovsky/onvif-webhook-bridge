# Configuration storage

`ONVIF_DATA_DIRECTORY` defaults to `/data` on Linux and `data` beside the executable
on other systems. It contains `configuration.db` and SQLite WAL/SHM or journal
sidecars. Use a dedicated private directory: the store restricts the directory itself.
[Deployment](DEPLOYMENT.md) owns container setup and upgrades;
[administration](ADMINISTRATION.md) owns bootstrap, reset, authentication and the API.

## Configuration contract

`AddPersistentCameraPipeline(directory)` registers the concrete store, then a startup service ahead of the existing dispatcher and supervisor. Startup checks storage, runs migrations and validates/restores a complete snapshot before workers start. Enabled cameras reconcile through the existing supervisor. Disabled cameras and their rules remain configured. Storage failure aborts startup with a fixed category; the application never deletes, renames, resets or replaces an existing database as recovery.

In an authorized in-process caller, obtain `ConfigurationStore` from DI. `Read()` returns a masked snapshot and its revision. `Save(expectedRevision, cameraEdits, ruleEdits)` replaces the complete configuration; omitted entries are removed. Use this coordinator for every persistent mutation; the old `CameraPipeline.ConfigureCameras` entry point remains for explicitly in-memory hosts/tests and does not persist. One process/store singleton owns a data directory. Sharing a live database between bridge processes is unsupported, even though SQLite transactions enforce revision conflicts between concurrent writers.

Detached validation uses a pipeline with an explicit rejecting admission callback; it does not allocate a TaskFlow. The store checks revisions inside a bounded immediate write transaction, resolves secret edits, validates detached configurations using the existing pipeline/rule/action validators, writes parameterized SQL and commits with synchronous FULL. Only after commit does it update the active pipeline. SQL errors, invalid configuration and stale revisions leave both durable and active configuration unchanged. Equal action values retain unchanged rule revisions, cooldowns and transition state. Committed edits invalidate queued jobs through the existing revision checks; the existing cleanup-before-replacement supervisor remains responsible for workers. Persistence never runs in event admission or HTTP dispatch.

Foreign keys are enabled on every store connection. Rules reference cameras with `ON DELETE RESTRICT`; headers reference rules with cascading deletion. A camera cannot be removed while any retained rule references it. A full replacement may remove the referencing rules and camera atomically. The existing limits of 64 cameras and 256 rules apply, together with existing string, header, JSON and HTTP limits. Rule friendly names are stored separately from the unchanged matcher record and are capped at 4096 UTF-16 units.

Configuration reads use masked views; credential/header/body values and sensitive
URLs are write-only. [The API edit contract](ADMINISTRATION.md#routes-and-edits)
defines Preserve/Replace/Clear and URL visibility. Never serialize edit objects,
internal rows or active pipeline snapshots to clients or diagnostics. Administrator
hash format and setup/reset policy live in [administration](ADMINISTRATION.md#initial-setup).
Do not delete a database to reset a password.

## Schema and dependencies

Embedded [SQL migrations](../src/OnvifWebhookBridge/Persistence/Migrations) advance
`PRAGMA user_version` transactionally. Current schema is **4**:

| Version | Change |
| --- | --- |
| 1 | Configuration revision, cameras and administrator hash |
| 2 | Named rules and ordered headers |
| 3 | Per-rule certificate-validation exemption, default false |
| 4 | URL sensitivity, default true for existing rules |

Supported older schemas migrate without discarding data. Future schemas, corruption
or invalid restored configuration fail closed without resetting/replacing the DB.

The [application project file](../src/OnvifWebhookBridge/OnvifWebhookBridge.csproj)
defines the Microsoft.Data.Sqlite version. Its bundled SQLitePCLRaw assets are used
under JIT and NativeAOT. No ORM or system SQLite fallback is used. Dependency/runtime upgrades
must verify actual native loading on both supported targets. See
[Microsoft's bundle guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
and [native validation](DEVELOPMENT.md#validation).

## Ownership, permissions and failures

Camera credentials and complete HTTP action configuration are recoverable plaintext in SQLite. Filesystem restrictions protect them; **this is not encryption at rest**. Treat the database, WAL, SHM, rollback journals and backups as secrets.

On Linux the final data directory must belong to the effective application UID. Existing DB/sidecar files must also belong to that UID and be regular files. The store checks ownership/type with Linux `statx` and rejects final-path symlinks. Directory ancestors/mounts must be controlled by the operator; do not allow another process with the same UID to modify this directory. Missing directories are created with mode 0700; an absent DB is created with mode 0600 before SQLite opens it. Existing owner-accessible files/directories are tightened to exactly 0600/0700. Owner read/write (and directory execute) must already exist: deliberately unreadable/read-only storage is rejected, not made writable. Root ownership is not adopted or recursively changed. SQLite inherits 0600 database permissions for sidecars within the private directory; ownership/modes are rechecked before writes commit.

Before mounting an existing volume, arrange ownership for the intended container UID and restrict the directory/DB/sidecars to 0700/0600 on the host. Do not solve ownership failures by running the bridge as root or making the volume world-writable. The automated runner uses the existing non-root runtime account and separately verifies rejection of root-owned directories, databases, WAL and SHM files. Windows storage tests verify SQL behavior only: the code does not provision Windows ACLs, and Windows tests are not Linux permission evidence. Restrict Windows test directories using OS ACLs when they contain anything beyond synthetic data.

Fixed errors distinguish unsupported schema, corrupt/invalid data, ownership/permissions, unsafe file and unavailable storage. They retain no provider exception as an inner exception and do not include paths, SQL, URLs, credentials, headers or bodies. A failed save leaves the running configuration in place and can be retried with the same revision after repairing storage. Revision conflicts require rereading the masked snapshot and reviewing/resubmitting the edit. No automatic reset, deletion or recovery from backups occurs.

## Backup and restore

Use SQLite's online backup API/tool for a consistent backup while WAL is active. Copying only the main DB while the process is running can lose committed WAL content. Alternatively stop the application cleanly, then copy the complete data directory without discarding sidecars. Create backups in a private directory with restricted ownership/permissions; protect their transfer and retention as carefully as the live secrets. The tests use `SqliteConnection.BackupDatabase` and restore its standalone snapshot into a fresh private directory.

Restore only with the bridge stopped. Keep the old directory intact, restore the chosen consistent backup into a separate correctly owned directory, and point `ONVIF_DATA_DIRECTORY` at it. Startup validates schema/configuration before starting workers. Restoring configuration re-enables saved enabled cameras/actions; review it before starting. Restart restores no queued action or prior execution result and establishes fresh transition baselines/cooldowns. A backup is sensitive plaintext and does not establish real-device or destination interoperability.
