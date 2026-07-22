# Architecture

## Context

FintechLedgerApi is a single deployable process that accepts JSON over HTTP and maintains an in-memory double-entry journal. Callers are assumed to be trusted internal channels (no end-user auth in this sample).

## Containers

| Container | Responsibility |
|-----------|----------------|
| FintechLedger.Api | HTTP edge, validation mapping, security headers, OpenAPI |
| LedgerStore (in-process) | Journal posting, balances, idempotency, audit chain |

There is no separate database container in this repository. Restart clears state.

## Components

- **Account registry** — customer and system clearing accounts keyed by id
- **Journal** — immutable list of balanced entries (corrections via reverse entries)
- **Idempotency map** — key → (entryId, payload fingerprint)
- **Audit chain** — SHA-256 linked events for tamper evidence inside one process

## Cross-cutting rules

1. Debits must equal credits on every post.
2. Balances are derived; never stored as a writable field.
3. Posted lines are never updated or deleted.
4. Amounts allow at most two decimal places.
5. Currencies must pass the ISO 4217 allow-list used by `Iso4217`.

## Threat notes (sample)

| Threat | Mitigation in this demo |
|--------|-------------------------|
| Duplicate post on retry | Idempotency keys + fingerprint |
| Oversized body DoS | Kestrel max body 64 KiB |
| Clickjacking / MIME sniff | `X-Frame-Options`, `X-Content-Type-Options` |
| Audit rewrite in memory | Hash chain (`/api/audit/verify`); not durable against process owner |
