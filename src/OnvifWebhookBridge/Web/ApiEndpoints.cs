namespace OnvifWebhookBridge.Web
{
    using Microsoft.Extensions.Hosting;
    using OnvifWebhookBridge.Dispatch;
    using OnvifWebhookBridge.Events;
    using OnvifWebhookBridge.Onvif;
    using OnvifWebhookBridge.Persistence;
    using System.Text.Json;
    using System.Text.Json.Serialization.Metadata;

    public static class ApiEndpoints
    {
        private static readonly SemaphoreSlim connectivity = new(4);
        public static IServiceCollection AddAdministration(this IServiceCollection services)
        {
            services.AddSingleton<AdminAuthentication>();
            services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonContext.Default));
            return services;
        }
        public static void MapAdministration(this WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                try
                {
                    var path = context.Request.Path;
                    if (path == "/health" && HttpMethods.IsGet(context.Request.Method))
                    {
                        await next(context);
                        return;
                    }
                    var auth = context.RequestServices.GetRequiredService<AdminAuthentication>();
                    if (path == "/api/login" && HttpMethods.IsPost(context.Request.Method))
                    {
                        if (!AdminAuthentication.SameOrigin(context.Request))
                        {
                            await Error(context, 403, "Origin", "A matching Origin header is required.");
                            return;
                        }
                        await next(context);
                        return;
                    }
                    var session = auth.Find(context.Request.Cookies[AdminAuthentication.CookieName]);
                    if (session is null)
                    {
                        await Error(context, 401, "Authentication", "Sign in is required.");
                        return;
                    }
                    context.Items[typeof(AdminAuthentication.Session)] = session;
                    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
                        (!AdminAuthentication.SameOrigin(context.Request) || !AdminAuthentication.ValidCsrf(context.Request.Headers["X-CSRF-Token"].ToString(), session)))
                    {
                        await Error(context, 403, "Csrf", "A matching Origin and session CSRF token are required.");
                        return;
                    }
                    await next(context);
                }
                catch (RevisionConflictException) { await Error(context, 409, "RevisionConflict", "Configuration changed. Read it again and review your edit."); }
                catch (StorageException) { await Error(context, 503, "StorageUnavailable", "Configuration storage is unavailable. No edit was applied."); }
                catch (BadHttpRequestException error) { await Error(context, error.StatusCode is 413 or 415 or 408 ? error.StatusCode : 400, "InvalidRequest", "The request is invalid or exceeds its size or time limit."); }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
                {
                    await Error(context, 400, "Validation", "Check required fields, field lengths, enums, secret operations, references and HTTP action settings.");
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (Exception) { await Error(context, 500, "InternalError", "The operation could not be completed."); }
            });
            app.MapGet("/health", () => Results.Text("Healthy"));
            app.MapPost("/api/login", async (HttpContext c, AdminAuthentication auth) =>
            {
                var request = await Body<LoginRequest>(c);
                if (string.IsNullOrEmpty(request.Username) || request.Username.Length > 256 || request.Password is null || request.Password.Length > 4096)
                {
                    throw new ArgumentException();
                }
                var result = await auth.Login(request.Username, request.Password, c.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                if (result.Session is not { } session)
                {
                    await Error(c, result.Status, result.Status == 429 ? "Throttled" : "Authentication", "Sign in failed or is temporarily limited.");
                    return;
                }
                c.Response.Cookies.Append(AdminAuthentication.CookieName, session.Token, Cookie(c.Request.IsHttps, session.Expires));
                await Json(c, new SessionView(session.Csrf, session.Expires));
            });
            app.MapGet("/api/session", async (HttpContext c) =>
            {
                var s = (AdminAuthentication.Session)c.Items[typeof(AdminAuthentication.Session)]!;
                await Json(c, new SessionView(s.Csrf, s.Expires));
            });
            app.MapPost("/api/logout", (HttpContext c, AdminAuthentication auth) =>
            {
                auth.Logout((AdminAuthentication.Session)c.Items[typeof(AdminAuthentication.Session)]!);
                c.Response.Cookies.Delete(AdminAuthentication.CookieName, Cookie(c.Request.IsHttps, null));
                c.Response.StatusCode = 204;
            });
            app.MapGet("/api/configuration", async (HttpContext c, ConfigurationStore store) => await Json(c, store.Read()));
            app.MapGet("/api/cameras", async (HttpContext c, ConfigurationStore store) => await Json(c, store.Read().Cameras.ToArray()));
            app.MapGet("/api/rules", async (HttpContext c, ConfigurationStore store) => await Json(c, store.Read().Rules.ToArray()));
            app.MapGet("/api/events", async (HttpContext c, EventHistory history) => await Json(c, history.Snapshot().ToArray()));
            app.MapGet("/api/camera-status", async (HttpContext c, IEnumerable<IHostedService> services) =>
                await Json(c, services.OfType<CameraSupervisor>().Single().Status.ToArray()));
            app.MapGet("/api/executions", async (HttpContext c, CameraPipeline pipeline) => await Json(c, pipeline.LastExecutions.ToArray()));
            app.MapGet("/api/execution-status", async (HttpContext c, CameraPipeline pipeline) => await Json(c, pipeline.Status.ToArray()));
            app.MapPut("/api/cameras/{id}", async (HttpContext c, ConfigurationStore store, CameraPipeline pipeline, string id) =>
            {
                var edit = await Body<CameraRequest>(c);
                if (Uri.TryCreate(edit.Endpoint, UriKind.Absolute, out var endpoint) && pipeline.IsSelfAddress(endpoint))
                {
                    await Error(c, 400, "SelfTarget", "A camera endpoint cannot target this application.");
                    return;
                }
                var view = store.Read();
                await Json(c, new RevisionView(store.Save(edit.Revision,
                    view.Cameras.Where(x => x.Id != id).Select(Camera).Append(edit.Edit(id)), view.Rules.Select(Rule))));
            });
            app.MapPut("/api/rules/{id}", async (HttpContext c, ConfigurationStore store, string id) =>
            {
                var edit = await Body<RuleRequest>(c);
                var view = store.Read();
                await Json(c, new RevisionView(store.Save(edit.Revision, view.Cameras.Select(Camera),
                    view.Rules.Where(x => x.Match.Id != id).Select(Rule).Append(edit.Edit(id)))));
            });
            app.MapDelete("/api/cameras/{id}", async (HttpContext c, ConfigurationStore store, string id) =>
            {
                var edit = await Body<RevisionRequest>(c);
                var view = store.Read();
                if (view.Rules.Any(x => x.Match.CameraId == id))
                {
                    await Error(c, 409, "CameraReferenced", "Remove this camera's rules before deleting it.");
                    return;
                }
                await Json(c, new RevisionView(store.Save(edit.Revision, view.Cameras.Where(x => x.Id != id).Select(Camera), view.Rules.Select(Rule))));
            });
            app.MapDelete("/api/rules/{id}", async (HttpContext c, ConfigurationStore store, string id) =>
            {
                var edit = await Body<RevisionRequest>(c);
                var view = store.Read();
                await Json(c, new RevisionView(store.Save(edit.Revision, view.Cameras.Select(Camera), view.Rules.Where(x => x.Match.Id != id).Select(Rule))));
            });
            app.MapPost("/api/cameras/{id}/enabled", async (HttpContext c, ConfigurationStore store, string id) =>
            {
                var edit = await Body<EnabledRequest>(c);
                var view = store.Read();
                if (!view.Cameras.Any(x => x.Id == id))
                {
                    c.Response.StatusCode = 404;
                    return;
                }
                await Json(c, new RevisionView(store.Save(edit.Revision, view.Cameras.Select(x => Camera(x.Id == id ? x with { Enabled = edit.Enabled } : x)), view.Rules.Select(Rule))));
            });
            app.MapPost("/api/rules/{id}/enabled", async (HttpContext c, ConfigurationStore store, string id) =>
            {
                var edit = await Body<EnabledRequest>(c);
                var view = store.Read();
                if (!view.Rules.Any(x => x.Match.Id == id))
                {
                    c.Response.StatusCode = 404;
                    return;
                }
                await Json(c, new RevisionView(store.Save(edit.Revision, view.Cameras.Select(Camera),
                    view.Rules.Select(x => Rule(x.Match.Id == id ? x with { Match = x.Match with { Enabled = edit.Enabled } } : x)))));
            });
            app.MapPost("/api/rules/{id}/test", async (HttpContext c, CameraPipeline pipeline, string id) =>
            {
                if (!pipeline.ManualTest(id))
                {
                    await Error(c, 409, "NotAdmitted", "Rule is missing, camera is disabled, or the bounded queue is full.");
                    return;
                }
                c.Response.StatusCode = 202;
            });
            app.MapPost("/api/cameras/{id}/test", async (HttpContext c, CameraPipeline pipeline, string id) =>
            {
                var camera = pipeline.Cameras.SingleOrDefault(x => x.Id == id);
                if (camera is null)
                {
                    c.Response.StatusCode = 404;
                    return;
                }
                if (!await connectivity.WaitAsync(0, c.RequestAborted))
                {
                    await Error(c, 429, "Busy", "Connectivity tests are at capacity.");
                    return;
                }
                try
                {
                    await Json(c, await CameraSubscriptionWorker.TestConnectivity(camera, c.RequestAborted));
                }
                finally { connectivity.Release(); }
            });
        }
        private static CameraEdit Camera(CameraView v) => new(v.Id, v.Name, v.Endpoint, v.Enabled);
        private static RuleEdit Rule(RuleView v) => new(v.Match, v.Name, v.Method, v.AdditionalAttempts);
        private static CookieOptions Cookie(bool secure, DateTimeOffset? expires) => new()
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = secure,
            Path = "/",
            Expires = expires,
            IsEssential = true
        };
        private static async Task<T> Body<T>(HttpContext c)
        {
            const int maximum = 262144;
            if (c.Request.ContentLength > maximum)
            {
                throw new BadHttpRequestException("Request too large.", 413);
            }
            if (!c.Request.HasJsonContentType())
            {
                throw new BadHttpRequestException("JSON required.", 415);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            try
            {
                while ((read = await c.Request.Body.ReadAsync(bytes, deadline.Token)) != 0)
                {
                    if (buffer.Length + read > maximum)
                    {
                        throw new BadHttpRequestException("Request too large.", 413);
                    }
                    buffer.Write(bytes, 0, read);
                }
            }
            catch (OperationCanceledException) when (!c.RequestAborted.IsCancellationRequested) { throw new BadHttpRequestException("Request deadline.", 408); }
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Metadata<T>()) ?? throw new ArgumentException();
        }
        private static JsonTypeInfo<T> Metadata<T>() => (JsonTypeInfo<T>)JsonContext.Default.GetTypeInfo(typeof(T))!;
        private static Task Json<T>(HttpContext c, T value) => c.Response.WriteAsJsonAsync(value, Metadata<T>(), cancellationToken: c.RequestAborted);
        private static Task Error(HttpContext c, int status, string code, string message)
        {
            c.Response.StatusCode = status;
            return Json(c, new ApiError(code, message));
        }
    }
}
