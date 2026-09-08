# Dapr Stats

This repository contains a program that collects Dapr SDK and community metrics.

At the moment the data sources include:

- Discord member count of the Dapr server
- NuGet package downloads for `Dapr.Client`, `Dapr.Workflow`, `Dapr.AspNetCore`, `Dapr.Extensions.Configuration`, `Dapr.Actors`, `Dapr.Messaging`, `Dapr.Jobs`, `CommunityToolkit.Aspire.Hosting.Dapr` and `Diagrid.AI.Microsoft.AgentFramework`
- Npm package downloads for `@dapr/dapr`
- Python package downloads for `dapr`, `dapr-agents`, `dapr-ext-workflow` and `diagrid`
- Docker Hub pull counts for `daprio/daprd`, `daprio/scheduler`, `daprio/operator`, `daprio/injector`, `daprio/sentry` and `daprio/placement`
- GitHub data for all repositories under the dapr org:
  - Commits
  - Issues
  - Comments
  - Pull Requests
  - Forks and stars
  - Distinct contributing users
- Scarf pixel data for `dapr.io` and `docs.dapr.io`:
  - Weekly views of each Dapr building block documentation page, per company
  - Weekly company visit counts across both sites
- Scarf pixel data for `diagrid.io` and `docs.diagrid.io`:
  - Weekly views of each page, per company
- Datadog RUM views and users for the `dev-dashboard` service
- Datadog RUM authenticated sessions, identified users and customer
  organisations for the `conductor-ui` service on `conductor.r1.diagrid.io` and
  `dapr-ops-dashboard.diagrid.io`, including every organisation each user has
  been seen under and running totals of unique users and unique organisations
- Download count of the `diagrid-dashboard` container image on GitHub Container Registry

The program is a .NET web service that's using Dapr workflow.

## Set Environment Variables

The application requires the following secrets to be set as environment variables when running locally:

- `export DAPRSTATSGITHUBPAT=<GITHUB_PAT_VALUE>`
- `export DISCORDBOTTOKEN=<DISCORD_BOT_TOKEN_VALUE>`
- `export DAPRDISCORDSERVERID=<DISCORD_SERVER_ID_VALUE>`
- `export POSTGRESQLCONNECTION=<POSTGRES_CONNECTION_VALUE>`
- `export DATADOGAPIKEY=<DATADOG_API_KEY_VALUE>`
- `export DATADOGAPPKEY=<DATADOG_APP_KEY_VALUE>`
- `export SCARF_DAPR_API_TOKEN=<SCARF_API_TOKEN_VALUE>`
- `export SCARF_DIAGRID_API_TOKEN=<SCARF_API_TOKEN_VALUE>`

## Running the CollectDaprStats program

Collection runs automatically every Monday on a GitHub Actions runner, see [Running via GitHub Actions](#running-via-github-actions). To run it yourself, in a GitHub Codespace or locally:

1. Start the .NET service:

    ``` bash
    dapr run -f .
    ```

2. Run the `CollectorWorkflow` workflow via the Dapr http endpoint:

    ```http
    POST http://localhost:3500/v1.0/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
    ```

    Use the [local-tests.http](local-tests.http) file with the VSCode REST client (should be pre-configured with the Codespace) rather than writing the request by hand. It holds the current request body along with narrower requests for exercising a single collector, and queries for inspecting what was stored. The body shape is defined by `CollectorWorkflowInput` in [CollectorWorkflow.cs](CollectDaprStats/CollectorWorkflow.cs): every `*Names`, `*Images`, `*Services` and `*PixelIds` list is skipped when empty, and `SkipStorage` fetches everything without writing to the database.

## Running via GitHub Actions

The [Run CollectorWorkflow](.github/workflows/run-workflow.yaml) workflow runs on a schedule at 08:43 UTC every Monday. The odd minute is deliberate: GitHub delays or drops scheduled runs under load, and the top of the hour is the most contended slot. It can also be started manually from the *Actions* tab with *Run workflow*, which offers these inputs:

| Input | Type | Default | Effect |
|---|---|---|---|
| `nuget_packages` | `all` / `none` / list | `all` | NuGet downloads: the nine standard packages, nothing, or the names you give |
| `npm_packages` | `all` / `none` / list | `all` | npm downloads: `@dapr/dapr`, nothing, or the names you give |
| `python_packages` | `all` / `none` / list | `all` | PyPI downloads: the four standard packages, nothing, or the names you give |
| `collect_dockerhub` | checkbox | on | Docker Hub pulls for the `daprio/*` images |
| `collect_datadog` | checkbox | on | Datadog RUM views and users, plus `conductor-ui` authenticated sessions and identified users |
| `collect_scarf` | `all` / `none` / `dapr` / `diagrid` | `all` | Which Scarf accounts to collect. `dapr` is building block page views and company visits; `diagrid` is page views per company for `diagrid.io` and `docs.diagrid.io` |
| `collect_discord` | checkbox | on | Discord data |
| `collect_github` | checkbox | on | GitHub data for the dapr org |
| `collect_diagrid_dashboard` | checkbox | on | Diagrid dashboard data |
| `skip_storage` | checkbox | off | Dry run: fetch everything but write nothing to the database |

A few things worth knowing:

- **Type `none` to skip an ecosystem — do not clear the field.** Clearing a text input does not submit an empty value: GitHub substitutes the input's default, so an emptied field is indistinguishable from an omitted one and collects everything. `none` is a value GitHub cannot override. Entries in an explicit list may be padded with spaces; they are trimmed, and `all` / `none` are case-insensitive.
- **Re-running is safe, and is how you repair a bad run.** Every source deduplicates on the ISO week the run happened in, so collecting twice on the same day, or twice in the same week, updates the stored numbers rather than duplicating them. A run that half-failed — GitHub throttled some repos, pypistats rate-limited a package — is fixed by simply running it again. This matters because GitHub delays scheduled runs: this workflow's have been late by two hours, by nineteen, and once by eight days, and a delayed run that lands after a manual one used to double every row it wrote.
- **Unchecking a box is reliable**, because an unchecked box submits a real `false`. A `choice` input such as `collect_scarf` is equally safe, since it always submits one of its declared options. This is why the other sources are checkboxes and a choice rather than text fields: `workflow_dispatch` allows at most 10 inputs, so the Docker Hub images, Datadog RUM services and Scarf pixel IDs live in the `FIXED_*` variables in the workflow's `env` block and the toggles switch those lists on and off. Change the values there when an image or pixel is added.
- **`collect_scarf` selects an account, not just on/off.** `dapr` collects the building block and company-visit tables; `diagrid` collects page views for `diagrid.io` and `docs.diagrid.io`. Because each account's writes rewrite three ISO weeks, being able to re-run one without touching the other matters. Making it a `choice` cost no extra input; a second checkbox would have exceeded the cap.
- **The package lists are defined once**, in the `DEFAULT_*` env values. Both a scheduled run and a manual run left at `all` read them from there, so there is nothing to keep in sync.
- The resolved workflow input is printed to the run log and to the run summary, so you can confirm what a manual run actually collected.
