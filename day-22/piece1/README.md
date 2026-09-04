# Day 22 / Piece 1 — Resilience with Polly

Backend is [day-21/piece1](../../day-21/piece1)'s `QuotesApi`, copied unmodified into
[QuotesApi/](QuotesApi/) so the resilience pipeline could be added without touching the read-only
original. Frontend is day-21/piece1's Angular app, copied unmodified into
[quotes-list-detail/](quotes-list-detail/) with one new tab: **Resilience**.

## Current status

**Verified locally, end to end** - backend, live application logs, the frontend tab, and a
headless-browser (Playwright) pass, all showing the real Closed → Open → HalfOpen → Closed cycle.
**Not deployed to Azure this session** - after day-21's live-deployment debugging saga (a real
config bug and an unusable Managed Redis instance, both only caught in production), this session's
scope was kept to proving the resilience pipeline itself works correctly, rather than also taking
on a second live-infrastructure risk in the same session. Everything below is real, reproducible
evidence - not simulated - just gathered against `localhost` rather than `syquotes17-api`.

## 1. The outbound dependency being wrapped

This exercise's "outbound dependency" is `FlakyDependencyHandler` - a custom `HttpMessageHandler`
registered as the *primary* handler for a typed `HttpClient`, so every call still constructs a
real `HttpRequestMessage` and flows through the full Polly pipeline exactly as it would for any
other outbound call; only the bottom-most "make the network call" step is faked, deterministically:

```csharp
public sealed class FlakyDependencyHandler(FlakyDependencyState state, IResilienceMetrics metrics) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        metrics.RecordDependencyAttempt();

        var (shouldFail, delayMs) = state.NextOutcome();
        await Task.Delay(delayMs, cancellationToken);

        if (shouldFail)
        {
            metrics.RecordDependencyFailure();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request, ... };
        }

        metrics.RecordDependencySuccess();
        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, ... };
    }
}
```

`FlakyDependencyState` holds a mutable mode (`Healthy` / `AlwaysFail` / `Slow` / `Intermittent`),
latency, and failure rate, controllable at runtime from `POST /api/resilience/dependency/configure`
- and from the Resilience tab's "Dependency control" panel. This is the same design choice day-19
made for Service Bus consumers and day-21 made for the simulated DB cost: a real third-party API
would behave identically from the pipeline's point of view, but its actual uptime isn't something
this session can control on demand, and the whole point of this exercise is to reproducibly prove
the breaker opens and recovers - not to hope some public API happens to fail during the demo.

## 2. The resilience pipeline

`Extensions/InfrastructureExtensions.cs`:

```csharp
services.AddSingleton<FlakyDependencyState>();
services.AddSingleton<IResilienceMetrics, ResilienceMetrics>();
services.AddTransient<FlakyDependencyHandler>();

services
    .AddHttpClient<IQuoteEnrichmentClient, QuoteEnrichmentClient>(client =>
    {
        client.BaseAddress = new Uri("http://flaky-dependency.internal/"); // never resolved
    })
    .ConfigurePrimaryHttpMessageHandler<FlakyDependencyHandler>()
    .AddResilienceHandler("quote-enrichment-pipeline", (builder, context) =>
    {
        var metrics = context.ServiceProvider.GetRequiredService<IResilienceMetrics>();
        var logger = context.ServiceProvider.GetRequiredService<ILogger<IQuoteEnrichmentClient>>();
        var stateProvider = new CircuitBreakerStateProvider();
        metrics.AttachStateProvider(stateProvider);

        HttpMethod[] idempotentMethods = [HttpMethod.Get, HttpMethod.Head, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options];

        builder
            // BULKHEAD - outermost, so one logical call + its own retries occupies ONE slot,
            // not one per retry attempt.
            .AddConcurrencyLimiter(permitLimit: 5, queueLimit: 5)
            // RETRY - idempotent methods only, never retries a circuit that's already open.
            .AddRetry(new HttpRetryStrategyOptions
            {
                ShouldHandle = args =>
                {
                    if (args.Outcome.Exception is BrokenCircuitException or IsolatedCircuitException)
                        return ValueTask.FromResult(false);

                    var method = args.Outcome.Result?.RequestMessage?.Method;
                    var isIdempotent = method is null || idempotentMethods.Contains(method);
                    var isFailure = args.Outcome.Exception is not null
                        || (args.Outcome.Result is not null && !args.Outcome.Result.IsSuccessStatusCode);

                    return ValueTask.FromResult(isIdempotent && isFailure);
                },
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
                OnRetry = args => { metrics.RecordRetry(); logger.LogWarning("Retrying..."); return default; },
            })
            // CIRCUIT BREAKER - trips once a rolling 10s window sees >=8 attempts with a >=50%
            // failure ratio; short-circuits with BrokenCircuitException for 8s.
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 8,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(8),
                StateProvider = stateProvider,
                OnOpened = args => { metrics.RecordTransition(CircuitState.Open); logger.LogWarning("Circuit breaker OPENED for {BreakDuration}", args.BreakDuration); return default; },
                OnClosed = args => { metrics.RecordTransition(CircuitState.Closed); logger.LogInformation("Circuit breaker CLOSED - dependency recovered"); return default; },
                OnHalfOpened = args => { metrics.RecordTransition(CircuitState.HalfOpen); logger.LogInformation("Circuit breaker HALF-OPEN - trial call in flight"); return default; },
            })
            // TIMEOUT - bounds a single attempt, not the whole retry sequence.
            .AddTimeout(new HttpTimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(1),
                OnTimeout = args => { metrics.RecordTimeout(); logger.LogWarning("Attempt timed out after {Timeout}", args.Timeout); return default; },
            });
    });
```

**Order matters, and is deliberate.** Polly v8 executes strategies in the order they're added -
first added is outermost. Bulkhead first means one logical call (plus every retry it generates)
consumes exactly one concurrency slot, not one per attempt. Retry wraps circuit breaker so each
retry attempt re-enters (and is counted by) the breaker; circuit breaker wraps timeout so a
per-attempt timeout counts as a failure toward the breaker's stats. This is the same ordering
(rate limiter → retry → circuit breaker → attempt timeout) Microsoft's own `AddStandardResilienceHandler`
uses, minus its extra "total request timeout" layer, which this exercise's four named primitives
didn't call for.

**Retry is idempotent-only, made explicit, not assumed.** `IQuoteEnrichmentClient.EnrichAsync`
only ever issues a `GET`, so every retry here is already safe by construction - but the predicate
checks `RequestMessage.Method` against an explicit idempotent-methods list anyway, so it would
correctly refuse to retry a hypothetical future `POST`/`PATCH` call site on this same client rather
than silently retrying a non-idempotent write.

**Never retry an open circuit.** The predicate explicitly excludes `BrokenCircuitException` /
`IsolatedCircuitException` - retrying into a breaker that just told you "don't call this" would
undo the fail-fast benefit the breaker exists to provide.

The actual endpoint (`Extensions/ResilienceEndpointExtensions.cs`, `GET /api/resilience/enrich/{id}`)
classifies the outcome rather than returning a bare 500 for everything - `BrokenCircuitException` →
`"circuit-open"`, `RateLimiterRejectedException` → `"bulkhead-rejected"`, `TimeoutRejectedException`
→ `"timeout"`, an `HttpRequestException` after retries are exhausted → `"dependency-failed"` - so
both the demo UI and the transcripts below can show *which* layer of the pipeline handled each
failure, not just that something failed.

## 3. Proving each primitive independently

All four, run locally against `http://localhost:5312`, each reset (`POST /api/resilience/metrics/reset`)
before its own run so the numbers don't mix.

**Retry + circuit breaker**, dependency set to `AlwaysFail`, six sequential calls to
`GET /api/resilience/enrich/1`:

```
call 1 -> {"outcome":"dependency-failed", ...}     # 3 attempts (initial + 2 retries), all 503
call 2 -> {"outcome":"dependency-failed", ...}     # 3 more attempts, still below MinimumThroughput
call 3 -> {"outcome":"circuit-open", ...}           # breaker trips mid-retry-sequence (8th attempt)
call 4 -> {"outcome":"circuit-open", ...}
call 5 -> {"outcome":"circuit-open", ...}
call 6 -> {"outcome":"circuit-open", ...}

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":8,"dependencySuccesses":0,"dependencyFailures":8,"retries":6,
 "timeouts":0,"bulkheadRejections":0,"circuitRejections":4,"circuitState":"Open",
 "timeline":[{"at":"...T10:48:06...","state":1}]}
```

`dependencyAttempts` stops climbing (stays at 8) the instant the breaker opens - calls 4-6 never
reach `FlakyDependencyHandler` at all, proven by the counter, not just the outcome label.

**Timeout**, dependency set to `Slow` (2000ms latency, above the pipeline's 1s per-attempt timeout):

```
$ time curl -s http://localhost:5312/api/resilience/enrich/1
{"outcome":"timeout","detail":"The operation didn't complete within the allowed timeout of '00:00:01'."}
real 0m3.6s

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":3,"dependencyFailures":0,"retries":2,"timeouts":3,...}
```

3 attempts (initial + 2 retries), each individually timing out at 1s and being retried - a timeout
is treated as a transient, retriable failure, same as a 503.

**Bulkhead**, dependency `Healthy` with 800ms latency (long enough to hold a concurrency slot),
15 truly concurrent requests against a `permitLimit: 5, queueLimit: 5` pipeline (capacity 10):

```
$ for i in $(seq 1 15); do curl -s http://localhost:5312/api/resilience/enrich/1 & done; wait
      5 "outcome":"bulkhead-rejected"
     10 "outcome":"success"

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":10,"dependencySuccesses":10,"bulkheadRejections":5,...}
```

Exactly 10 succeed (5 running immediately + 5 queued, released as slots free up), exactly 5 are
rejected instantly without ever reaching the dependency - `dependencyAttempts` tops out at 10, not
15.

## 4. The breaker opening, then half-opening, to recovery

**Real application logs**, captured from the running process's own console output during one
fail → open → wait → heal → close cycle (four calls against `AlwaysFail`, a 9s wait, then one call
after switching back to `Healthy`):

```
info: Retrying quote enrichment (attempt 1) after ServiceUnavailable
info: Retrying quote enrichment (attempt 2) after ServiceUnavailable
info: Retrying quote enrichment (attempt 1) after ServiceUnavailable
info: Retrying quote enrichment (attempt 2) after ServiceUnavailable
info: Retrying quote enrichment (attempt 1) after ServiceUnavailable
warn: Circuit breaker OPENED for 00:00:08
info: Retrying quote enrichment (attempt 2) after ServiceUnavailable
info: Circuit breaker HALF-OPEN - trial call in flight
info: Circuit breaker CLOSED - dependency recovered
```

**Metrics timeline for the same cycle** (`GET /api/resilience/metrics`'s `timeline` field, backed
by the circuit breaker's own `OnOpened`/`OnHalfOpened`/`OnClosed` callbacks - not inferred, not
polled-and-guessed):

```json
"timeline": [
  {"at": "2026-09-04T16:34:57...", "state": 1},   // Open
  {"at": "2026-09-04T16:35:07...", "state": 2},   // HalfOpen (right at BreakDuration elapsing)
  {"at": "2026-09-04T16:35:07...", "state": 0}    // Closed (the trial call succeeded immediately)
]
```

**And in the Resilience tab itself** - "Run breaker demo" automates the exact same sequence
(configure `AlwaysFail` → fire calls until Open → confirm a rejected call never reaches the
dependency → wait out `BreakDuration` → configure `Healthy` → fire the half-open trial → confirm
`Closed`) and narrates each step live:

```
1. Configuring dependency to AlwaysFail...
2. Firing calls until the circuit opens (max 10)...
3. Circuit OPEN after 3 call(s). Confirming further calls fail fast...
4. Confirmed: that call never reached the dependency (dependencyAttempts unchanged) - a real fail-fast rejection.
5. Waiting 9s for BreakDuration (8s) to elapse...
6. Configuring dependency back to Healthy...
7. Firing the half-open trial call...
8. Circuit is now Closed after the trial call.
```

Screenshots: [screenshots/resilience-tab-breaker-timeline.png](screenshots/resilience-tab-breaker-timeline.png)
(live counters + the Open → HalfOpen → Closed timeline with real timestamps) and
[screenshots/resilience-tab-breaker-demo.png](screenshots/resilience-tab-breaker-demo.png) (the
call-by-call log plus the full narration). Verified with a headless-browser pass (Playwright) - zero
console errors, every button drives a real HTTP call against the running API, nothing staged client-side.

## What did I learn this session?

1. **Retries count toward the circuit breaker's own failure sample, which can trip it faster than
   "number of logical requests" suggests.** With `MaxRetryAttempts: 2` (3 attempts per logical
   call) and `MinimumThroughput: 8`, the breaker opened partway through the *third* logical
   request's own retry sequence, not after 8 separate calls from 8 different "users." This is
   correct Polly behavior (well-documented, not a bug I found), but it means the breaker's
   `MinimumThroughput` has to be reasoned about in terms of *attempts*, not requests, once retry
   sits inside it - tuning one without the other gives a breaker that trips much sooner (or later)
   than the numbers alone suggest.
2. **A custom primary `HttpMessageHandler` is a clean way to make a resilience pipeline testable
   on demand.** Wrapping a *real* third-party API would have meant either depending on it actually
   being flaky at the right moment (unreproducible) or mocking at the `IQuoteEnrichmentClient`
   level (which would skip the pipeline entirely - the thing actually being tested here).
   Replacing only the bottom-most transport, while keeping `HttpClient`/`HttpRequestMessage`/the
   whole `AddResilienceHandler` pipeline completely real, gave a demo that's both deterministic and
   architecturally honest.
3. **`GetOrCreateAsync`-style resilience (day 21) and Polly's `AddResilienceHandler` (day 22) solve
   adjacent but different problems, and conflating them would be a mistake.** HybridCache's
   single-flight coalescing protects *one key* from concurrent duplicate work; Polly's bulkhead
   here protects the *dependency as a whole* from too much concurrent traffic regardless of what
   each call is asking for. A cache stampede and a bulkhead-worthy traffic spike look similar from
   the outside (many concurrent callers) but need different mechanisms.

## What would break this

- **`FlakyDependencyState` is a single in-process singleton.** If `QuotesApi` ever scales to
  multiple instances, each instance has its own dependency-mode toggle and its own circuit breaker
  state - configuring `AlwaysFail` from the demo UI only affects whichever instance happened to
  serve that request, and each instance's breaker trips independently based on only the traffic
  *it* saw. A real deployment fronting multiple instances of the same downstream dependency would
  need this reasoned about per-instance, same as day-21's "single-process view of stampede
  protection" caveat.
- **The circuit breaker's `MinimumThroughput`/`SamplingDuration`/`BreakDuration` (8 attempts / 10s
  / 8s) are tuned for a fast, legible demo, not derived from any real dependency's actual failure
  characteristics.** A production system would set these from the downstream service's real
  latency/error budget, not from "what looks good in a 10-second screen recording."
  `MaxRetryAttempts: 2` with a 200ms exponential base is similarly a demo-speed choice - worth
  revisiting against the real cost of a retry storm on an already-struggling dependency.
- **No jitter or cap on the circuit breaker's `BreakDuration` itself** (only the retry delay uses
  jitter). If many client processes all observed the same dependency fail at the same instant (a
  real outage, not this demo's controlled scenario), they'd all open their own breakers and all
  attempt their half-open trial at roughly the same moment `BreakDuration` later - a synchronized
  "thundering herd of trial calls" hitting a downstream service the instant it's expected to be
  recovering. A jittered break duration is the standard fix, not implemented here.
- **The bulkhead's `queueLimit: 5` means a burst beyond capacity waits rather than always failing
  fast.** Demonstrated as "10 succeed, 5 rejected" (section 3), but the 5 queued callers experience
  real added latency (however long the queue takes to drain) rather than an immediate answer either
  way - a caller with its own tight deadline further upstream could still time out waiting on a
  bulkhead slot that was technically "not rejected."
- **Not deployed live.** Everything here is proven against `localhost` - see "Current status" for
  why (day-21's live-deployment session surfaced a real config bug and an unusable Managed Redis
  instance; this session deliberately didn't take on a second live-infrastructure risk in the same
  sitting). The resilience pipeline's own code has no Azure-specific dependency and should behave
  identically once deployed, but that's an inference, not something this session verified live.

## Notes for mentor

- `day-21/piece1` (and everything upstream of it) was read-only reference / copy source - nothing
  there was modified. Any file needed from it was copied into `day-22/piece1` first.
- **Not deployed live this session** - see "Current status" and "What would break this" above for
  the reasoning. Fully verified locally: backend logs, the metrics/timeline API, the frontend tab,
  and a headless-browser (Playwright) pass, all showing a real Closed → Open → HalfOpen → Closed
  cycle end to end.
- Full command transcripts (all four primitives proven independently, the full breaker cycle's
  logs, and the Playwright verification pass) are in [verification-log.md](verification-log.md).

## GitHub link

Not pushed yet - link to follow once pushed to the `thinkbridge-thinkschool` org, per this user's
standing preference that git actions (including read-only ones) need explicit permission each time.
