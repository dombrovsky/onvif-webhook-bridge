# Optional metrics export

Metrics are disabled by default. Set `ONVIF_METRICS_ENABLED=true` in private process/container configuration to enable OTLP **metrics only**. No trace or log exporter or unauthenticated scrape endpoint is registered. The diagnostic probe is unchanged.

The exporter uses HTTP/protobuf with normal TLS verification. Configure a trusted collector with standard OpenTelemetry environment variables:

```text
ONVIF_METRICS_ENABLED=true
OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=https://collector.example.invalid/v1/metrics
OTEL_EXPORTER_OTLP_METRICS_HEADERS=X-Collector-Key=synthetic-token
OTEL_EXPORTER_OTLP_METRICS_TIMEOUT=10000
```

The metrics endpoint is a full signal URL, including `/v1/metrics`. Generic `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` and `OTEL_EXPORTER_OTLP_TIMEOUT` are fallbacks; the generic HTTP endpoint has the metrics path appended. Without an endpoint the HTTP exporter uses localhost port 4318. Set exporter credentials only in private configuration. Collector networking uses the same strict TLS, DNS/address checks and disabled redirects, cookies and proxies as verified webhook transport, and rejects application self-targets. Export failure does not affect rule admission or webhook retry policy; metrics are best effort, in memory, and reset on process restart.

The application fixes `service.name=onvif-webhook-bridge`, cumulative temporality and a 60,000 ms periodic export interval. `OTEL_SERVICE_NAME`, resource attribute variables, protocol, temporality and interval environment settings cannot override these application settings. No host, deployment or device resource detector is installed. Exemplars are disabled so filtered attributes cannot appear in exemplar payloads.

## Application instruments

All names below are OTLP instrument names. Counters are monotonic cumulative sums; gauges reflect collection-time snapshots. Counts aggregate across all cameras and rules, including manual dispatch tests. There are no camera/rule/job identity, topic, URL, exception, response body or credential labels.

| Name | Type | Unit | Labels | Meaning |
| --- | --- | --- | --- | --- |
| `bridge.camera.count` | Gauge | `{camera}` | `state` | Configured cameras by current worker state: Disabled, Connecting, Subscribed, Reconnecting, Error, Stopped, Unknown. |
| `bridge.camera.contact.age` | Gauge | `s` | None | Maximum elapsed time since last successful contact among configured cameras with contact history; zero when none have contacted. Includes disabled cameras with retained contact history. |
| `bridge.camera.contact.unknown` | Gauge | `{camera}` | None | Configured cameras with no successful contact timestamp. |
| `bridge.subscription.failures` | Counter | `{failure}` | None | Session discovery/subscription/renewal/poll failures that enter recovery; excludes expected shutdown and best-effort unsubscribe failures. |
| `bridge.events` | Counter | `{event}` | None | Notifications passed to the pipeline, including truncated or obsolete-snapshot notifications retained in inspector history. |
| `bridge.rule.decisions` | Counter | `{decision}` | `decision` | One per evaluated rule: Disabled, Unmatched, Baseline, Deleted, Unchanged, Cooldown, Rejected, Accepted, UnsafeContent. |
| `bridge.dispatch.admissions` | Counter | `{job}` | `result` | Accepted or Rejected TaskFlow admissions; pre-admission cancellation is not overflow or an accepted job. |
| `bridge.dispatch.outcomes` | Counter | `{job}` | `result` | Exactly one per accepted terminal job: Accepted, HttpFailure, TransportFailure, Timeout, Cancelled, Stale, Expired. Includes queued cancellation, obsolete jobs, removed rules and shutdown. Accepted means HTTP 2xx transport acceptance, not destination side effects. |
| `bridge.dispatch.retries` | Counter | `{retry}` | None | Additional HTTP attempts actually started; excludes scheduled delays cancelled before another request. |
| `bridge.dispatch.duration` | Histogram | `s` | `result` | Enqueue-to-terminal time for each accepted job, including queue wait, retries and queued cancellation. |

TaskFlow still owns scheduling, capacity and settlement. A small per-job admission/completion handshake handles completion racing synchronous admission, without another queue or lifecycle registry. Rejection never consumes cooldown. Metrics do not depend on whether a job is still the rule's latest displayed status.

## Framework instruments and privacy

The exporter subscribes to the built-in .NET 10 meters `System.Runtime`, `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `System.Net.Http` and `System.Net.NameResolution`. Instrument names and units follow .NET, including `dotnet.*` runtime/GC/threadpool metrics, `http.server.request.duration` (seconds), `http.server.active_requests` (requests), `kestrel.*` connection metrics, `http.client.request.duration` (seconds), `http.client.active_requests` (requests), `http.client.open_connections` (connections), `http.client.connection.duration` (seconds), `http.client.request.time_in_queue` (seconds) and `dns.lookup.duration` (seconds). Outgoing HTTP includes camera and webhook traffic.

The complete framework instrument names, units and availability are listed in Microsoft's [runtime metrics reference](https://learn.microsoft.com/dotnet/core/diagnostics/built-in-metrics-runtime), [ASP.NET Core metrics reference](https://learn.microsoft.com/aspnet/core/metrics/built-in?view=aspnetcore-10.0) and [networking metrics reference](https://learn.microsoft.com/dotnet/core/diagnostics/built-in-metrics-system-net). Use the .NET 10 entries. Only the meters above are subscribed; the label restrictions below override the framework's default attributes.

Every subscribed instrument passes through a view. Framework labels are restricted to `http.request.method`, `http.response.status_code`, `network.protocol.version`, `cpu.mode`, `gc.heap.generation`, and `http.connection.state`. All other labels, including server addresses/ports, URL paths/routes, DNS questions and error types, are removed before aggregation. SDK cardinality limits remain enabled. No HTTP instrumentation enrichers or diagnostic listeners that log payloads are added. Existing HttpClientFactory logging remains disabled.

All histograms use explicit buckets. For instruments with unit `s`, boundaries are `0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30, 60`. Other histogram units use `0, 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000`. Both include the implicit positive-infinity bucket; min/max recording is disabled.

## Verification

The unit suite exercises cumulative collection, admission overflow, queued and running cancellation, actual retries, exactly-once outcomes, histogram buckets and fake-secret redaction through the OTLP HTTP/protobuf serializer with an in-process recorder. The same shared check runs in the Linux NativeAOT smoke executable, in both full and storage-only verification. Run the repository's Release build/full tests and `tests/Verify-Phase7Native.ps1` on Linux amd64 and arm64 builders as described in [development](DEVELOPMENT.md). Native execution and emulation must be reported separately. Synthetic checks do not establish real-device interoperability.

For focused native metrics verification without a simulator or password-hashing tests, use `./tests/Verify-Phase7Native.ps1 -MetricsOnly -Platform linux/amd64`. On an amd64 builder, add `-Platform linux/arm64 -CrossCompile` instead to compile arm64 and execute the focused check under emulation. This does not replace the full repository/native suite.
