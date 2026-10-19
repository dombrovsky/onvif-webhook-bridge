# Browser administration

Open the application root after [administrator setup](ADMINISTRATION.md). The same NativeAOT process serves embedded HTML, CSS and vanilla JavaScript. No frontend build, CDN or extra service is required. The public login shell contains no configuration; all data requires an authenticated session.

## Overview and cameras

Overview shows subscribed cameras/total, enabled rules, retained notification count, recent completed executions and event snapshots. These are bounded process snapshots, not durable metrics. History, execution results and queues clear on restart. Connection feedback identifies stale data after refresh failures.

Cameras supports add/edit, enable/disable, connectivity testing and confirmed deletion. Status includes subscription state, last contact/event and failures. Reachable means the bounded connectivity request succeeded; it does not establish subscription readiness. Referenced cameras cannot be deleted until their rules are removed. One disconnected camera does not stop the others.

## Event Explorer and rule editing

Event Explorer includes false, unmatched, Initialized, Deleted and truncated notifications. Search filters the retained snapshot by camera/topic/fields/values/operation without changing admission or stored history. Retention remains at most 100 notifications within the existing 1 MiB budget. Inspect shows complete retained JSON as inert text, including timestamps, dialect/namespace, duplicate Source/Data/Key arrays, ElementItems, extensions, diagnostics and truncation. Raw XML is the library-exposed subtree, not the full original SOAP envelope.

Draft from an observed topic or scalar Source/Data field. New rules always save disabled. Truncated notifications remain inspectable but cannot establish exact drafts. Review the camera, exact topic and selected condition before saving. No vendor topic or field name is assumed.

The rule editor uses a two-column desktop surface with a persistent event reference. Fields and Raw JSON switch without rebuilding the form; Copy retained JSON copies the complete retained observation. Eligible scalar Source/Data buttons fill section/name/value while preserving comparison. Replacing a current condition asks for confirmation. Duplicate names match if any selected field value matches. Key/structured/missing values are inspection only. Editing an existing rule offers a real retained event for its camera/topic when available; otherwise it explicitly reports no retained event.

On phones, View event / Hide event controls an inline reference above the form. The editor has one main scroll surface, reachable Save/Cancel and no independently scrolling event cage. Inputs and event selection survive reference switches and polling. Every editor field has an info button that supports touch, keyboard focus and Escape dismissal.

General, WHEN and THEN sections expose the common settings. Advanced matching & delivery holds Text/Boolean, Occurrence/Transition, cooldown, retries, header rows and literal JSON body; it opens automatically when relevant existing settings are active. Text is ordinal/case-sensitive; Boolean accepts true/1 and false/0 only. Transition requires a field condition and a known nonmatching-to-matching change; first observations/Initialized establish baseline and Deleted clears state. Retry defaults to zero and can duplicate side effects. Verify real activation/reset and stable identity privately before enabling a hardware transition mapping.

## URLs and secrets

New browser rules default to a visible URL. Sensitive URL marks it write-only on future API reads, including after reload. The separate Show/Hide entered URL control changes only the local replacement display. Existing sensitive URLs have a presence indicator and blank disabled replacement input, never a fake mask used as form data. Sensitive-to-visible changes require re-entry; a stored secret cannot be revealed. Visible-to-sensitive changes can preserve the destination. Visible URLs are available only to authenticated administrators and should contain no tokens. Existing databases and API callers that omit classification retain write-only URLs.

Camera credentials, headers and JSON bodies remain write-only. Unchanged maps to Preserve, Edit to Replace and explicit confirmed Clear to Clear. Undo reverses an edit/clear before save. Closing, cancelling or signing out discards replacement values. Required URLs cannot be cleared. Clear both camera username and password for unauthenticated cameras.

Header name/value rows replace the entire collection: re-enter all needed headers, including authentication. Stored rows are never reconstructed or incrementally edited. Values are masked while typing. POST bodies are literal JSON without interpolation. Changing POST to GET with a stored body requires explicit body clearing.

Camera replacements retain stable names and per-camera username/current-password autocomplete sections. Password-manager prompts depend on the browser/profile and are not guaranteed. Sign-in preserves the submitted fields until successful navigation; failed sign-in stays on the form.

Ignore certificate validation is off by default. Opting in accepts untrusted/expired/mismatched HTTPS certificates for this webhook's delivery and manual tests, enabling interception/impersonation. A visible warning accompanies the option and rule card. Existing isolated HttpClientFactory pools retain redirect/proxy/address restrictions, request bounds and strict camera TLS. See [TLS evidence](WEBHOOK_TLS_VERIFICATION.md).

## Execution and sessions

Manual HTTP test requires explicit confirmation and sends the saved action, including when the rule is disabled; its camera must be enabled. It bypasses matching/cooldown and retains dispatcher deadlines, capacity, revision checks and retry settings. Queue acceptance, HTTP transport acceptance and destination-side effects are distinct. Tests here use only disposable local recorders.

Current and last-completed results show category, attempts, HTTP code and timing. Execution details displays the final attempt's bounded JSON response as inert text (16 KiB/depth 32). No-response transport errors say so. Response previews can contain sensitive data; they remain transient and are never logged/persisted.

Editors retain their opening revision. Conflicts keep the draft and require deliberate cancel/refresh/reopen/review; mutations are never automatically retried. Unknown network outcomes require review before resubmission. Polling uses five sequential reads, a twelve-second cycle deadline and a three-second delay. Selection, inspection, form editing and search focus defer screen redraw while keeping only the latest pending snapshot. Status-button focus and page scroll are restored on background redraw. Connection/session handling continues; logout/expiry immediately cancel polling, discard pending snapshots and clear protected content even during editing or selection. Refresh now supersedes the previous cycle. A failed cycle reports stale data.

Strict CSP, same-origin mutations, CSRF, HttpOnly/SameSite sessions, source-generated JSON and transaction-before-activation remain enforced. Dynamic content uses safe DOM text/value operations. No URL flavor is logged. Filesystem permissions protect recoverable configuration; this is not encryption at rest. See [storage](DATA_STORAGE.md), [deployment](DEPLOYMENT.md) and [redesign verification](UI_REDESIGN_VERIFICATION.md) for limits and evidence.
