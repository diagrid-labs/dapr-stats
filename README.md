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
| `nuget_packages` | comma-separated list | the nine `Dapr.*`, `CommunityToolkit.*` and `Diagrid.*` packages | Packages to collect NuGet downloads for |
| `npm_packages` | comma-separated list | `@dapr/dapr` | Packages to collect npm downloads for |
| `python_packages` | comma-separated list | `dapr,dapr-agents,dapr-ext-workflow,diagrid` | Packages to collect PyPI downloads for |
| `collect_dockerhub` | checkbox | on | Docker Hub pulls for the `daprio/*` images |
| `collect_datadog` | checkbox | on | Datadog RUM views and users |
| `collect_scarf` | checkbox | on | Scarf building block page views and company visits |
| `collect_discord` | checkbox | on | Discord data |
| `collect_github` | checkbox | on | GitHub data for the dapr org |
| `collect_diagrid_dashboard` | checkbox | on | Diagrid dashboard data |
| `skip_storage` | checkbox | off | Dry run: fetch everything but write nothing to the database |

A few things worth knowing:

- **Clearing a package field skips that ecosystem entirely.** The workflow only starts a collector when its list is non-empty, so emptying `nuget_packages` is how you turn NuGet collection off. Entries may be padded with spaces; they are trimmed.
- **Unchecking a box has the same effect** for the sources whose lists are not exposed as inputs. `workflow_dispatch` allows at most 10 inputs, so the Docker Hub images, Datadog RUM services and Scarf pixel IDs live in the `FIXED_*` variables in the workflow's `env` block, and the checkboxes switch those lists on and off. Change the values there when an image or pixel is added.
- **Scheduled runs always use the defaults.** GitHub does not apply `workflow_dispatch` input defaults to `schedule` runs, so the workflow falls back to its `DEFAULT_*` and `FIXED_*` env values. When you change a default for the weekly run, update both the input's `default:` and the matching `DEFAULT_*` variable.
- The resolved workflow input is printed to the run log and to the run summary, so you can confirm what a manual run actually collected.
