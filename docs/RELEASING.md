# Releasing

Maintainer reference for [.github/workflows/images.yml](../.github/workflows/images.yml),
[the reusable CI gate](../.github/workflows/ci.yml) and [build/release.py](../build/release.py).
Running the Release workflow writes tags, GitHub Releases and registry images; obtain explicit publication authorization.
For installation and upgrades, use [deployment](DEPLOYMENT.md).

## Version source and workflow

`Directory.Build.props` is the only version definition: `VersionPrefix` is the three-number version and `VersionSuffix` is `rc.$(RcNumber)` or empty for stable. `RcNumber` uses GitHub Actions `GITHUB_RUN_NUMBER`, with local fallback `1`. Each new run increments the counter for that workflow; reruns retain the number, counters do not reset for a new prefix, and Verify and Release have separate counters. The reusable release verification uses its caller's counter. Docker receives the same counter as a build argument. `Version` and `InformationalVersion` combine them; `AssemblyVersion` and `FileVersion` remain numeric (`0.1.0.0` for all 0.1.0 candidates). The SDK's existing deterministic/CI and built-in SourceLink settings remain enabled; no additional SourceLink package is needed. The SDK may append the commit to informational metadata. The executable's exact `--version` command and authenticated session response remove that provenance suffix; Overview displays `v` plus that compiled version. `--version` needs neither administrator credentials nor storage and does not start the server.

Versions use SemVer without a leading `v`. Numeric prerelease identifiers cannot have leading zeroes. Build metadata (`+...`) is deliberately excluded from source release versions because Docker tags cannot represent it; provenance uses the separate revision label and informational metadata. Core components must fit numeric .NET assembly metadata (0..65534), and image tags are limited to 128 characters. Git tags and GitHub Release titles add `v`; image tags and product versions do not.

Version changes go through a small reviewed PR. The release workflow reads the prefix/suffix from `Directory.Build.props` at the selected commit; there is no version input in Actions. That derived version is passed unchanged to verification and publication and checked against the source again before registry writes. To reproduce a tagged candidate locally, set `GITHUB_RUN_NUMBER` to the candidate number and pass `--build-arg GITHUB_RUN_NUMBER=<N>` to Docker. Only prefix changes and switching to stable need a version PR; new candidates use the workflow counter. Do not change version literals in fixtures: they are synthetic test scenarios.

## One-time maintainer setup

1. Protect `main` with a branch rule/ruleset requiring reviewed PRs and the existing native verification checks. The workflow rejects an unprotected main branch. Allow native `ubuntu-24.04` and `ubuntu-24.04-arm` runners.
2. Configure tag rules for `v*` that prevent updates and deletion. Allow the workflow to **create** tags, without permitting force updates. Enable GitHub immutable releases where available. Avoid other publishers that can race this workflow or mutate its version tags.
3. Create a Docker Hub repository named `onvif-webhook-bridge` in the desired namespace. Choose visibility deliberately; the workflow never changes it. Set repository Actions variable **`DOCKERHUB_NAMESPACE`** to that user/organization namespace. The account used for authentication may differ from the organization namespace.
4. Set repository Actions secrets **`DOCKERHUB_USERNAME`** and **`DOCKERHUB_TOKEN`**. Use a Docker Hub access token with read/write access to this repository only where supported, without delete permission. The read-only preflight job needs these secrets too, so do not store them solely in the release environment. Enable immutable version tags in Docker Hub where available, excluding `latest`.
5. Allow `GITHUB_TOKEN` to read contents for verification, and write contents/packages in the publisher. No PAT is required. If a GHCR package already exists, link it to this repository and grant this repository's Actions access. Private packages/images require authorized pulls; do not make the repository or package public as a side effect of setup.
6. Create a GitHub environment named **`release`**, limited to `main`. Optionally require a reviewer approval (subject to the repository's GitHub plan). Approval occurs after the full verification gate and before publication. Do not add camera/destination credentials to release jobs. The publisher's `contents: write` permission is needed to create the exact-commit tag and draft/final Release; `packages: write` is used for GHCR.

Actions are official actions pinned to existing reviewed commit SHAs. The publisher installs the distribution's Skopeo package to copy an OCI layout without rebuilding. Authentication comes from the login actions' ephemeral Docker configuration; credentials are never command arguments or release assets. Login post-steps remove that configuration.

## GitHub UI procedure

After completing the repository and registry setup:

1. Select the desired merged revision on `main`; merge a version PR first when changing the prefix or switching to stable. Copy the **full 40-character SHA** of the main commit you intend to release from GitHub's commit page. The selected commit must be an ancestor of current protected main and contain this release implementation.
2. Open **Actions → Release → Run workflow**. Leave the branch selector on **main**. Enter the copied revision SHA, then run it. The workflow reads the version from that revision's `Directory.Build.props` and shows it in the run summary. The workflow refuses feature-branch dispatches, non-main revisions, source/version mismatches, existing tags/releases/drafts, and existing version tags in either registry. Authentication/network errors do not count as an available version.
3. Wait for the existing full native AMD64 and ARM64 suite. If configured, approve the `release` environment only after reviewing those checks.
4. A successful run publishes both multi-architecture image tags, verifies their exact digests/configurations, moves stable aliases when appropriate, and finalizes the GitHub Release. Follow its installation links and digest references. Do not manually create a release tag first.

| Intended release | VersionPrefix | VersionSuffix | Automatically resolved version | GitHub tag/type | Image tags in both registries |
| --- | --- | --- | --- | --- | --- |
| Release run 42 | `0.1.0` | `rc.$(RcNumber)` | `0.1.0-rc.42` | `v0.1.0-rc.42`, prerelease | `0.1.0-rc.42` only |
| Release run 43 | `0.1.0` | `rc.$(RcNumber)` | `0.1.0-rc.43` | `v0.1.0-rc.43`, prerelease | `0.1.0-rc.43` only |
| Stable | `0.1.0` | empty | `0.1.0` | `v0.1.0`, stable | `0.1.0`, then `latest` |

For `0.2.0` or `1.0.0`, bump the prefix and leave the suffix empty. Candidate runs can reuse the same merged SHA; stable releases require the merged empty-suffix change. The release workflow creates no automatic version-bump commit or PR. Use the normal main-branch CI before release; the release also repeats that same reusable gate with the chosen immutable SHA and source version.

## Local version and image labels

For a local image with release-style OCI labels, supply the source version and revision explicitly (ordinary local builds remain supported):

```sh
docker build --build-arg GITHUB_RUN_NUMBER=1 --build-arg VERSION=0.1.0-rc.1 --build-arg REVISION="$(git rev-parse HEAD)" -t onvif-webhook-bridge:local .
docker run --rm --network none onvif-webhook-bridge:local --version
```

`VERSION` is checked against MSBuild's source value before publishing the binary, not used to override it. For another candidate number, pass the same `GITHUB_RUN_NUMBER` to local MSBuild and Docker. Local builds without these optional arguments still have a meaningful compiled/UI version, with empty version/revision OCI labels. Release CI always supplies and validates both labels.

## Artifact verification and deliberate limits

The reusable CI gate retains the existing simulator, tests, NativeAOT smoke, browser, Compose, TLS, deployment and soak checks on native runners. Its checkout is the selected SHA. Before export on each runner, the final production image must report the expected compiled version using `--version` without network, writable root, capabilities or administrator setup. Its architecture and OCI version/source/exact revision/MIT license labels must match. CI records the tested Docker config ID, archive SHA-256, binary version and commit alongside each saved image.

The publisher downloads only those artifacts from the **same workflow run**. It checks archive hashes and evidence, converts them once into a local OCI layout, checks the original tested config digests, validates labels again, and assembles exactly two runnable platform descriptors. Docker schema-2 platform configs are retained so conversion cannot strip the tested Docker health check. Skopeo copies that same layout to both registries using `--all --preserve-digests`; no application/image rebuild happens after tests. It reads back each registry's index and platform manifests, checks their SHA-256 digests and labels against the local evidence, and checks the exact release tag target before finalization. Generated notes include pinned image digests and setup links. The attached `release.json` records source, index/platform digests and tested-artifact hashes. Workflow evidence is retained for 90 days; tested image archives for 7 days.

A draft Release is created after verification and version reservation, before registry writes. It becomes public as a successful release only after both registries (and stable aliases, if any) pass verification. The workflow checks the final prerelease/draft state. There is no atomic transaction across GitHub and two registries: an intermediate failure is explicitly incomplete, leaves its draft unpublished, and may leave one registry/tag or one stable alias updated. See recovery below.

Standalone binary downloads are deferred: Docker remains the supported runtime contract, including OS dependencies, permissions and TLS assets. A bare NativeAOT executable does not carry that contract. Adding separate archive/runtime-support verification would add maintenance beyond this release path. No second binary is rebuilt for release assets. SBOMs, vulnerability scanners, Gitleaks in hosted release CI and attestations are also deferred; adding them later should include reviewed tool pins, base-image severity/suppression policy and explicit verification rather than unreliable default release blockers. Secret/privacy review remains part of contribution and release review.

## Recovering a failed release

Inspect the first failed step, the workflow summary, preserved `release-evidence`, the GitHub tag/draft, and **both registries**. A failed workflow is not a successful release even if one version tag is visible in a registry. Do not advertise a draft or manually finalize it after a failed verification.

- **Before tag reservation:** after fixing setup/transient errors, rerun using the same version/revision if no tag, draft, or registry version exists. Verification is rerun; expired CI artifacts cannot be silently substituted.
- **After tag reservation or any publication:** the version is consumed. Keep its tag/draft/evidence; never move/recreate the tag, overwrite assets/images or use a rerun to repair it. For a failed candidate, start a new workflow run for the desired protected-main revision to obtain a fresh counter; reruns retain the consumed number. For a failed stable version, merge a new unused patch version before dispatch. Existing tags, drafts or registry versions cause preflight to reject reuse, including same-commit retries.
- **Stable alias failure:** versioned images may both be valid while only one `latest` moved. Treat the release as incomplete; use a known successful pinned digest while preparing the next stable version. Prereleases cannot repair/move stable aliases. Publication of that next verified stable version reconciles both aliases.
- **Failure after finalization:** inspect the actual Release state and digest evidence. A final state-check/API connection failure can leave an already published Release. Never rerun to overwrite it. Confirm its tag, both manifests and prerelease flag before treating it as successful; fix any issue with a new version.

Repository permissions/rules and registry immutability settings should prevent out-of-band mutations. Preflight collision checks plus serialized workflow writes cannot defend against an unrelated publisher with unrestricted concurrent write credentials. Do not run another publisher for these version tags.

## Verification and release review

CI runs native amd64 and arm64 jobs with JIT tests, onvifsim, NativeAOT runtime checks,
production browser/deployment tests, a 600-second two-camera delivery soak, Compose
bootstrap/reset and validated inbound TLS. Browser tooling runs on amd64. See
[development validation](DEVELOPMENT.md#validation) for local commands. Inspect the
checks for the exact selected SHA; historical test totals do not certify a new release.

PR verification has contents-read permissions and needs no registry credentials.
Preflight additionally reads packages and authenticates to both registries; only the
publisher gets contents-write/packages-write. All releases share a concurrency group
with cancellation disabled. GitHub may replace older pending runs; avoid queuing
several releases. See [GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency).

Before publication, review the diff and available Git history for credentials, private
addresses, device identities and unsanitized captures. A secret scanner complements
manual privacy review; neither certifies remote caches or third-party clones. Keep
screenshots synthetic and aligned with the released UI; see
[screenshot maintenance](DEVELOPMENT.md#screenshots). Verify the repository's private
vulnerability-reporting channel described in [SECURITY.md](../SECURITY.md).

Dependency, base-image and Actions pins are intentional. Check version-matched
sources and release notes when updating them; rerun supported targets, including
SQLite/TLS/authentication/browser/simulator paths, and inspect the platform index.
OS packages resolved during builds mean these builds are not byte reproducible.
Operator hardware/proxy interoperability, real camera activation/reset and reboot
recovery, and destination-side effects require separate evidence; see
[compatibility](DEVELOPMENT.md#compatibility-and-evidence).
