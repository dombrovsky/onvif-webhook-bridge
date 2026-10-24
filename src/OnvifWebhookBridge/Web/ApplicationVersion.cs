namespace OnvifWebhookBridge.Web
{
    using System.Reflection;

    internal static class ApplicationVersion
    {
        public static string Current
        {
            get;
        } = typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    }
}
