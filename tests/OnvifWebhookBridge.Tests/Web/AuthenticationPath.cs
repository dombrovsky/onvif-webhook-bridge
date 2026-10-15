namespace OnvifWebhookBridge.Tests.Web
{
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Tests.Persistence;
    using OnvifWebhookBridge.Web;
    using static OnvifWebhookBridge.Tests.Persistence.StoragePath;

    internal static class AuthenticationPath
    {
        public static async Task Verify()
        {
            using var data = new TemporaryStorage();
            var store = new ConfigurationStore(data.Path, Pipeline());
            store.Initialize();
            var clock = new Clock();
            var auth = new AdminAuthentication(store, clock);
            Throws<ArgumentException>(() => auth.Bootstrap(name => name == "ONVIF_ADMIN_USERNAME" ? "synthetic-admin" : null));
            Throws<ArgumentException>(() => auth.Bootstrap(_ => "synthetic-conflicting-source"));
            Check(!store.Read().HasAdministrator, "InvalidBootstrapDoesNotCreateAdmin");
            auth.Bootstrap(name => name == "ONVIF_ADMIN_USERNAME" ? "synthetic-admin" : name == "ONVIF_ADMIN_PASSWORD" ? "synthetic-admin-password" : null);
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 64; i++)
            {
                if (i % 4 == 0)
                {
                    clock.Now += TimeSpan.FromMinutes(1);
                }
                var login = await auth.Login("synthetic-admin", "synthetic-admin-password", "synthetic-address");
                Check(login.Session is not null && tokens.Add(login.Session.Token) && login.Session.Token != login.Session.Csrf, "RandomSessionTokens");
            }
            clock.Now += TimeSpan.FromMinutes(1);
            Check((await auth.Login("synthetic-admin", "synthetic-admin-password", "synthetic-address")).Status == 429, "SessionCapacity");
            clock.Now += TimeSpan.FromHours(8);
            Check(tokens.All(t => auth.Find(t) is null), "AbsoluteExpiry");
            var session = (await auth.Login("synthetic-admin", "synthetic-admin-password", "synthetic-address")).Session;
            Check(session is not null, "ExpiredCapacityReclaimed");
            auth.Bootstrap(name => name == "ONVIF_ADMIN_USERNAME" ? "replacement-admin" : name == "ONVIF_ADMIN_PASSWORD" ? "replacement-admin-password" : null, reset: true);
            Check(auth.Find(session!.Token) is null && !store.VerifyAdministrator("synthetic-admin", "synthetic-admin-password") &&
                store.VerifyAdministrator("replacement-admin", "replacement-admin-password"), "ExplicitResetOnly");
            var restarted = new ConfigurationStore(data.Path, Pipeline());
            restarted.Initialize();
            Check(restarted.Read().Revision == 0 && restarted.VerifyAdministrator("replacement-admin", "replacement-admin-password"), "ResetPersistsWithoutConfigMutation");
            Console.WriteLine("PASS: bounded random sessions, expiry/reclamation and explicit durable administrator reset.");
        }
        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
