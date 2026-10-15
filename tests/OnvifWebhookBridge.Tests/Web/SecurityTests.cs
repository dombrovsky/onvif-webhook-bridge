namespace OnvifWebhookBridge.Tests.Web
{
    using OnvifWebhookBridge.Persistence;
    using OnvifWebhookBridge.Tests.Persistence;
    using OnvifWebhookBridge.Web;
    using Xunit;

    public sealed class SecurityTests
    {
        [Fact]
        public async Task SharedSecurityHttpPath()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await SecurityPath.Verify(deadline.Token);
        }
        [Fact]
        public Task BoundedSessionAndResetAcceptance() => AuthenticationPath.Verify();
        [Fact]
        public async Task ProductionExecutableResetAndLogin()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await ExecutablePath.Verify(deadline.Token);
        }
        [Fact]
        public async Task SessionsExpireThrottleIsBoundedAndExplicitResetInvalidatesCredentials()
        {
            using var data = new TemporaryStorage();
            var store = new ConfigurationStore(data.Path, StoragePath.Pipeline());
            store.Initialize();
            var clock = new Clock();
            var auth = new AdminAuthentication(store, clock);
            auth.Bootstrap(name => name == "ONVIF_ADMIN_USERNAME" ? "synthetic-admin" : name == "ONVIF_ADMIN_PASSWORD" ? "synthetic-admin-password" : null);
            var first = await auth.Login("synthetic-admin", "synthetic-admin-password", "synthetic-address");
            Assert.NotNull(first.Session);
            Assert.NotNull(auth.Find(first.Session.Token));
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(401, (await auth.Login("unknown", "synthetic-wrong-password", "synthetic-address")).Status);
            }
            Assert.Equal(429, (await auth.Login("synthetic-admin", "synthetic-admin-password", "synthetic-address")).Status);
            clock.Now += TimeSpan.FromHours(8);
            Assert.Null(auth.Find(first.Session.Token));
            var replacement = await auth.Login("synthetic-admin", "synthetic-admin-password", "synthetic-address");
            Assert.NotNull(replacement.Session);
            auth.Bootstrap(name => name == "ONVIF_ADMIN_USERNAME" ? "replacement-admin" : name == "ONVIF_ADMIN_PASSWORD" ? "replacement-admin-password" : null, reset: true);
            Assert.Null(auth.Find(replacement.Session.Token));
            Assert.False(store.VerifyAdministrator("synthetic-admin", "synthetic-admin-password"));
            Assert.True(store.VerifyAdministrator("replacement-admin", "replacement-admin-password"));
            Assert.Equal(0, store.Read().Revision);
        }
        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
