# Configuration storage

Phase 5 stores configuration only. The executable uses `ONVIF_DATA_DIRECTORY`, defaulting to `/data` on Linux and `data` beside the executable on Windows. The directory contains `configuration.db` and SQLite WAL/SHM or rollback-journal sidecars. Do not point this setting at a shared/general-purpose directory: the application restricts the selected directory itself. Phase 6 exposes authenticated configuration, event, status and test APIs; `/health` is the sole anonymous read. Administrator setup/reset and API protections are documented in [ADMINISTRATION.md](ADMINISTRATION.md). UI and production deployment packaging remain later work.

## Configuration contract

`AddPersistentCameraPipeline(directory)` registers the concrete store, then a startup service ahead of the existing dispatcher and supervisor. Startup checks storage, runs migrations and validates/restores a complete snapshot before workers start. Enabled cameras reconcile through the existing supervisor. Disabled cameras and their rules remain configured. Storage failure aborts startup with a fixed category; the application never deletes, renames, resets or replaces an existing database as recovery.

In an authorized in-process caller, obtain `ConfigurationStore` from DI. `Read()` returns a masked snapshot and its revision. `Save(expectedRevision, cameraEdits, ruleEdits)` replaces the complete configuration; omitted entries are removed. Use this coordinator for every persistent mutation; the old `CameraPipeline.ConfigureCameras` entry point remains for explicitly in-memory hosts/tests and does not persist. One process/store singleton owns a data directory. Sharing a live database between bridge processes is unsupported, even though SQLite transactions enforce revision conflicts between concurrent writers.

The store checks revisions inside a bounded immediate write transaction, resolves secret edits, validates detached configurations using the existing pipeline/rule/action validators, writes parameterized SQL and commits with synchronous FULL. Only after commit does it update the active pipeline. SQL errors, invalid configuration and stale revisions leave both durable and active configuration unchanged. Equal action values retain unchanged rule revisions, cooldowns and transition state. Committed edits invalidate queued jobs through the existing revision checks; the existing cleanup-before-replacement supervisor remains responsible for workers. Persistence never runs in event admission or HTTP dispatch.

Foreign keys are enabled on every store connection. Rules reference cameras with `ON DELETE RESTRICT`; headers reference rules with cascading deletion. A camera cannot be removed while any retained rule references it. A full replacement may remove the referencing rules and camera atomically. The existing limits of 64 cameras and 256 rules apply, together with existing string, header, JSON and HTTP limits. Rule friendly names are stored separately from the unchanged matcher record and are capped at 4096 UTF-16 units.

`SecretEdit<T>` has three explicit operations:

- Preserve (the default): keep the stored value for that same stable ID; no replacement value is allowed.
- Replace: supply a non-null value, validated before commit. Empty strings are subject to the existing configuration validators.
- Clear: remove the optional value; no replacement value is allowed. Clear both camera username/password together for an unauthenticated camera. Clear optional action headers/body independently. A required action URL cannot be cleared from a retained rule; delete the rule or replace its URL.

Camera reads include endpoint/name/enabled and credential presence flags, never username/password. Action reads include method/retry configuration and URL/header/body presence flags, never their values. A preserved URL/headers/body cannot be recovered through the masked view. Do not serialize edit objects, pipeline snapshots or stored internal rows to clients or diagnostics. Phase 6 uses explicit API edit DTOs with source-generated JSON metadata and default HTML-sensitive escaping; internal snapshots and secret edit implementation objects are never response models.

`BootstrapAdministrator(username, password)` is an in-process persistence operation only. It inserts one administrator if absent and never silently replaces an existing credential. It stores `pbkdf2-sha256$600000$<base64 salt>$<base64 hash>`: PBKDF2-HMAC-SHA256, 600,000 iterations, a random 16-byte salt and 32-byte hash. Bootstrap requires a nonblank username of at most 256 units and a password of 12–4096 units. No default account exists. Phase 6 supplies environment/password-file setup, hash verification, bounded sessions and an explicit offline reset command; see [the administrator policy](ADMINISTRATION.md). The store operation itself exposes no hash or session. Restart preserves the hash and bootstrap returns false when it already exists. Do not delete the database to reset a password.

## Schema and dependencies

Small embedded SQL migrations use `PRAGMA user_version`, advancing only inside a transaction. Version 1 creates configuration revision, cameras and administrator; version 2 adds rules and ordered headers. Earlier supported schemas migrate without discarding data. A future schema version, corrupt database or invalid stored configuration fails closed. `quick_check` runs at startup and foreign-key checks run during loads. Each operation closes its connection (pooling disabled); lock waits are bounded by the two-second SQLite busy timeout. No event history, transition identities, cooldowns, jobs, camera/execution statuses, sessions or response bodies have database tables.

Pinned/resolved dependencies for this phase:

| Component | Version / asset |
| --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.13 |
| SQLitePCLRaw bundle_e_sqlite3 / core / provider.e_sqlite3 / lib.e_sqlite3 | 2.1.13 |
| SQLite engine observed by the running JIT and Linux native tests | 3.53.3 |
| Verified Linux native library | `SQLitePCLRaw.lib.e_sqlite3/2.1.13/runtimes/linux-x64/native/libe_sqlite3.so` |

Microsoft documents that [Microsoft.Data.Sqlite initializes its bundled SQLitePCLRaw dependency](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions). The package baseline is [Microsoft.Data.Sqlite 10.0.13](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.13); actual NativeAOT loading evidence is in [the phase 5 handoff](PERSISTENCE_VERIFICATION.md). No ORM or system SQLite fallback is used. Linux arm64 assets/runtime remain unverified; this phase claims Linux x64 only.

## Ownership, permissions and failures

Camera credentials and complete HTTP action configuration are recoverable plaintext in SQLite. Filesystem restrictions protect them; **this is not encryption at rest**. Treat the database, WAL, SHM, rollback journals and backups as secrets.

On Linux the final data directory must belong to the effective application UID. Existing DB/sidecar files must also belong to that UID and be regular files. The store checks ownership/type with Linux `statx` and rejects final-path symlinks. Directory ancestors/mounts must be controlled by the operator; do not allow another process with the same UID to modify this directory. Missing directories are created with mode 0700; an absent DB is created with mode 0600 before SQLite opens it. Existing owner-accessible files/directories are tightened to exactly 0600/0700. Owner read/write (and directory execute) must already exist: deliberately unreadable/read-only storage is rejected, not made writable. Root ownership is not adopted or recursively changed. SQLite inherits 0600 database permissions for sidecars within the private directory; ownership/modes are rechecked before writes commit.

Before mounting an existing volume, arrange ownership for the intended container UID and restrict the directory/DB/sidecars to 0700/0600 on the host. Do not solve ownership failures by running the bridge as root or making the volume world-writable. The automated runner uses the existing non-root runtime account and separately verifies rejection of root-owned directories, databases, WAL and SHM files. Windows storage tests verify SQL behavior only: the code does not provision Windows ACLs, and Windows tests are not Linux permission evidence. Restrict Windows test directories using OS ACLs when they contain anything beyond synthetic data.

Fixed errors distinguish unsupported schema, corrupt/invalid data, ownership/permissions, unsafe file and unavailable storage. They retain no provider exception as an inner exception and do not include paths, SQL, URLs, credentials, headers or bodies. A failed save leaves the running configuration in place and can be retried with the same revision after repairing storage. Revision conflicts require rereading the masked snapshot and reviewing/resubmitting the edit. No automatic reset, deletion or recovery from backups occurs.

## Backup and restore

Use SQLite's online backup API/tool for a consistent backup while WAL is active. Copying only the main DB while the process is running can lose committed WAL content. Alternatively stop the application cleanly, then copy the complete data directory without discarding sidecars. Create backups in a private directory with restricted ownership/permissions; protect their transfer and retention as carefully as the live secrets. The tests use `SqliteConnection.BackupDatabase` and restore its standalone snapshot into a fresh private directory.

Restore only with the bridge stopped. Keep the old directory intact, restore the chosen consistent backup into a separate correctly owned directory, and point `ONVIF_DATA_DIRECTORY` at it. Startup validates schema/configuration before starting workers. Restoring configuration re-enables saved enabled cameras/actions; review it before starting. Restart restores no queued action or prior execution result and establishes fresh transition baselines/cooldowns. A backup is sensitive plaintext and does not establish real-device or destination interoperability.
