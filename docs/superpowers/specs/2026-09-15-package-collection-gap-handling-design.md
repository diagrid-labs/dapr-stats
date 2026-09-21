# Package collection gap handling — design

Date: 2026-09-15

## Problem

The [package collection retry design](2026-07-27-package-collection-retry-design.md)
opened with this:

> The run reports `COMPLETED` while rows are silently missing from Postgres.

It fixed detection and re-collection, but reintroduced the same silent
completion one step further out. Both exits from the retry loop are the same
statement:

```csharp
if (pending.Length == 0 || attempt == MaxAttempts)
{
    return;
}
```

"Everything stored" and "attempts exhausted, packages still missing" are
indistinguishable. `pending` is discarded, `RunAsync` returns `true`, the
workflow reaches `COMPLETED`, and `Evaluate Result` — which tests only
`runtimeStatus` — passes the job green.

This happened on 2026-09-15. Run
[34860872995](https://github.com/diagrid-labs/dapr-stats/actions/runs/34860872995)
reported success while `python_dapr` was missing `dapr` and `dapr-agents` for
ISO week `2026-09-14`. The retry loop worked correctly — `diagrid` was rescued
on the third attempt at 11:59:32 — it simply had no way to say that two packages
were never rescued.

It was not the first time. The same two-of-four pattern hit the 2026-09-08 run,
where `dapr-agents` and `diagrid` kept their 2026-09-07 rows. Neither week was
noticed, because nothing reports it.

The cause is also unknown, and that is its own defect. `Get*PackageData` returns
`false` for any non-success status without recording which one, and the only
diagnostic — the `Console.WriteLine` in the Check activities — goes to the
backgrounded `dapr run` stdout, which CI does not capture.

## Goal

1. Report which packages a run failed to collect, instead of discarding the list.
2. Fail the build when a gap survives a second, later attempt — but not on a
   single transient upstream hiccup.
3. Reduce how often the retries are exhausted, without lengthening the job.
4. Make the next gap diagnosable from the Actions log alone.

## Non-goals

- Retry or gap handling for any non-package collector (Docker Hub, Discord,
  Diagrid, Datadog, Scarf, GitHub). They have not shown this failure.
- Honouring `Retry-After` on 429. Considered and dropped: it replaces an
  approximate backoff with a correct one, but adds a control path that cannot be
  tested without a live 429. Revisit if the exponential schedule proves
  insufficient.
- Replacing pypistats.org with another download-stats source.
- Fixing the two limitations inherited from the 2026-07-27 design (midnight-UTC
  collection-date mismatch, NuGet name drift). The second one interacts with
  this work; see Known limitations.

## Architecture

Three changes that compose:

```
 CollectorWorkflow                       run-workflow.yaml
 -----------------                       -----------------
 loops return still-missing  ---------->  poll step reads MissingPackages
 RunAsync aggregates,                     from the workflow output
 finishes every other
 collector, returns                       Evaluate Result branches:
 CollectorWorkflowResult                    main run     -> warn, exit 0
                                            gap-fill run -> exit 1
 GapFillOnly seeds `pending`
 from the DB before the      <----------  second cron, 2h later,
 first pass                                GapFillOnly: true
```

The workflow **reports**; CI **decides**. `COMPLETED` keeps meaning "the run
finished", the missing list survives in structured form, and the fail-vs-warn
policy — which differs between the two runs — lives in the one place that knows
which run it is.

Throwing from the workflow was rejected: the package loops are awaited before
Docker Hub, Datadog, Scarf and GitHub, so an exception there would abandon every
remaining collector over two packages.

## Workflow changes

### Loops return what they could not collect

`CollectDaprStats/CollectorWorkflow.cs`. The three helpers change from `Task` to
`Task<string[]>`, returning the still-unresolved package names:

| Exit | Returns |
|---|---|
| `pending.Length == 0` | `[]` |
| `attempt == MaxAttempts` with packages outstanding | `pending` + terminal names |
| `input.SkipStorage` | `[]` — nothing was stored, so nothing is verifiable |

`RunAsync` holds the three results and **carries on** with every remaining
collector, then returns them:

```csharp
public record CollectorWorkflowResult(string[] MissingPackages);
```

`CollectorWorkflow` becomes
`Workflow<CollectorWorkflowInput, CollectorWorkflowResult>`. Names are qualified
by ecosystem (`nuget:Dapr.Jobs`, `pypi:dapr-agents`) so the CI message is
unambiguous. Nothing else consumes the old `bool`.

### Retry budget: 5 attempts, exponential backoff

```csharp
private const int MaxAttempts = 5;

// One entry per gap between attempts; asserted in tests.
private static readonly TimeSpan[] Backoffs =
[
    TimeSpan.FromSeconds(30),
    TimeSpan.FromMinutes(1),
    TimeSpan.FromMinutes(2),
    TimeSpan.FromMinutes(4),
];
```

Indexed by `attempt - 1`, replacing the flat `RateLimitBackoff`.

Budget, with the per-pass cost measured from run 34860872995 (13m58s total;
NuGet's 9-package loop is the long pole at ~1.5 min/pass). The table is
**worst case** — every attempt used — so the current row exceeds that run's
actual time, which exhausted only the Python loop:

| | Passes | Sleeps | Package phase | Whole job |
|---|---|---|---|---|
| Current (3 × flat 5 min) | 4.5 min | 10 min | ~14.5 min | ~17.5 min |
| **This design (5 × exponential)** | 7.5 min | 7.5 min | **~15 min** | **~18 min** |
| Rejected (4 × flat 5 min) | 6 min | 15 min | ~21 min | ~24 min |

Two extra attempts for half a minute of wall clock, and the front-loaded 30s and
1m retries catch a brief 429 far sooner than the current 5-minute wait.

**Neither `timeout-minutes: 30` nor the 25-minute poll deadline changes.** The
binding constraint was never the runner's capacity — GitHub-hosted jobs may run
for 6 hours — but these two self-imposed numbers, and the design stays inside
both with roughly 7 minutes to spare.

### Terminal failures are not retried

`Get{NuGet,Npm,Python}PackageData` return an outcome instead of `bool`:

```csharp
public enum PackageFetchOutcome { Stored, Transient, Terminal }
```

Classified from the response status by a pure helper:

| Status | Outcome | Reasoning |
|---|---|---|
| 2xx | `Stored` | |
| 429 | `Transient` | rate limited; retrying is the whole point |
| 5xx | `Transient` | upstream trouble, usually brief |
| anything else | `Terminal` | a 404 on a renamed package will never succeed |

403 is classified `Terminal`. Some APIs throttle with 403 rather than 429; if
that is observed in practice, move it. The `[packages]` log line makes the call
visible either way.

The loop keeps a terminal list and subtracts it from whatever the checker
reports, so one dead package cannot starve four live ones of their retries.
Terminal names are still reported in `MissingPackages` — a 404 is exactly what
you want to hear about.

**Replay hazard:** this code runs under Dapr workflow replay, so the loop uses
order-stable collections (`List<string>`, ordered concat) and never `HashSet`
iteration order.

### GapFill mode

`CollectorWorkflowInput` gains `bool GapFillOnly`. When set, each package loop
calls its existing `Check*PackageData` activity **before** the first collection
pass, seeding `pending` from the database, and returns `[]` immediately if
nothing is missing.

Everything else is skipped through the existing mechanism — the gap-fill job
passes empty lists and `false` flags, exactly as `collect_scarf: none` already
works. No new skip path is invented.

Gap-fill always targets the current ISO week. A cron delayed past a week
boundary simply collects the new week early; the upsert means Monday's run
overwrites it.

`local-tests.http` gains a gap-fill request alongside the existing ones, per the
repo convention that every input field is drivable by hand.

## CI changes

`.github/workflows/run-workflow.yaml`.

### Second schedule

```yaml
on:
  schedule:
    - cron: '43 8 * * 1'    # main collection
    - cron: '43 10 * * 1'   # gap-fill, two hours later
```

Two hours, not six. The offset only needs to clear a rate-limit window, and a
shorter one surfaces a real failure the same morning. It deliberately does *not*
try to out-wait GitHub's scheduling delay — measured on this workflow at two
hours, nineteen hours, and once eight days — because no offset can. Ordering is
handled by the total-gap rule below instead.

`GapFillOnly` is derived from `github.event.schedule`, so a delayed main run
cannot set it.

### Evaluate Result branches on how much is missing

The poll step reads the workflow output alongside the status it already reads:

```bash
BODY=$(curl -s "http://localhost:3500/v1.0/workflows/dapr/$INSTANCE_ID")
STATUS=$(echo "$BODY" | jq -r '.runtimeStatus')
MISSING=$(echo "$BODY" | jq -r '.properties["dapr.workflow.output"]' \
          | jq -r '.MissingPackages // [] | join(", ")')
```

> **Must be confirmed before building on it.** Two things are unverified on this
> Dapr version: the property name carrying workflow output, and whether the
> record serializes as `MissingPackages` or `missingPackages`. Verify both with
> a manual run via `local-tests.http` (line 54 already performs the status GET).
> If the output is not exposed there, fall back to parsing the `[packages]`
> lines out of the captured app log.

`Evaluate Result` then applies:

| Gap-fill finds | Means | Action |
|---|---|---|
| nothing missing | main run was complete | exit 0 |
| **every** requested package missing | main run has not landed yet | collect, warn, **exit 0** |
| **some** missing | main run ran and dropped some | collect, **exit 1** if any remain |

A total gap can only mean the main run has not happened; a partial gap is the
failure worth an email. This makes the two runs order-independent, which is what
lets the offset be short.

The main run never fails on missing packages. It writes them to
`$GITHUB_STEP_SUMMARY`, emits a `::warning::` annotation, and exits 0.

### Diagnostics without a PII hazard

The app currently starts as `dapr run -f . &`, discarding stdout. It becomes:

```yaml
dapr run -f . > dapr-run.log 2>&1 &
```

with an `if: always()` step that prints **only lines matching the `[packages]`
prefix**:

```yaml
- name: Package collector log
  if: always()
  run: grep -F '[packages]' dapr-run.log || echo "(no package collector output)"
```

**This repository is public, so its Actions logs are world-readable.** The
identified-user collectors are written to be counts-only — see the explicit
`// Counts only -- never an id, email or name.` comments at
`GetDataDogRumIdentifiedUsers.cs:146` and `:241` — so a blanket dump would be
safe today. It would also leave every future `Console.WriteLine` one review
mistake away from leaking a customer email into a public log. The allowlist grep
removes that standing hazard: new console output cannot reach the Actions log
unless someone deliberately prefixes it.

`Get*PackageData` log `[packages] pypi dapr-agents: HTTP 429` on non-success —
ecosystem, package name and status only, never response bodies.

`dapr-run.log` is not uploaded as an artifact, and `scratch/` and `.dapr/logs/`
stay out of CI entirely.

## Testing

`RunAsync` has no test coverage today and this design does not add any — the
workflow class stays orchestration-only. The new decision logic is extracted
into pure helpers, matching how `IsoWeek` and `PackageDataChecker` are already
factored, and those are unit tested in the existing suite:

| Helper | Cases |
|---|---|
| Backoff schedule | delay for attempts 1–4; `Backoffs.Length == MaxAttempts - 1`; total sleep plus worst-case passes stays inside the 25-minute poll deadline |
| Status classifier | 200 → `Stored`; 429, 500, 503 → `Transient`; 404, 400, 403 → `Terminal` |
| Pending/terminal merge | checker result minus terminal names; terminal names still surface in the reported missing set; order stability across identical inputs |
| Missing-set qualification | ecosystem prefixes; an empty set yields `[]`, not `[""]` |

The budget test is the one that earns its keep: it fails if someone raises
`MaxAttempts` or stretches a backoff past what the job can absorb, which is the
exact mistake this design is one edit away from.

Manual verification before merge:

1. Confirm the workflow-output property name (above).
2. Run with `SkipStorage: true` — `MissingPackages` must be empty, and no loop
   may sleep.
3. Run `GapFillOnly: true` against a complete week — every loop returns
   immediately, the job is green, nothing is written.
4. Run `GapFillOnly: true` against a week with one package's row deleted — that
   package alone is re-collected.

## Known limitations

**NuGet name drift now fails the build.** Carried over from the 2026-07-27
design: `GetNuGetPackageData` calls `SearchAsync(..., take: 1)` and stores the
top hit's `Identity.Id`, which is not guaranteed to equal the requested name. If
a search ever returns a different ID, the fetch *succeeds* — so the status
classifier cannot mark it terminal — while the checker reports that name missing
on every pass, in every run, indefinitely. Under this design that becomes a red
build every Monday rather than a silent gap. That is an improvement in
visibility and a regression in noise, and the fix belongs in
`GetNuGetPackageData`, not here. The `[packages]` log makes it identifiable when
it happens.

**A total upstream failure reads as "main run has not landed".** If all 14
packages genuinely fail in the main run, gap-fill sees a total gap and exits 0
under the rule above. The `::warning::` annotation still fires, so it is not
invisible, but it is not red either. Simultaneous failure across nuget.org,
registry.npmjs.org and pypistats.org is unlikely enough to accept.

**Midnight-UTC collection-date mismatch.** Unchanged from the 2026-07-27 design.
The two crons fire at 08:43 and 10:43 UTC, so neither can straddle midnight.

**Gap-fill races a very late main run.** If the main run lands between
gap-fill's seeding check and its collection pass, both write the same week. The
upsert makes that harmless — the worst case is a redundant fetch.
