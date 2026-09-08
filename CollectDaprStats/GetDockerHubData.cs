using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dapr.Workflow;

namespace DaprStats
{
    public class GetDockerHubData : WorkflowActivity<DockerHubInput, bool>
    {
        private readonly HttpClient _httpClient;
        private readonly PostgresOutput _output;

        public GetDockerHubData(IHttpClientFactory httpClientFactory, PostgresOutput output)
        {
            _httpClient = httpClientFactory.CreateClient();
            _output = output;
        }

        private const string TableName = "dockerhub_images";

        // Declared above InsertSql: static initialisers run in textual order.
        private static readonly string[] KeyColumns =
            ["collection_week", "namespace", "image_name"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "pull_count"];

        /// <summary>
        /// Internal so the composed statement can be asserted without a database.
        /// </summary>
        internal static readonly string InsertSql =
            $"insert into {TableName} (namespace, image_name, collection_date, pull_count) " +
            "values ($1, $2, $3, $4) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            DockerHubInput input)
        {
            _httpClient.BaseAddress = new Uri("https://hub.docker.com/");
            var response = await _httpClient.GetAsync($"v2/repositories/{input.Namespace}/{input.ImageName}/");
            
            if (response.IsSuccessStatusCode)
            {
                var dockerHubResponse = await response.Content.ReadFromJsonAsync<DockerHubResponse>();
                
                var dockerHubImageData = new DockerHubImageData
                (
                    CollectionDate: DateTime.UtcNow,
                    Namespace: input.Namespace,
                    ImageName: dockerHubResponse.Name,
                    PullCount: dockerHubResponse.PullCount
                );

                Console.WriteLine($"Docker Hub Image: {dockerHubImageData.Namespace}/{dockerHubImageData.ImageName}, Pull Count: {dockerHubImageData.PullCount}");

                if (!input.SkipStorage)
                {
                    var sqlParameters = new object[] { dockerHubImageData.Namespace, dockerHubImageData.ImageName, dockerHubImageData.CollectionDate, dockerHubImageData.PullCount };
                    await _output.InsertAsync(InsertSql, sqlParameters);
                }

                return true;
            }

            Console.WriteLine($"Failed to retrieve Docker Hub data for {input.Namespace}/{input.ImageName}. Status: {response.StatusCode}");
            return false;
        }
    }
}

public record DockerHubInput(string Namespace, string ImageName, bool SkipStorage);

public record DockerHubResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pull_count")] long PullCount,
    [property: JsonPropertyName("last_updated")] string LastUpdated);

public record DockerHubImageData(string Namespace, string ImageName, long PullCount, DateTime CollectionDate);
