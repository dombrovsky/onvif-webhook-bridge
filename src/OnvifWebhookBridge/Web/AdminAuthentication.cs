namespace OnvifWebhookBridge.Web
{
    using OnvifWebhookBridge.Persistence;
    using System.Security.Cryptography;

    public sealed class AdminAuthentication(ConfigurationStore store, TimeProvider clock)
    {
        public const string CookieName = "onvif-session";
        private readonly object gate = new();
        private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim hashing = new(2);
        private DateTimeOffset window;
        private int total;
        internal sealed record Session(string Token, string Csrf, DateTimeOffset Expires);

        internal void Bootstrap(Func<string, string?> environment, bool reset = false)
        {
            if (!reset && store.Read().HasAdministrator)
            {
                return;
            }
            var username = environment("ONVIF_ADMIN_USERNAME");
            var password = environment("ONVIF_ADMIN_PASSWORD");
            var file = environment("ONVIF_ADMIN_PASSWORD_FILE");
            if (username is null && password is null && file is null && !reset)
            {
                return;
            }
            if (password is not null && file is not null)
            {
                throw new ArgumentException("Choose one administrator password source.");
            }
            if (file is not null)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    if (stream.Length > 16384)
                    {
                        throw new ArgumentException("Administrator password file is too large.");
                    }
                    using var reader = new StreamReader(stream);
                    var characters = new char[4099];
                    var length = reader.ReadBlock(characters, 0, characters.Length);
                    if (length == characters.Length)
                    {
                        throw new ArgumentException("Administrator password file is too large.");
                    }
                    password = new string(characters, 0, length).TrimEnd('\r', '\n');
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ArgumentException("Administrator password file is unavailable.");
                }
            }
            if (username is null || password is null)
            {
                throw new ArgumentException("Administrator username and password are required.");
            }
            if (reset)
            {
                store.ResetAdministrator(username, password);
                lock (gate)
                {
                    sessions.Clear();
                }
            }
            else
            {
                store.BootstrapAdministrator(username, password);
            }
        }

        internal async Task<(int Status, Session? Session)> Login(string username, string password, string address)
        {
            lock (gate)
            {
                var now = clock.GetUtcNow();
                foreach (var key in attempts.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(1)).Select(x => x.Key).ToArray())
                {
                    attempts.Remove(key);
                }
                if (now - window >= TimeSpan.FromMinutes(1))
                {
                    window = now;
                    total = 0;
                }
                if (total >= 20 || !attempts.ContainsKey(address) && attempts.Count >= 256)
                {
                    return (429, null);
                }
                var entry = attempts.GetValueOrDefault(address, (now, 0));
                if (entry.Item2 >= 5)
                {
                    return (429, null);
                }
                attempts[address] = (entry.Item1, entry.Item2 + 1);
                total++;
            }
            if (!await hashing.WaitAsync(0))
            {
                return (429, null);
            }
            try
            {
                if (!store.VerifyAdministrator(username, password))
                {
                    return (401, null);
                }
                lock (gate)
                {
                    RemoveExpired();
                    if (sessions.Count >= 64)
                    {
                        return (429, null);
                    }
                    var session = new Session(RandomToken(), RandomToken(), clock.GetUtcNow().AddHours(8));
                    sessions.Add(session.Token, session);
                    return (200, session);
                }
            }
            finally { hashing.Release(); }
        }
        internal Session? Find(string? token)
        {
            if (token is null || token.Length != 64)
            {
                return null;
            }
            lock (gate)
            {
                RemoveExpired();
                return sessions.GetValueOrDefault(token);
            }
        }
        internal void Logout(Session session)
        {
            lock (gate)
            {
                sessions.Remove(session.Token);
            }
        }
        private void RemoveExpired()
        {
            foreach (var key in sessions.Where(s => s.Value.Expires <= clock.GetUtcNow()).Select(s => s.Key).ToArray())
            {
                sessions.Remove(key);
            }
        }
        private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        internal static bool SameOrigin(HttpRequest request)
        {
            var origin = request.Headers.Origin;
            return origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var uri) &&
                uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 &&
                Uri.TryCreate(request.Scheme + "://" + request.Host, UriKind.Absolute, out var expected) &&
                uri.Scheme == expected.Scheme && uri.Port == expected.Port &&
                string.Equals(uri.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                request.Headers["Sec-Fetch-Site"].ToString() is not ("cross-site" or "same-site");
        }
        internal static bool ValidCsrf(string supplied, Session session) => supplied.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(session.Csrf));
    }
}
