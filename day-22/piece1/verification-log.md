# Verification log — Day 22 / Piece 1

## 1. Setup

Copied `day-21/piece1/QuotesApi` and `day-21/piece1/quotes-list-detail` into `day-22/piece1`
unmodified (robocopy, excluding `bin`/`obj`/`publish`/`node_modules`/`.angular`/`dist`/`*.db*` -
all build artifacts, regenerated fresh). Added `Microsoft.Extensions.Http.Resilience` 10.9.0
(brings in `Polly.Core`, `Polly.Extensions`, `Polly.RateLimiting` transitively):

```
$ dotnet add package Microsoft.Extensions.Http.Resilience --version 10.9.0
info : PackageReference for package 'Microsoft.Extensions.Http.Resilience' version '10.9.0' added
```

New backend files: `Resilience/FlakyDependencyState.cs`, `Resilience/FlakyDependencyHandler.cs`,
`Resilience/IQuoteEnrichmentClient.cs`, `Resilience/IResilienceMetrics.cs`,
`Resilience/ResilienceMetrics.cs`, `Extensions/ResilienceEndpointExtensions.cs`. Wired in
`InfrastructureExtensions.cs` and `Program.cs`. Clean build on the first real attempt after fixing
one thing caught immediately by the compiler, not by running anything:

```
$ dotnet build
Build succeeded.
    0 Error(s)
```

(The one compile-time catch: `HttpContent.ReadFromJsonAsync` needs an explicit
`using System.Net.Http.Json;` - not in this SDK's implicit-usings set, unlike `System.Net.Http`
itself.)

## 2. Each primitive, proven independently

API run locally on `http://localhost:5312` (fresh SQLite `quotes.db`, same as every prior day's
local run). All four below reset counters first (`POST /api/resilience/metrics/reset`) so each
run's numbers stand on their own.

**Healthy baseline** (sanity check before touching failure modes):

```
$ curl -s http://localhost:5312/api/resilience/dependency
{"mode":"Healthy","latencyMs":30,"failureRatePercent":100}
$ curl -s http://localhost:5312/api/resilience/enrich/1
{"outcome":"success","enrichment":{"quoteId":1,"enrichment":"sentiment: reflective, era: unknown"},"detail":null}
$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":1,"dependencySuccesses":1,"dependencyFailures":0,"retries":0,"timeouts":0,
 "bulkheadRejections":0,"circuitRejections":0,"circuitState":"Closed","timeline":[]}
```

(Enum note: `FlakyDependencyMode` initially serialized as a bare int - `"mode":0` - fixed by adding
`[JsonConverter(typeof(JsonStringEnumConverter<FlakyDependencyMode>))]`, scoped to just this enum
rather than a global JSON-options change that could've affected `JobStatus` elsewhere unexpectedly.)

**Retry + circuit breaker** - dependency set to `AlwaysFail`, six sequential calls:

```
$ curl -s -X POST http://localhost:5312/api/resilience/dependency/configure -H "Content-Type: application/json" -d '{"mode":"AlwaysFail"}'
{"mode":"AlwaysFail","latencyMs":30,"failureRatePercent":100}
$ curl -s -X POST http://localhost:5312/api/resilience/metrics/reset

$ for i in $(seq 1 6); do curl -s http://localhost:5312/api/resilience/enrich/1; echo; done
{"outcome":"dependency-failed","detail":"Response status code does not indicate success: 503 (Service Unavailable)."}
{"outcome":"dependency-failed","detail":"Response status code does not indicate success: 503 (Service Unavailable)."}
{"outcome":"circuit-open","detail":"The circuit is now open and is not allowing calls."}
{"outcome":"circuit-open","detail":"The circuit is now open and is not allowing calls."}
{"outcome":"circuit-open","detail":"The circuit is now open and is not allowing calls."}
{"outcome":"circuit-open","detail":"The circuit is now open and is not allowing calls."}

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":8,"dependencySuccesses":0,"dependencyFailures":8,"retries":6,"timeouts":0,
 "bulkheadRejections":0,"circuitRejections":4,"circuitState":"Open",
 "timeline":[{"at":"2026-09-04T10:48:06.1066719+00:00","state":1}]}
```

`dependencyAttempts` stays at 8 through calls 4-6 - the counter itself (not just the outcome label)
proves those calls never reached `FlakyDependencyHandler`.

**Recovery** - waited out `BreakDuration` (8s), configured `Healthy`, fired the trial call plus one
more to confirm fully closed:

```
$ sleep 9
$ curl -s -X POST http://localhost:5312/api/resilience/dependency/configure -H "Content-Type: application/json" -d '{"mode":"Healthy"}'
$ curl -s http://localhost:5312/api/resilience/enrich/1
{"outcome":"success",...}
$ curl -s http://localhost:5312/api/resilience/enrich/1
{"outcome":"success",...}

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":10,"dependencySuccesses":2,"dependencyFailures":8,"retries":6,"timeouts":0,
 "bulkheadRejections":0,"circuitRejections":4,"circuitState":"Closed",
 "timeline":[{"at":"...10:48:06...","state":1},{"at":"...10:48:36.798...","state":2},{"at":"...10:48:36.838...","state":0}]}
```

Full lifecycle in one timeline: Open(1) → HalfOpen(2) → Closed(0), ~30s apart for the first
transition (8s `BreakDuration` + the time between test steps), HalfOpen → Closed within 40ms of
each other (expected: Polly transitions to HalfOpen just before the trial call and to Closed
immediately after it succeeds - both part of handling that one call).

**Timeout** - dependency set to `Slow` (2000ms latency, above the pipeline's 1s per-attempt
timeout):

```
$ curl -s -X POST http://localhost:5312/api/resilience/metrics/reset
$ curl -s -X POST http://localhost:5312/api/resilience/dependency/configure -H "Content-Type: application/json" -d '{"mode":"Slow","latencyMs":2000}'
{"mode":"Slow","latencyMs":2000,"failureRatePercent":100}

$ time curl -s http://localhost:5312/api/resilience/enrich/1
{"outcome":"timeout","detail":"The operation didn't complete within the allowed timeout of '00:00:01'."}
real    0m3.619s

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":3,"dependencySuccesses":0,"dependencyFailures":0,"retries":2,"timeouts":3,
 "bulkheadRejections":0,"circuitRejections":0,"circuitState":"Closed","timeline":[]}
```

3 attempts (initial + 2 retries), all 3 timing out (not failing outright - `dependencyFailures: 0`,
`timeouts: 3`), each retried since a timeout counts as a transient failure under the retry
predicate. `circuitState` stays `Closed` - 3 attempts is below `MinimumThroughput: 8`.

**Bulkhead** - dependency `Healthy`, 800ms latency, 15 truly concurrent requests
(`permitLimit: 5, queueLimit: 5` ⇒ total capacity 10):

```
$ curl -s -X POST http://localhost:5312/api/resilience/metrics/reset
$ curl -s -X POST http://localhost:5312/api/resilience/dependency/configure -H "Content-Type: application/json" -d '{"mode":"Healthy","latencyMs":800}'

$ for i in $(seq 1 15); do curl -s http://localhost:5312/api/resilience/enrich/1 -o "/tmp/enrich_$i.json" & done; wait
$ grep -oh '"outcome":"[a-z-]*"' /tmp/enrich_*.json | sort | uniq -c
      5 "outcome":"bulkhead-rejected"
     10 "outcome":"success"

$ curl -s http://localhost:5312/api/resilience/metrics
{"dependencyAttempts":10,"dependencySuccesses":10,"dependencyFailures":0,"retries":0,"timeouts":0,
 "bulkheadRejections":5,"circuitRejections":0,"circuitState":"Closed"}
```

Exactly 10 reach the dependency (5 immediately + 5 queued), exactly 5 rejected instantly - matches
`permitLimit + queueLimit` precisely.

## 3. Real application logs for the full breaker cycle

Captured by tailing the running `dotnet run` process's own console output (not the metrics API)
while repeating the fail → open → wait → heal → close sequence:

```
$ tail -f -n0 <dotnet console log> > /tmp/breaker-log-capture.txt &
$ curl -s -X POST .../dependency/configure -d '{"mode":"AlwaysFail","latencyMs":30}'
$ for i in $(seq 1 4); do curl -s .../enrich/1 > /dev/null; done
$ sleep 9
$ curl -s -X POST .../dependency/configure -d '{"mode":"Healthy","latencyMs":30}'
$ curl -s .../enrich/1 > /dev/null

$ grep -E "Retrying|Circuit breaker" /tmp/breaker-log-capture.txt
      Retrying quote enrichment (attempt 1) after ServiceUnavailable
      Retrying quote enrichment (attempt 2) after ServiceUnavailable
      Retrying quote enrichment (attempt 1) after ServiceUnavailable
      Retrying quote enrichment (attempt 2) after ServiceUnavailable
      Retrying quote enrichment (attempt 1) after ServiceUnavailable
      Circuit breaker OPENED for 00:00:08
      Retrying quote enrichment (attempt 2) after ServiceUnavailable
      Circuit breaker HALF-OPEN - trial call in flight
      Circuit breaker CLOSED - dependency recovered
```

(The last "Retrying...attempt 2" line appearing just after "OPENED" is expected, not a bug: that
retry belongs to a call already in flight when the breaker tripped, logged by the outer retry
strategy slightly after the inner circuit breaker's own `OnOpened` callback fired for a *different*
call's attempt within the same window.)

## 4. Frontend

New files: `models/resilience.model.ts`, `resilience.service.ts`,
`resilience-view/resilience-view.{ts,html,css}`. Wired into `app.ts`/`app.html` as a new
**Resilience** tab, same pattern every prior day's tab used. `proxy.conf.json` updated to point at
this day's own API port (`5312`, was `5310` inherited from day-21's copy - a local dev-only file,
not a secret, not something the "don't modify the copy source" rule applies to since it's part of
`day-22/piece1`'s own tree).

```
$ ng build
Application bundle generation complete. [9.401 seconds]
```

## 5. Headless-browser (Playwright) verification

`ng serve --port 4222 --proxy-config proxy.conf.json` against the API on `5312`. Two passes:

**First pass** (`check-resilience-tab.js`, scratchpad, not part of the repo): full-page
screenshots after each step - fire a batch of healthy calls, then run the full breaker demo.
Console errors: none. The full-page screenshots showed a duplicated header artifact - a known
Playwright quirk where a `position: sticky` element (the app's own `.header-bar`, inherited
unmodified from day-17) gets re-rendered at each tile boundary during a stitched `fullPage`
capture. Not a real rendering bug - confirmed by retaking with a fixed, sufficiently-tall viewport
and no `fullPage` stitching:

**Second pass** (`check-resilience-tab2.js`): 1280×1400 viewport, no full-page stitching. Ran the
breaker demo, screenshotted the counters/timeline panel and the narration panel. Zero console
errors both passes. Narration text captured from the live DOM, matching section 3's curl proof
exactly:

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

Screenshots copied into [screenshots/](screenshots/): `resilience-tab-initial.png`,
`resilience-tab-breaker-timeline.png`, `resilience-tab-breaker-demo.png`.

## 6. Cleanup

- Local `dotnet run` and `ng serve` processes for this day's API (port 5312) and frontend (port
  4222) left running for the user's own continued testing, same as every prior local-verification
  day.
- No Azure resources touched or created this session - see README.md "Current status" for why this
  day wasn't deployed live.
- Scratchpad Playwright scripts (`check-resilience-tab.js`, `check-resilience-tab2.js`) and their
  intermediate screenshots live only in the session scratchpad, not the repo - only the final,
  clean screenshots were copied into `screenshots/`.
