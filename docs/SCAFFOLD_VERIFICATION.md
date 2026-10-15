# Project scaffold verification

Date: 2026-10-14. This is the separately authorized structure/test/Dockerfile setup, not completion of a product implementation phase.

## Delivered structure

- `OnvifWebhookBridge.slnx` includes the application, standalone probe and xUnit test project.
- `src/OnvifWebhookBridge/` is a .NET 10 NativeAOT Minimal API shell with only `/health`. No camera workers or administrator routes are registered.
- `src/OnvifProbe/` retains the original observation/recovery loop. Only its location and renewal-adapter source ownership changed. Ignored private launch profiles were left in place rather than copied.
- `Onvif/AddressedAuthentication.cs` is the proven adapter extracted from the probe. A linked compile item lets the probe reuse it without referencing the web application. The unit test references the application assembly and verifies two generated renewal requests carry distinct WS-Addressing MessageIDs, Action, To, anonymous ReplyTo and the delegated WS-UsernameToken security header.
- Shared props centralize .NET 10, C# 14, nullable references, implicit usings, deterministic compilation and warnings as errors. Shared targets are an empty extension point. Editor conventions follow the requested reference repository's basic layout, indentation and C# preferences; no personal metadata or unrelated product architecture was copied.
- The root Dockerfile targets the application, installs the native compiler, publishes NativeAOT and uses a digest-pinned runtime-deps image with its non-root account. Its allowlisted build context excludes the probe, tests and private files. It is not production deployment packaging.

## Commands and evidence

- SDK 10.0.400, SharpOnvifClient/Common 0.11.2. Restored package metadata and version-matched `SimpleOnvifClient`/`OnvifClientSettings` source were checked before adding client calls.
- `dotnet restore OnvifWebhookBridge.slnx --packages .local/nuget`: passed. The local package path was used for this restricted environment only and is not a repository default. Test dependencies are Microsoft.NET.Test.Sdk 18.3.0, xUnit 2.9.3, Visual Studio runner 3.1.5, Testcontainers 4.16.0. Restore rejected an initially considered older Testcontainers dependency graph for vulnerable SSH.NET; the current pinned release restores without suppressions.
- `dotnet build OnvifWebhookBridge.slnx --no-restore`: passed, zero warnings/errors.
- `dotnet test OnvifWebhookBridge.slnx --no-restore --filter Category=Unit`: passed, one renewal regression test.
- `./tests/OnvifProbe.Smoke.ps1`: passed after the move. Six notifications including repeats/false, duplicate/structured/unknown fields, raw XML, renewal headers and fault-driven resubscription. This is a synthetic console regression, not simulator or camera interoperability; it terminates its child and does not verify Ctrl+C.
- `docker build -t onvif-webhook-bridge:scaffold .`: passed; generated native Linux x64 code without AOT/trimming warnings. Runtime smoke passed on the local Linux amd64 Docker engine: `/health` returned 200 with `Healthy`, `/` returned 404, and the process ran as the image's non-root account. The temporary container was stopped/removed.

## Simulator fixture

Use [onvifsim revision `8f86706c2ba768006e85b38f6bc1c760d36f1bba`](https://github.com/mrtian2016/onvifsim/tree/8f86706c2ba768006e85b38f6bc1c760d36f1bba). `tests/Build-OnvifSimulator.ps1` builds its upstream `packaging/docker/Dockerfile`, tagging the local image with the full revision. Source version is 0.1.0; upstream builds against Qt 6.4.2 on Debian 12. The source revision is pinned; upstream base tags and apt packages can change, so this is not a byte-reproducible image build.

The xUnit integration test uses Testcontainers to start this cached image on random mapped ports. It disables multicast discovery, replaces the default camera through `POST /api/scenario` with a generic synthetic camera and explicit fake credentials, and supplies the mapped advertised host/port to avoid unreachable container-internal XAddrs. It uses service discovery, unfiltered subscribe, the shared renewal adapter, and explicit true/false control-API event triggers. It then pulls each state and unsubscribes, with a 90-second overall deadline and bounded cleanup. An unknown synthetic topic uses the simulator's fallback `State` field; production code has no field assumption. Container disposal and the resource reaper handle teardown. Missing prerequisites fail visibly; use `--filter Category=Unit` for Docker-free work.

Simulator image build passed using the upstream Dockerfile at the pinned revision. `dotnet test OnvifWebhookBridge.slnx --no-restore` passed: two tests, zero failures/skips, including the simulator subscription/renewal/true/false/unsubscribe path. The fixture buffers its small JSON requests before sending because this simulator revision does not accept chunked control bodies. Its event service also requires authentication even when the user list is empty; the scenario therefore supplies clearly synthetic credentials instead of relying on upstream defaults. Neither workaround changes production transport behaviour.

## Remaining gates

Pre-commit recheck after adding the plan's per-step test-maintenance gates: restored local package paths with SDK 10.0.204, built the solution with zero warnings/errors, passed both xUnit tests (zero failures/skips, including Docker simulator integration), and passed the console smoke regression. No implementation tests were changed for this documentation update; the existing coverage remains applicable. Earlier NativeAOT container results above were not rerun for the plan-only change.

The native runtime smoke exercises the application shell only: it does not exercise ONVIF under NativeAOT, SQLite, authentication or dispatch. Those paths need their own published-native checks as they are implemented. The simulator test is JIT protocol integration against a simulator, not physical-device interoperability. Real activation/reset cycles, reboot/network recovery, Linux arm64 and deployment-host checks remain pending. No camera settings or external destinations were accessed. No Compose or CI workflows were added.
