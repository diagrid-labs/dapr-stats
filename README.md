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
- Datadog RUM views and users for the `dev-dashboard` service
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

The [Run CollectorWorkflow](.github/workflows/run-workflow.yaml) workflow runs on a schedule at 09:00 UTC every Monday. It can also be started manually from the *Actions* tab with *Run workflow*, which offers these inputs:

| Input | Type | Default | Effect |
|---|---|---|---|
| `nuget_packages` | `all` / `none` / list | `all` | NuGet downloads: the nine standard packages, nothing, or the names you give |
| `npm_packages` | `all` / `none` / list | `all` | npm downloads: `@dapr/dapr`, nothing, or the names you give |
| `python_packages` | `all` / `none` / list | `all` | PyPI downloads: the four standard packages, nothing, or the names you give |
| `collect_dockerhub` | checkbox | on | Docker Hub pulls for the `daprio/*` images |
| `collect_datadog` | checkbox | on | Datadog RUM views and users |
| `collect_scarf` | checkbox | on | Scarf building block page views and company visits |
| `collect_discord` | checkbox | on | Discord data |
| `collect_github` | checkbox | on | GitHub data for the dapr org |
| `collect_diagrid_dashboard` | checkbox | on | Diagrid dashboard data |
| `skip_storage` | checkbox | off | Dry run: fetch everything but write nothing to the database |

A few things worth knowing:

- **Type `none` to skip an ecosystem — do not clear the field.** Clearing a text input does not submit an empty value: GitHub substitutes the input's default, so an emptied field is indistinguishable from an omitted one and collects everything. `none` is a value GitHub cannot override. Entries in an explicit list may be padded with spaces; they are trimmed, and `all` / `none` are case-insensitive.
- **Unchecking a box is reliable**, because an unchecked box submits a real `false`. This is why the other six sources are checkboxes: `workflow_dispatch` allows at most 10 inputs, so the Docker Hub images, Datadog RUM services and Scarf pixel IDs live in the `FIXED_*` variables in the workflow's `env` block and the checkboxes switch those lists on and off. Change the values there when an image or pixel is added.
- **The package lists are defined once**, in the `DEFAULT_*` env values. Both a scheduled run and a manual run left at `all` read them from there, so there is nothing to keep in sync.
- The resolved workflow input is printed to the run log and to the run summary, so you can confirm what a manual run actually collected.
