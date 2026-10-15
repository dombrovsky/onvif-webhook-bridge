namespace OnvifWebhookBridge.Persistence
{
    using System.Runtime.InteropServices;

    internal static partial class StorageFiles
    {
        private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        internal static void Prepare(string directory, string database)
        {
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsLinux()) { Directory.CreateDirectory(directory, DirectoryMode); }
                else { Directory.CreateDirectory(directory); }
            }
            Restrict(directory, true);
            foreach (var path in new[] { database, database + "-wal", database + "-shm", database + "-journal" })
            { if (File.Exists(path) || Directory.Exists(path)) { Restrict(path, false); } }
        }

        internal static void Restrict(string path, bool directory)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new StorageException("UnsafeFile"); }
            if (!OperatingSystem.IsLinux()) { return; }
            if (Statx(-100, path, 256, 0x7ff, out var stat) != 0 || stat.Uid != GetEffectiveUserId() ||
                (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000))
            { throw new StorageException("Ownership"); }
            var required = directory ? DirectoryMode : FileMode;
            var mode = File.GetUnixFileMode(path);
            // Do not turn deliberately read-only storage into writable storage.
            if ((mode & required) != required) { throw new StorageException("Permissions"); }
            File.SetUnixFileMode(path, required);
            if (File.GetUnixFileMode(path) != required) { throw new StorageException("Permissions"); }
        }

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct StatResult
        {
            [FieldOffset(20)] public uint Uid;
            [FieldOffset(28)] public ushort Mode;
        }
        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int Statx(int directory, string path, int flags, uint mask, out StatResult result);
        [LibraryImport("libc", EntryPoint = "geteuid")]
        private static partial uint GetEffectiveUserId();
    }
}
