# Brief to the agent (Claude Code)

**Exercise (Day 22 - Resilience with Polly):** wrap an outbound dependency with Polly:
retry-with-backoff (idempotent only), a circuit breaker, a timeout, and a bulkhead. Prove the
circuit opens under sustained failure and recovers.

**Where:** `day-22/piece1`. Backend is [day-21/piece1](../../day-21/piece1)'s `QuotesApi`, copied
unmodified into [QuotesApi/](QuotesApi/) so the resilience pipeline could be added without
touching the read-only original. Frontend is day-21/piece1's Angular app, copied unmodified into
[quotes-list-detail/](quotes-list-detail/) with one new tab added: **Resilience**.

**What's new:** this app had no outbound HTTP dependency at all before this day - every prior
day's integration point was either the DB, Redis, or Service Bus, none of which are "an outbound
dependency" in the sense this exercise means (a downstream HTTP API whose failures a caller has to
defend itself against). Day 22 adds one on purpose: `IQuoteEnrichmentClient`, a typed `HttpClient`
wrapped in a Polly resilience pipeline (`Microsoft.Extensions.Http.Resilience`) with all four named
primitives - see README.md for the full pipeline code and section 3-4 for proof of each one working
independently, plus the full breaker open → half-open → closed cycle with real application logs.

**Do not modify** `day-21/piece1` (or anything upstream of it) - read-only reference / copy source,
same rule every prior day has used. Anything needed from it was copied into `day-22/piece1` first,
never edited in place.

## Why the "dependency" is in-process, not a real third party

The dependency behind `IQuoteEnrichmentClient` is `FlakyDependencyHandler` - a custom
`HttpMessageHandler` registered as the *primary* handler for the client, so a real
`HttpRequestMessage` still flows through the whole Polly pipeline (bulkhead → retry → circuit
breaker → timeout) exactly as it would for a real outbound call; only the bottom-most "make the
network call" step is faked, deterministically, from a runtime-configurable in-memory state
(`FlakyDependencyState`: healthy / always-fail / slow / intermittent, with configurable latency and
failure rate).

This was a deliberate choice, not a shortcut: wrapping a real third-party API here would mean the
"prove the circuit opens under sustained failure and recovers" gate depends on that API actually
being down at the right moment during the demo - unreproducible, and not something this session
could guarantee on request. The in-process fake makes the exact same proof deterministic and
repeatable (see README.md section 4's "Run breaker demo" narration, reproduced live via
Playwright), while keeping the resilience *configuration itself* identical to what a real outbound
HttpClient dependency would use - `AddHttpClient(...).AddResilienceHandler(...)` doesn't know or
care what's behind `ConfigurePrimaryHttpMessageHandler`.

## Deployment scope this session

**Not deployed to Azure.** Day 21's session (immediately prior) spent a large amount of its time
on a live-deployment saga: a real Redis-connectivity problem with Azure Managed Redis from the
existing App Service, and a real config bug (`appsettings.Production.json` silently inheriting a
local-dev default) that only a live deployment surfaced. Given that recent experience, this
session's scope was kept to proving the resilience pipeline works correctly - locally, but with the
same rigor (real logs, real metrics, a real headless-browser pass) - rather than also taking on a
second live-infrastructure risk in the same sitting. The user did not request a live deployment for
this exercise; if they want one, day-22 can be redeployed onto the same `syquotes17-api` App
Service day-19/20/21 already use, following the same process.

## The exercise's own gates

- Paste the resilience pipeline - see README.md section 2, the actual
  `InfrastructureExtensions.cs` registration code, order-of-strategies reasoning included.
- Show logs/metrics of the breaker opening then half-opening to recovery - see README.md section
  4: real application console log lines from a live fail → open → wait → heal → close cycle, the
  same cycle's `timeline` field from `GET /api/resilience/metrics` (timestamped, sourced from the
  circuit breaker's own `OnOpened`/`OnHalfOpened`/`OnClosed` callbacks), and the same proof
  reproduced live in the browser (screenshots in [screenshots/](screenshots/)), verified with
  Playwright.
