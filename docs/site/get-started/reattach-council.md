# Reattach a retained council

`dsf onboard decide reattach` explicitly returns a fully detached, non-purged
factory to its original product, repository, subscription, and application
environment identity. It never adopts a matching name or tag, retargets the
factory, restores an old approval, or activates proposal publication.

Review current retained identity, application drift, owner claims, GitHub App
authority, and exact factory access:

```bash
dsf onboard decide reattach \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --operation 33333333-3333-3333-3333-333333333333 \
  --json
```

The read-only result is `ReviewRequired`. Preserve its operation ID and approve
only its exact current fingerprint:

```bash
dsf onboard decide reattach \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --operation 33333333-3333-3333-3333-333333333333 \
  --approve <reattachment-plan-fingerprint> \
  --json
```

Approval conditionally reacquires only the factory's exact eligible owner
claims and reopens preparation admission. Competing or uncertain claims,
changed immutable identity, missing retained authority, retired identity, and
concurrent lifecycle changes remain `Blocked`, `Partial`, or `Unknown`.
Application resources, repository content/settings, shared owner assets,
factory ID, and historical audit/publication provenance remain preserved.
Before approval, DSF live-checks retained infrastructure incarnations, exact
grant and key custody, the GitHub App installation/permissions, repository
source access, and the private verification path. The reviewed fingerprint
binds those observations.

`AwaitingReadiness` is not activation. Reverify GitHub/source access,
telemetry, models, and every selected supplemental source; reconcile any
reviewed drift; refresh and explicitly re-adopt the charter; run a fully evaluated
supervised preview; then use a separate `dsf onboard decide activate` review
and approval bound to the new current revisions. Retained compute, old
readiness, and old approval never resurrect publication automatically.
All runtime entry paths remain closed while the protected reattachment request
is awaiting this evidence and activation.

Stale preview, activation, live, publisher, promotion, offboarding, and current
charter-adoption roots are retired only after their complete values are
durably chunked with original key, ETag, and SHA-256. Conditional deletion
progress resumes under the original operation after lost responses; changed
records are never deleted.

Redirected and `--json` use never prompts. Missing exact locators or operation
ID fails closed. Keep the structured outcome for recovery; it contains no
credentials or source contents.
