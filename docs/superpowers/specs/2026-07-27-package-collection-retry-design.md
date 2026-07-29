# Package collection retry — design

Date: 2026-07-27

## Problem

`CollectorWorkflow` collects package download stats from PyPI, npm, and NuGet by
calling one activity per package. When an upstream API returns 429, the activity
returns `false` (Python, npm) or throws (NuGet), and the workflow ignores the
result. The run reports `COMPLETED` while rows are silently missing from
Postgres, so the first attempt of a scheduled collection frequently produces
incomplete data.

Nothing in the current design detects the gap or retries.

## Goal

After collecting an ecosystem's packages, verify against Postgres that every
requested package actually has a row for the current collection date. Re-collect
only the missing packages, with a 5-minute pause to let rate limits reset, for at
most three passes.

## Non-goals

- Retrying anything other than the three package ecosystems (DockerHub, Discord,
  Diagrid, GitHub are unchanged).
- Fixing `GetNuGetPackageData`'s `SearchAsync(take: 1)` behaviour (see
  Known limitations).
- Aligning the date that `Get*PackageData` inserts with `input.CollectionDate`
  (see Known limitations).

## Architecture

Three independent `Get → Check → Sleep` loops, one per ecosystem, started
together and awaited with `Task.WhenAll`. PyPI, npm, and NuGet are separate hosts
with independent rate limits, so serializing the loops buys nothing while
tripling the worst-case wall clock.

```
RunAsync
  ├─ CollectNuGetPackagesAsync  ─┐
  ├─ CollectNpmPackagesAsync    ─┼─ Task.WhenAll
  ├─ CollectPythonPackagesAsync ─┘
  ├─ DockerHub / Discord / Diagrid  (unchanged)
  └─ GitHub child workflow          (unchanged)
```

Each loop body:

1. For each pending package: call `Get*PackageData`, then a short timer (5s for
   NuGet and npm, 10s for Python — matching the existing spacing).
2. Call `Check*PackageData`, which reads the ecosystem's
   `*_latest_packages_view` and returns the requested package names that have no
   row for the collection date.
3. If nothing is missing, or this was the final pass, return.
4. Otherwise sleep 5 minutes and repeat with only the missing names.

## Components

### `PostgresOutput.ReadAsync`

New method on the existing class in `CollectDaprStats/PostgresOuput.cs`.

The Dapr PostgreSQL binding's `query` operation returns rows as positional JSON
arrays, not named objects:

```json
[["dapr","2026-07-27T00:00:00Z"],["dapr-agents","2026-07-27T00:00:00Z"]]
```

`InvokeBindingAsync<T>` cannot bind that to a record with named properties, so
the method uses the raw `BindingRequest`/`BindingResponse` pair and maps by
position.

```csharp
public record PackageCollectionRecord(string PackageName, DateOnly CollectionDate);

public async Task<PackageCollectionRecord[]> ReadAsync(string sqlText)
{
    var request = new BindingRequest(BindingName, QueryOperation);
    request.Metadata.Add("sql", sqlText);

    var response = await _daprClient.InvokeBindingAsync(request);
    return ParseRows(response.Data.Span);
}

internal static PackageCollectionRecord[] ParseRows(ReadOnlySpan<byte> json)
{
    var rows = JsonSerializer.Deserialize<JsonElement[][]>(json) ?? [];

    return rows.Select(row => new PackageCollectionRecord(
        row[0].GetString()!,
        ParseDate(row[1]))).ToArray();
}
```

Row mapping sits in `ParseRows`, separate from the binding call, so it is
testable without a `DaprClient`.

`ParseDate` parses with `DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal`
before taking `DateOnly`. Parsing `"…T00:00:00Z"` as local time would shift the
date by a day for any host west of UTC and make every package read as missing.

`BindingName` (`"daprstats"`), `ExecOperation` (`"exec"`), and `QueryOperation`
(`"query"`) become consts shared with `InsertAsync`.

No parameterized overload — the three views take no parameters.

### `PackageDataChecker`

New DI-registered singleton in `CollectDaprStats/PackageDataChecker.cs`. The
three Check activities differ only by view name, so the comparison lives here
rather than being triplicated.

```csharp
public async Task<string[]> FindMissingAsync(
    string viewName, IEnumerable<string> expectedPackageNames, DateOnly collectionDate)
{
    var rows = await _output.ReadAsync($"select package_name, collection_date from {viewName}");

    var stored = rows.Where(r => r.CollectionDate == collectionDate)
                     .Select(r => r.PackageName)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return expectedPackageNames.Where(n => !stored.Contains(n)).ToArray();
}
```

The comparison itself is a pure static method (`FindMissing`) that
`FindMissingAsync` calls with the rows it read, so it is testable without a
`DaprClient`. `PackageDataChecker` also owns `ToCollectionDate`, the
`DateTime` → `DateOnly` normalization the three activities share.

The date filter is load-bearing. Each view selects rows at
`MAX(collection_date)`, so a run that stored nothing at all returns the
*previous* run's rows. Filtering on the collection date makes that case
correctly read as "all missing".

`OrdinalIgnoreCase` absorbs casing drift between requested and stored names.

### Check activities

One file each, matching the existing one-activity-per-file convention. Each is
`WorkflowActivity<TInput, string[]>` returning the missing package names, and
logs them before returning.

| File | View | Input record |
|---|---|---|
| `CheckPythonPackageData.cs` | `python_dapr_latest_packages_view` | `CheckPythonPackageDataInput(PythonPackageInput[] Packages, DateTime CollectionDate)` |
| `CheckNuGetPackageData.cs` | `nuget_dapr_client_latest_packages_view` | `CheckNuGetPackageDataInput(NuGetPackageInput[] Packages, DateTime CollectionDate)` |
| `CheckNpmPackageData.cs` | `npm_dapr_dapr_latest_packages_view` | `CheckNpmPackageDataInput(NpmPackageInput[] Packages, DateTime CollectionDate)` |

Each activity normalizes `CollectionDate` to UTC before taking `DateOnly`, then
delegates to `PackageDataChecker.FindMissingAsync` with its view name and the
package names from its input.

The return type is a bare `string[]` rather than a wrapper record — there is
nothing else to carry.

### `Program.cs`

Register the three activities and `PackageDataChecker` as a singleton alongside
the existing registrations.

## Workflow changes

`CollectorWorkflow.cs`:

```csharp
private const int MaxAttempts = 3;
private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);
```

The three sequential package blocks in `RunAsync` are replaced by:

```csharp
var packageLoops = new List<Task>();
if (input.NuGetPackageNames.Length > 0)  packageLoops.Add(CollectNuGetPackagesAsync(context, input));
if (input.NpmPackageNames.Length > 0)    packageLoops.Add(CollectNpmPackagesAsync(context, input));
if (input.PythonPackageNames.Length > 0) packageLoops.Add(CollectPythonPackagesAsync(context, input));
if (packageLoops.Count > 0) await Task.WhenAll(packageLoops);
```

Each helper follows this shape (Python shown; npm and NuGet differ only in
activity names, input record, and timer duration):

```csharp
private static async Task CollectPythonPackagesAsync(WorkflowContext context, CollectorWorkflowInput input)
{
    var pending = input.PythonPackageNames;

    for (var attempt = 1; attempt <= MaxAttempts; attempt++)
    {
        foreach (var packageName in pending)
        {
            await context.CallActivityAsync(
                nameof(GetPythonPackageData),
                new PythonPackageInput(packageName, input.SkipStorage));
            // Wait to prevent 429 error
            await context.CreateTimer(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        if (input.SkipStorage) return;   // nothing stored, so nothing to verify

        pending = await context.CallActivityAsync<string[]>(
            nameof(CheckPythonPackageData),
            new CheckPythonPackageDataInput(
                pending.Select(p => new PythonPackageInput(p, input.SkipStorage)).ToArray(),
                input.CollectionDate));

        if (pending.Length == 0 || attempt == MaxAttempts) return;

        await context.CreateTimer(RateLimitBackoff, CancellationToken.None);
    }
}
```

Decisions encoded above:

- **`MaxAttempts = 3` means 3 iterations of the loop body** — the initial pass
  plus up to 2 retries, so at most 2 backoff sleeps.
- The `attempt == MaxAttempts` return happens *before* sleeping, so a failed
  final check does not waste 5 minutes.
- `SkipStorage` short-circuits the check. Nothing was written, so every package
  would read as missing and the loop would sleep twice for nothing.
- Every await is on `context`, so the `Task.WhenAll` fan-out replays
  deterministically.

Worst case per ecosystem is roughly 10 minutes of backoff plus per-package
spacing. Because the loops run in parallel, that is also the worst case overall:
around 12 minutes for the package phase.

## CI changes

`.github/workflows/run-workflow.yaml`:

- `timeout-minutes: 10` → `30`.
- The "Wait for Workflow Completion" (`sleep 180`) and "Check Workflow Status"
  steps collapse into one polling step. "Evaluate Result" is unchanged.

```yaml
      - name: Wait for Workflow Completion
        id: check-status
        run: |
          INSTANCE_ID="${{ steps.start-workflow.outputs.instance_id }}"
          DEADLINE=$((SECONDS + 1500))   # 25 minutes
          STATUS="UNKNOWN"
          while [ $SECONDS -lt $DEADLINE ]; do
            STATUS=$(curl -s "http://localhost:3500/v1.0/workflows/dapr/$INSTANCE_ID" | jq -r '.runtimeStatus')
            echo "Workflow status: $STATUS"
            case "$STATUS" in COMPLETED|FAILED|TERMINATED) break ;; esac
            sleep 30
          done
          echo "status=$STATUS" >> $GITHUB_OUTPUT
```

Without this the job is killed at 10 minutes and the retry logic never runs.

## Verification

**Confirm the binding response shape first.** Before building on the positional-
array assumption in `ReadAsync`, run
`select package_name, collection_date from python_dapr_latest_packages_view`
through the binding and log the raw response bytes. Everything else depends on
this being right.

**Unit tests.** A new `CollectDaprStats.Tests` xunit project added to
`dapr-stats.sln`, covering the two places bugs will actually live — both pure
logic, no Dapr or Postgres required:

- `PostgresOutput.ParseRows`: positional array mapping, `"…T00:00:00Z"` yielding
  the correct `DateOnly` under a non-UTC local timezone, empty result set.
- `PackageDataChecker.FindMissing`: all present, some missing, all missing, rows
  present but at a different collection date, case-differing package names,
  empty view.
- `PackageDataChecker.ToCollectionDate`: `DateTimeKind.Utc` and
  `DateTimeKind.Unspecified` inputs both yielding the calendar date as written.

`InternalsVisibleTo` (or making `ParseRows` public) gives the test project access.

**Manual run.** Start the workflow via `local-tests.http` and confirm from
console output that the Check activities report the expected missing sets and
that the loop terminates early when nothing is missing.

## Known limitations

**Collection date mismatch at midnight UTC.** The Check activities compare
against `input.CollectionDate`, but `Get*PackageData` inserts `DateTime.UtcNow`.
A run straddling midnight UTC would see the two disagree and report packages as
permanently missing. The scheduled cron fires at 09:00 UTC and the loop window is
~12 minutes, so this cannot trigger on the schedule — only on a manual run
started just before midnight. Fixing it means threading `CollectionDate` into the
Get activities, which is out of scope here.

**NuGet name drift.** `GetNuGetPackageData` calls `SearchAsync(..., take: 1)` and
stores the top hit's `Identity.Id`, which is not guaranteed to equal the
requested name. If a search ever returns a different package ID, the check
reports that name missing on every pass and the loop burns all three attempts on
it. The attempt cap bounds the cost, and the Check activity's log line makes the
cause visible.
