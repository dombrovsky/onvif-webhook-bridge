# Coding-agent instructions

## Current repository state and authorization

This repository is in a requirements and planning session. The current authorized deliverables are only `REQUIREMENTS.md`, `IMPLEMENTATION_PLAN.md`, and this file, committed to Git. Do not create the application, build scaffolding, tests, UI, Docker/Compose files, workflows, or README during this session.

When a later user explicitly authorizes implementation, work on the requested phase of `IMPLEMENTATION_PLAN.md`. That later instruction supersedes this planning-only restriction. Read both planning documents before coding. Do not interpret a phase request as authorization to implement the entire product.

## Implementation discipline

- Phase 1 must be the end-to-end ONVIF observation proof of concept, including NativeAOT feasibility. Do not jump to presumed human/motion schemas, UI or persistence. Record all properties and actual event lifecycle first.
- Distinguish source-reviewed API capability, synthetic fixture results, JIT tests, published NativeAOT runtime results and real-camera interoperability. If the camera is unavailable, report the hardware gate pending and follow the plan's exit gate. Never describe simulation as device or destination interoperability verification.
- Check the restored pinned SharpOnvifClient API and version-matched source/sample before writing calls. Do not invent methods, return shapes, renewal, cancellation, or endpoint-reference support. Current main documentation may differ from a released package.
- Never assume a boolean field is called `State`. Preserve vendor topics, all Source/Data/Key fields, duplicate names, ElementItems, property operations and extension details. Inspector admission precedes matching/cooldown/dispatch and includes false/unmatched notifications.
- Keep a single .NET 10 NativeAOT Minimal API process with DI, independent PullPoint workers, HttpClientFactory, direct SQLite and small plain browser files. Avoid React/Node, MVC, brokers, ORMs without demonstrated need, generic framework layers, plugins, and vendor-specific core code.
- Add only abstractions required by an actual external seam or test. Reuse proven code from earlier phases; prefer a few concrete classes over speculative architecture.
- Enforce bounded event bytes/count, transition identities, queues, request deadlines, retries and shutdown. A failed HTTP action or camera must not stop other subscriptions. Coordinate lease renewal with long-poll timing and recheck eligibility/revisions before dispatch.
- Follow the exact/boolean/transition/cooldown semantics in REQUIREMENTS.md; document any evidence-driven change before coding it. Do not silently add deduplication or suppress inspector events.

## Secrets and operation

- Keep all repository files generic and free of personal or deployment-specific information: no real IP addresses (including private/local addresses), hostnames, usernames, personal names, device brands/models, firmware identifying a personal device, destination products, or details of the user's setup. This applies to documentation, examples, fixtures, screenshots, logs, source, configuration and commit messages. Use clearly labelled placeholders and synthetic examples; retain real test configuration and device-identifying evidence only in ignored private local files. Audit files for these details before committing.
- Never commit camera credentials, destination tokens, real destination URLs, `.env`, local DBs, or unsanitized SOAP captures. Use ignored local configuration and sanitized fixtures.
- Do not log URLs, authorization headers, passwords, JSON bodies or arbitrary network exception text. Audit library and HttpClientFactory diagnostics as well as application logs. Seed fake secrets in redaction tests.
- Authenticate administration, events and manual test endpoints; protect mutations from CSRF; escape all event/camera content. Fail closed without administrator setup. Mask secrets on reads and make replacement/clearing explicit.
- Recoverable camera/action secrets require restricted `/data` ownership and permissions; do not claim encryption at rest when only permissions exist. No global certificate-validation bypass.
- Manual destination tests cause real external actions. Send only to explicitly supplied/configured destinations within the authorized implementation test scope; use a local recorder otherwise. Coordinate camera restarts with the user; do not change firmware, camera detection settings, video/PTZ or destination-host settings as an incidental test.
- Keep all integration examples vendor-independent. A 2xx HTTP response proves transport acceptance, not that the destination performed an action.

## Verification and handoff

- Use focused tests from the current phase's acceptance criteria. Avoid tests that merely mirror implementation. Use a controllable clock/fake transport for timing and recovery tests.
- Publish NativeAOT early and run the native output on Linux, especially ONVIF parsing, SQLite native loading, JSON and authentication. A successful JIT build is not sufficient. Investigate AOT/trimming warnings; no blanket suppression.
- Start container support with linux/amd64 and linux/arm64; ARMv7 is conditional on verified toolchain/native assets. Do not promise Linux 32-bit x86. Test native hardware when available and label emulation separately.
- Before completing a phase, run relevant checks, inspect the diff for scope and secrets, update its evidence/handoff record and planning assumptions, and state remaining pending hardware checks. Never fabricate build success or camera observations.
- Commit scoped work when requested; preserve unrelated user changes and existing license. Do not push, publish container images, or expand implementation scope unless authorized by the user/session.

## Excluded features

No streaming, recording, FFmpeg, RTSP proxy, MQTT, firmware integration, mobile/push infrastructure, complex automation, plugin system, multi-user/RBAC, vendor-specific core logic, or extra services.
