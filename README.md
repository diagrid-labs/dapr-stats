# Dapr Stats

This repository contains a program that collects Dapr SDK and community metrics.

At the moment the data sources include:

- Discord users
- Nuget package downloads for `Dapr.Client`
- Npm package downloads for `@dapr/dapr`
- Python package downloads for `dapr`
- GitHub data for all repositories under the dapr org:
  - Commits
  - Issues
  - Comments
  - Pull Requests
- Scarf pixel data for `dapr.io` and `docs.dapr.io`:
  - Weekly views of each Dapr building block documentation page, per company
  - Weekly company visit counts across both sites

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

The program is run in a GitHub Codespace on the 1st and 16th of each month.

1. Start the .NET service:

    ``` bash
    dapr run -f .
    ```

2. Run the `CollectorWorkflow` workflow via the Dapr http endpoint. Use the [local-test.http](local-tests.http) file with the VSCode REST client (should be pre-configured with the Codespace).

    ```http
    POST {{dapr_url}}/v1.0-beta1/workflows/dapr/CollectorWorkflow/start?instanceID={{workflow_id}}
    Content-Type: application/json

    {
        "CollectionDate" : "{{currentDate}}",
        "CollectNuGetData" : true,
        "CollectNpmData" : true,
        "CollectPythonData" : true,
        "CollectDiscordData" : true,
        "CollectGitHubData" : true
    }
    ```
