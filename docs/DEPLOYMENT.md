# Container deployment and release

Build locally with `docker compose build`. Nothing in these instructions requires image publication. The application remains one .NET 10 NativeAOT process, including the browser, authenticated APIs, independent camera workers and SQLite. It has no default administrator.

## First start

Copy `.env.example` to ignored `.env`. Replace the administrator name and password-file path locally. Create a private UTF-8 file containing a password of 12–4096 characters. Keep it outside source control; restrict its parent and read access. Compose file-backed secrets are mounted files, not an encrypted secret store. The container account (UID/GID 1654) must be able to read the file. On Linux, arrange ownership/access before startup; Compose does not reliably implement file-secret uid/gid/mode remapping. Do not put the password in `.env` or shell arguments.

```sh
docker compose config --quiet
docker compose build
docker compose up -d
docker compose ps
```

The default published interface is loopback. Open the configured local port and log in. Set `ONVIF_PUBLISHED_PORT` to change the host port and `ONVIF_CONTAINER_PORT` to change the single internal HTTP port. `ASPNETCORE_HTTP_PORTS` and the built-in `--health-probe` use that same single port; overriding Kestrel HTTP URLs requires updating probe configuration accordingly. `ONVIF_BIND_ADDRESS` changes the published interface deliberately. Use HTTPS for access beyond a controlled local connection.

The named volume is initialized from the image's private `/data`, owned by UID/GID 1654. Existing/bind-mounted directories and DB/sidecars must already belong to that account with owner access; directory mode 0700 and file mode 0600 are enforced. Fix ownership offline, never by running the service as root or making storage world-writable. A new named volume works without a privileged init service. `ONVIF_DATA_DIRECTORY` can select another dedicated mount in a Compose override, with the same ownership contract. Root filesystem is read-only, `/tmp` is a bounded disposable tmpfs, all capabilities are dropped and privilege escalation is disabled. Stop allowance is 15 seconds, including the workers' documented five-second cleanup target.

Bootstrap variables are ignored after the administrator exists. Keep the private file available for the shipped Compose definition, which requires its path at configuration time; startup ignores it after bootstrap. A separately reviewed override can remove bootstrap fields and their required substitutions. Retain a safe operator reset procedure. For offline reset, stop the bridge first, keep a consistent protected backup, then run `docker compose run --rm --no-deps bridge --reset-administrator` with the desired name/password file. Require exit zero before starting normally. This preserves cameras, rules and revisions. See [administrator policy](ADMINISTRATION.md).

## HTTPS and trusted certificates

The app intentionally does not trust forwarded headers. Preserve that boundary: use a reverse proxy which connects to a **validated HTTPS backend**, preserving the original Host header including a nondefault public port. Both browser and backend requests then have scheme HTTPS, so same-origin checks and Secure cookies work without trusting arbitrary forwarding headers. Do not terminate TLS to an HTTP backend and expect forwarded headers to fix cookies/origins.

Use a local Compose override to configure Kestrel's standard HTTPS endpoint, alongside the internal HTTP health endpoint:

```yaml
services:
  bridge:
    environment:
      Kestrel__Endpoints__Http__Url: http://+:8080
      Kestrel__Endpoints__Https__Url: https://+:8443
      Kestrel__Certificates__Default__Path: /run/secrets/backend_certificate
    secrets:
      - backend_certificate
    # Attach the operator-managed proxy to this private bridge network.
    # Do not publish the backend HTTP port on a LAN interface.
secrets:
  backend_certificate:
    file: ./.local/backend.pfx
```

Use a restricted PFX containing the private key, readable by 1654. If password-protected, supply Kestrel's certificate password through protected operator configuration. The proxy must trust the issuing CA, validate the backend certificate name and preserve Host. For example, in an operator-managed nginx HTTPS server block:

```nginx
location / {
    proxy_pass https://bridge:8443;
    proxy_set_header Host $http_host;
    proxy_ssl_server_name on;
    proxy_ssl_name bridge;
    proxy_ssl_verify on;
    proxy_ssl_trusted_certificate /run/secrets/backend_ca.pem;
}
```

The synthetic service name `bridge` must be in that backend certificate's SAN. Supply the proxy's public certificate and trust separately. Restrict backend network access to trusted peers. Direct peer throttling is shared by clients behind the proxy; client address forwarding is not enabled. Verify login, Secure cookie, same-origin mutations, logout and restart through the actual operator proxy before exposure. Proxy/host interoperability remains a deployment gate, not established by local simulator tests.

Outbound HTTPS uses normal system CA validation for both ONVIF and actions. The pinned Noble runtime includes CA certificates, ICU and timezone assets. For private issuing CAs, build a local derived image from a dedicated private build context as root, copy only the CA certificate to `/usr/local/share/ca-certificates/`, run `update-ca-certificates`, then restore `USER 1654:1654`; never copy CA private keys or disable validation. Rebuild after CA/base updates. Certificates must have correct names, validity and chain; also check system time. No global certificate bypass is supported.

## Storage, backup, upgrades and recovery

`/health` is anonymous fixed health text (or empty 503) and tests readable persistent configuration after startup administrator enforcement. The probe exits zero only for exact success, uses loopback without proxy/redirects, and has a four-second deadline. A disconnected camera does not fail process health. Camera readiness/contact/event state is shown separately. An unhealthy Docker status alone does not cause Docker restart; restart policy addresses process exits. Startup corruption/schema/permissions failures exit safely without deleting data.

Follow [the full storage/backup contract](DATA_STORAGE.md). Secrets in SQLite and backups are recoverable plaintext, protected by permissions rather than encryption. Stop the service before a filesystem backup; preserve the complete directory and sidecars. Alternatively use SQLite's online backup API to create a consistent snapshot. Never copy only a live main DB and discard WAL. Restore into a separate private correctly owned directory with the service stopped; retain the original. Start a single process on that directory and check schema, configuration, masked secrets and subscription state. Restore re-enables saved actions, so review the chosen backup before startup.

For upgrades, keep a consistent backup, stop, build/pull a chosen immutable image digest, then recreate with the same volume. Transactional migrations run before workers/listeners. Unsupported future schemas fail closed; binary rollback may require restoring a compatible backup. Do not use `down --volumes` for upgrades. Container recreation clears sessions, history, transitions, cooldowns, queues and execution results; stored configuration remains. No replay occurs.

## Network diagnosis and mappings

Normal Compose bridge networking is sufficient for explicitly configured device endpoints. Verify DNS/routing/firewall from the container for the device service, the **discovered event address**, returned subscription address and HTTP destination. A reachable device endpoint alone is insufficient. Incorrect advertised addresses require fixing routing/device configuration deliberately, not inventing subscription URLs. No host networking or WS-Discovery is required. Private addresses are allowed; metadata/link-local/multicast/unspecified targets and identifiable self loops are rejected. No camera outage should produce a health/restart storm.

Use Events to discover exact topics and Source/Data values, including false and unmatched notifications. Inspect retained raw/structured content; select an observed field to create a reviewed disabled draft. Generic examples: an occurrence rule for synthetic topic `Synthetic/Detection` can GET `https://<destination-host>/<action-path>`; a POST rule can send literal `{"synthetic":true}` and an explicitly replaced custom header. These are placeholders, not live destination instructions. Never assume a boolean field name or translate namespace prefixes silently. Boolean matching recognizes XML true/1 and false/0; text matching is exact. Transition requires a known nonmatching-to-matching change; first/Initialized observations establish baseline. Privately verify at least two activation/reset cycles and stable Source/Key identity before enabling a real transition mapping; use occurrence mode when reset is absent.

Cooldown starts on bounded queue admission and delivery failure consumes it. Rejected/suppressed work is not replayed. Retries default to zero and can duplicate effects even for GET; at most two additional attempts and the existing deadlines/queue/revision checks apply. Manual tests perform actual external actions; use only an explicitly intended destination. A 2xx response proves transport acceptance only. See [browser usage](BROWSER_ADMINISTRATION.md) and [delivery contracts](HTTP_VERIFICATION.md).

## Platforms, CI and releases

Baseline images target linux/amd64 and linux/arm64. Local arm64 execution is emulation, separately labelled in [deployment evidence](DEPLOYMENT_VERIFICATION.md); actual deployment hardware remains pending. Native runners build their own target; the local cross toolchain supports amd64-to-arm64 only. Cold emulated HTTPS needed bounded anonymous transport readiness before login tests; this does not establish native hardware handshake performance. ARMv7 is excluded until the native compiler/toolchain, SQLite runtime and hardware smoke gates pass. Linux 32-bit x86 is unsupported and is never in manifests. Check CPU architecture, Linux container capability and target OS before selecting images.

Hosted native amd64/ARM64 verification passed for the merged browser revision `fa0fad6` on 2026-10-21; see [current release readiness](#public-release-readiness). Historical results and [remaining hardware/host/destination gates](DEPLOYMENT_VERIFICATION.md#remaining-gates-and-concrete-handoff) retain their original scope. Hosted CI does not establish operator-host interoperability or authorize publication.

`ci.yml` uses native amd64/arm64 runners for full JIT tests, actual onvifsim, NativeAOT execution and production deployment tests. PRs need no registry credentials and have contents-read permissions only. The manually dispatched `Release` workflow in `images.yml` reuses that entire gate for the selected revision, then publishes its tested artifacts. Release runs share one concurrency group without cancellation; GitHub may replace an older pending run with a newer pending run, so avoid queuing several releases. Only the final publishing job receives contents-write and packages-write permission. See [GitHub's concurrency contract](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency). Hosted publication remains a separate verification gate.

Dependencies retain their existing exact package pins; SDK/runtime manifest digests and Dockerfile frontend are pinned, as are Actions commit SHAs with reviewed tag comments. OS apt security packages are resolved at build time, so builds are not byte reproducible. To upgrade, inspect official manifests/source/release notes, update pins intentionally, rerun all targets including SQLite/TLS/auth/browser/simulator paths and inspect the resulting manifest before releasing. Native platform builds and manifest assembly follow [Docker's multi-platform guidance](https://docs.docker.com/build/ci/github-actions/multi-platform/); see [NativeAOT cross-compilation constraints](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile). Do not broaden platform claims based on available base images alone.

## Repeating local verification

Requires PowerShell 7, .NET 10, a Linux Docker engine, the pinned simulator image (`tests/Build-OnvifSimulator.ps1`) and Node for the HTTPS client. The production browser walkthrough additionally needs an external Playwright module/browser; the application itself has no Node dependency. Build the native fixture first so its local HTTP/TLS recorder image is available:

The deployment soak requires recorder delivery from each camera on every cycle, alongside subscription and process health checks. Compose verification uses isolated synthetic setup, checks password replacement through old/new login results and restores inherited environment values. It does not import the operator's `.env`.

```powershell
./tests/Verify-Phase7Native.ps1 -Platform linux/amd64
docker build --platform linux/amd64 -t onvif-webhook-bridge:local .
./tests/Verify-Deployment.ps1 -Image onvif-webhook-bridge:local -Platform linux/amd64 -SoakSeconds 600
./tests/Verify-Compose.ps1 -Image onvif-webhook-bridge:local
./tests/Verify-InboundTls.ps1 -Image onvif-webhook-bridge:local -Platform linux/amd64
./tests/Verify-Workflows.ps1
./tests/Verify-ImageIndex.ps1 -Build
```

For local ARM64 emulation on an amd64 builder, run the native fixture with `-Platform linux/arm64 -CrossCompile`, build the production image with `--platform linux/arm64`, and use that platform/image for deployment and inbound TLS verification. Keep separate image tags for each local target. Report emulation explicitly; native ARM64 hardware, real device mappings/outages and deployment-host interoperability remain independent gates. The index runner builds only a local ignored OCI archive and never publishes it. Docker runners remove their own uniquely named fixtures/volumes, preserving unrelated resources; ignored source staging and local images remain cached.

## Public release readiness

Documentation review on 2026-10-21 confirmed that the merged browser implementation at `fa0fad63c635c8257551f023e00dc2fdb47051e5` passed the hosted [Verify run 39364042748](https://github.com/dombrovsky/onvif-webhook-bridge/actions/runs/39364042748). This supersedes the older hosted-verification gap above for that revision; historical verification records retain their original dates and scope. It does not establish real-device or operator-host interoperability.

No GitHub releases were listed at that review. Registry inventory could not be verified with the available package permissions, so the quick start uses a local build and makes no public image availability claim. The release implementation below replaces the former tag-triggered GHCR workflow with manual, version-checked publication to both registries. Implementing this workflow does not publish artifacts or change repository/package visibility.

Before offering a public release:

- Review the exact release revision's native amd64/arm64 CI results, tag protections and existing [hardware/host/destination gates](DEPLOYMENT_VERIFICATION.md#remaining-gates-and-concrete-handoff). Keep unverified device behavior explicit; inspect current workflow logs rather than relying on historical test totals.
- When making the repository public, enable GitHub private vulnerability reporting and verify the reporting form; see [security](../SECURITY.md). GitHub restricts this feature to public repositories, and its API returned 404 while this repository was private. Visibility was left unchanged; no security contact email is published.
- Repeat the [history and tracked-file privacy audit](#privacy-audit-and-screenshot-verification) for the final public revision. The current audit passed within its stated scope; it does not certify remote caches or third-party copies.
- Keep the three reviewed [UI screenshots](BROWSER_ADMINISTRATION.md#screenshots) aligned with the released interface; replace them using synthetic data when needed.
- When image publication is separately authorized, verify GHCR package access, trusted publication and the resulting platform manifest. Only then add a tested immutable image reference to the quick start. `ONVIF_IMAGE` selects the image in Compose; use `docker compose pull` and `docker compose up -d --no-build` for a verified published image. Keep the password-file, volume and bind defaults unchanged.

The [Sponsors profile](https://github.com/sponsors/dombrovsky) was verified active on 2026-10-21; `.github/FUNDING.yml` links it. Sponsorship is optional. This documentation change does not publish images, create a release, or change repository visibility. Private vulnerability reporting remains a public-visibility step as requested.

### Privacy audit and screenshot verification

On 2026-10-21, the pre-screenshot revision `cfaf4e9` was audited after refreshing origin. The checkout is not shallow. Coverage included 42 commits across available local/remote refs and reflogs, 120 historical paths, and all 619 locally stored objects, including 370 file-content blobs. Deleted and unreachable local content was included; ignored private configuration and rollback repositories were not added to Git or uploaded.

Gitleaks 8.30.1, downloaded from its official release and checked against its published SHA-256 checksum, scanned all refs/reflogs with full history. It reported 30 commits scanned, approximately 2.12 MB scanned, and zero secret findings. A separate all-object privacy scan reviewed addresses, URL hosts, network identities, personal paths, device/setup references, credential assignments and key/token markers. Findings were generic source/security checks, synthetic fixtures, public project/dependency references and loopback defaults. Known-value matches were an ordinary administrator label and a source-code expression, not private configuration. No real credentials, private device endpoints or identifying setup details were found in this scope. Raw findings remain in ignored private local storage.

The final 119-file tracked snapshot, including the screenshot/documentation follow-up, passed a separate Gitleaks scan with zero findings; relative links/anchors and diff whitespace were also checked before commit. The three screenshots show the unmodified current production UI with actual simulator events and successful delivery to a local HTTP recorder; assets, visible content and image metadata were checked. This is focused synthetic screenshot verification, not a repeat of the full integration suite or a hardware/destination interoperability claim.

This audit cannot certify inaccessible remote recovery objects, forks, third-party clones, caches, historical external logs or secrets a scanner cannot recognize. Repeat it for new changes before public release. No history rewrite, recovery deletion, key rotation or publication was performed.

## Publishing a release

### Version source and workflow

`Directory.Build.props` is the only version definition: `VersionPrefix` is the three-number version and `VersionSuffix` is `rc.1`, `rc.2`, or empty for stable. `Version` and `InformationalVersion` combine them; `AssemblyVersion` and `FileVersion` remain numeric (`0.1.0.0` for all 0.1.0 candidates). The SDK's existing deterministic/CI and built-in SourceLink settings remain enabled; no additional SourceLink package is needed. The SDK may append the commit to informational metadata. The executable's exact `--version` command and authenticated session response remove that provenance suffix; Overview displays `v` plus that compiled version. `--version` needs neither administrator credentials nor storage and does not start the server.

Versions use SemVer without a leading `v`. Numeric prerelease identifiers cannot have leading zeroes. Build metadata (`+...`) is deliberately excluded from release inputs because Docker tags cannot represent it; provenance uses the separate revision label and informational metadata. Core components must fit numeric .NET assembly metadata (0..65534), and image tags are limited to 128 characters. Git tags and GitHub Release titles add `v`; image tags and product versions do not.

Version changes go through a small reviewed PR. This is slightly more deliberate than typing an arbitrary version into Actions: a checkout of the tagged revision always builds the intended version without release-only MSBuild overrides. Only the prefix/suffix fields need editing. Do not change version literals in fixtures: they are synthetic test scenarios.

### One-time maintainer setup

1. Protect `main` with a branch rule/ruleset requiring reviewed PRs and the existing native verification checks. The workflow rejects an unprotected main branch. Allow native `ubuntu-24.04` and `ubuntu-24.04-arm` runners.
2. Configure tag rules for `v*` that prevent updates and deletion. Allow the workflow to **create** tags, without permitting force updates. Enable GitHub immutable releases where available. Avoid other publishers that can race this workflow or mutate its version tags.
3. Create a Docker Hub repository named `onvif-webhook-bridge` in the desired namespace. Choose visibility deliberately; the workflow never changes it. Set repository Actions variable **`DOCKERHUB_NAMESPACE`** to that user/organization namespace. The account used for authentication may differ from the organization namespace.
4. Set repository Actions secrets **`DOCKERHUB_USERNAME`** and **`DOCKERHUB_TOKEN`**. Use a Docker Hub access token with read/write access to this repository only where supported, without delete permission. The read-only preflight job needs these secrets too, so do not store them solely in the release environment. Enable immutable version tags in Docker Hub where available, excluding `latest`.
5. Allow `GITHUB_TOKEN` to read contents for verification, and write contents/packages in the publisher. No PAT is required. If a GHCR package already exists, link it to this repository and grant this repository's Actions access. Private packages/images require authorized pulls; do not make the repository or package public as a side effect of setup.
6. Create a GitHub environment named **`release`**, limited to `main`. Optionally require a reviewer approval (subject to the repository's GitHub plan). Approval occurs after the full verification gate and before publication. Do not add camera/destination credentials to release jobs. The publisher's `contents: write` permission is needed to create the exact-commit tag and draft/final Release; `packages: write` is used for GHCR.

Actions are official actions pinned to existing reviewed commit SHAs. The publisher installs the distribution's Skopeo package to copy an OCI layout without rebuilding. Authentication comes from the login actions' ephemeral Docker configuration; credentials are never command arguments or release assets. Login post-steps remove that configuration.

### GitHub UI procedure

After merging this implementation and completing setup:

1. Merge the desired version-bump PR into `main`. Copy the **full 40-character SHA** of the main commit you intend to release from GitHub's commit page. The selected commit must be an ancestor of current protected main and contain this release implementation.
2. Open **Actions → Release → Run workflow**. Leave the branch selector on **main**. Enter the version **without `v`** and the copied revision SHA, then run it. The workflow refuses feature-branch dispatches, non-main revisions, source/version mismatches, existing tags/releases/drafts, and existing version tags in either registry. Authentication/network errors do not count as an available version.
3. Wait for the existing full native AMD64 and ARM64 suite. If configured, approve the `release` environment only after reviewing those checks.
4. A successful run publishes both multi-architecture image tags, verifies their exact digests/configurations, moves stable aliases when appropriate, and finalizes the GitHub Release. Follow its installation links and digest references. Do not manually create a release tag first.

| Intended release | VersionPrefix | VersionSuffix | Actions version | GitHub tag/type | Image tags in both registries |
| --- | --- | --- | --- | --- | --- |
| First candidate | `0.1.0` | `rc.1` (already set) | `0.1.0-rc.1` | `v0.1.0-rc.1`, prerelease | `0.1.0-rc.1` only |
| Next candidate | `0.1.0` | `rc.2` | `0.1.0-rc.2` | `v0.1.0-rc.2`, prerelease | `0.1.0-rc.2` only |
| Stable | `0.1.0` | empty | `0.1.0` | `v0.1.0`, stable | `0.1.0`, then `latest` |

For `0.2.0` or `1.0.0`, bump the prefix and leave the suffix empty. Each row uses the SHA of its own merged version commit. The release workflow creates no automatic version-bump commit or PR. Use the normal main-branch CI before release; the release also repeats that same reusable gate with the chosen immutable SHA and source version.

### Images, installation and upgrades

Both registries receive the same tested content and multi-architecture index:

- `ghcr.io/dombrovsky/onvif-webhook-bridge:<version>`
- `<configured-namespace>/onvif-webhook-bridge:<version>` on Docker Hub

Runnable architectures are exactly `linux/amd64` and `linux/arm64`. There are no public platform-specific moving aliases or minor/major aliases. Prereleases never touch `latest` or GitHub's latest stable designation. Stable releases update `latest` in both registries only after both immutable version manifests pass verification. Prefer explicit version tags or the index digest in the GitHub Release over `latest` for controlled upgrades. This documentation does not claim any images/releases already exist.

After a version is published, authenticate if its registry visibility requires it, and set the chosen image in `.env`, for example:

```sh
ONVIF_IMAGE=ghcr.io/dombrovsky/onvif-webhook-bridge:0.1.0-rc.1
```

With the existing password-file and persistent-volume settings intact:

```sh
docker compose pull
docker compose up -d --no-build
docker compose ps
```

Before upgrading, stop and take a consistent private data backup as described above, then change only the pinned image and recreate using the same Compose project/volume. Never run `docker compose down --volumes` to upgrade. Login sessions expire on restart; configuration remains. A schema rollback may require the corresponding backup.

For a local image with release-style OCI labels, supply the source version and revision explicitly (ordinary local builds remain supported):

```sh
docker build --build-arg VERSION=0.1.0-rc.1 --build-arg REVISION="$(git rev-parse HEAD)" -t onvif-webhook-bridge:local .
docker run --rm --network none onvif-webhook-bridge:local --version
```

`VERSION` is checked against MSBuild's source value before publishing the binary, not used to override it. Local builds without these optional arguments still have a meaningful compiled/UI version, with empty version/revision OCI labels. Release CI always supplies and validates both labels.

### Artifact verification and deliberate limits

The reusable CI gate retains the existing simulator, tests, NativeAOT smoke, browser, Compose, TLS, deployment and soak checks on native runners. Its checkout is the selected SHA. Before export on each runner, the final production image must report the expected compiled version using `--version` without network, writable root, capabilities or administrator setup. Its architecture and OCI version/source/exact revision/MIT license labels must match. CI records the tested Docker config ID, archive SHA-256, binary version and commit alongside each saved image.

The publisher downloads only those artifacts from the **same workflow run**. It checks archive hashes and evidence, converts them once into a local OCI layout, checks the original tested config digests, validates labels again, and assembles exactly two runnable platform descriptors. Docker schema-2 platform configs are retained so conversion cannot strip the tested Docker health check. Skopeo copies that same layout to both registries using `--all --preserve-digests`; no application/image rebuild happens after tests. It reads back each registry's index and platform manifests, checks their SHA-256 digests and labels against the local evidence, and checks the exact release tag target before finalization. Generated notes include pinned image digests and setup links. The attached `release.json` records source, index/platform digests and tested-artifact hashes. Workflow evidence is retained for 90 days; tested image archives for 7 days.

A draft Release is created after verification and version reservation, before registry writes. It becomes public as a successful release only after both registries (and stable aliases, if any) pass verification. The workflow checks the final prerelease/draft state. There is no atomic transaction across GitHub and two registries: an intermediate failure is explicitly incomplete, leaves its draft unpublished, and may leave one registry/tag or one stable alias updated. See recovery below.

Standalone binary downloads are deferred: Docker remains the supported runtime contract, including OS dependencies, permissions and TLS assets. A bare NativeAOT executable does not carry that contract. Adding separate archive/runtime-support verification would add maintenance beyond this release path. No second binary is rebuilt for release assets. SBOMs, vulnerability scanners, Gitleaks in hosted release CI and attestations are also deferred; adding them later should include reviewed tool pins, base-image severity/suppression policy and explicit verification rather than unreliable default release blockers. Local secret review remains part of the implementation handoff.

### Recovering a failed release

Inspect the first failed step, the workflow summary, preserved `release-evidence`, the GitHub tag/draft, and **both registries**. A failed workflow is not a successful release even if one version tag is visible in a registry. Do not advertise a draft or manually finalize it after a failed verification.

- **Before tag reservation:** after fixing setup/transient errors, rerun using the same version/revision if no tag, draft, or registry version exists. Verification is rerun; expired CI artifacts cannot be silently substituted.
- **After tag reservation or any publication:** the version is consumed. Keep its tag/draft/evidence for diagnosis; never move/recreate the tag, overwrite assets/images, or use a rerun to repair it. Fix the problem through a PR, choose a new unused version (for example `0.1.0-rc.2` after a failed `rc.1`, or the next patch after a failed stable version), then dispatch that revision. Any existing tag, draft, or version image causes the preflight to reject reuse, including a same-commit retry.
- **Stable alias failure:** versioned images may both be valid while only one `latest` moved. Treat the release as incomplete; use a known successful pinned digest while preparing the next stable version. Prereleases cannot repair/move stable aliases. Publication of that next verified stable version reconciles both aliases.
- **Failure after finalization:** inspect the actual Release state and digest evidence. A final state-check/API connection failure can leave an already published Release. Never rerun to overwrite it. Confirm its tag, both manifests and prerelease flag before treating it as successful; fix any issue with a new version.

Repository permissions/rules and registry immutability settings should prevent out-of-band mutations. Preflight collision checks plus serialized workflow writes cannot defend against an unrelated publisher with unrestricted concurrent write credentials. Do not run another publisher for these version tags.
