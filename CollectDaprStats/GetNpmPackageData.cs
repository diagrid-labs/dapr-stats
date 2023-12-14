// using Azure.Data.Tables;
// using Azure;
// using Microsoft.Azure.Functions.Worker;
// using Microsoft.Extensions.Logging;
// using System.Net;
// using System.Net.Http.Json;
// using System.Text.RegularExpressions;

// namespace CollectDaprStats
// {
//     public class GetNpmPackageData
//     {
//         private readonly HttpClient _httpClient;

//         public GetNpmPackageData(IHttpClientFactory httpClientFactory)
//         {
//             _httpClient = httpClientFactory.CreateClient();
//         }

//         [Function(nameof(GetNpmPackageData))]
//         [TableOutput("NpmDapr", Connection = "AzureWebJobsStorage")]
//         public async Task<IEnumerable<NpmPackageVersionData>> Run(
//             [ActivityTrigger] string packageName, FunctionContext executionContext)
//         {
//             var logger = executionContext.GetLogger(nameof(GetNpmPackageData));
//             var npmPackageVersionDataList = new List<NpmPackageVersionData>();

//             _httpClient.BaseAddress = new Uri("https://api.npmjs.org/");
//             packageName = WebUtility.UrlEncode(packageName);
//             var response = await _httpClient.GetAsync($"versions/{packageName}/last-week");
//             if (response.IsSuccessStatusCode)
//             {
//                 var npmPackageVersionResponse = await response.Content.ReadFromJsonAsync<NpmPackageVersionResponse>();
//                 foreach (var versionPair in npmPackageVersionResponse.Downloads)
//                 {
//                     var npmPackageVersionData = new NpmPackageVersionData
//                     {
//                         DayOfYear = DateTime.UtcNow.DayOfYear,
//                         PackageName = npmPackageVersionResponse.Package,
//                         VersionString = versionPair.Key,
//                         Downloads = versionPair.Value,
//                         PartitionKey = DateTime.UtcNow.DayOfYear.ToString(),
//                         RowKey = CleanPartitionKey($"{npmPackageVersionResponse.Package}-{versionPair.Key}")
//                     };
//                     npmPackageVersionDataList.Add(npmPackageVersionData);
//                     logger.LogInformation(npmPackageVersionData.ToString());
//                 }
//             }
//             else
//             {
//                 logger.LogError($"Failed to get data for {packageName}");
//             }

//             return npmPackageVersionDataList;
//         }

//         private static string CleanPartitionKey(string partitionKey)
//         {
//             // use a regular expression to replace a slash or a backslash with a dash
//             return Regex.Replace(partitionKey, @"[\/\\]", "-");
//         }
//     }

//     public class NpmPackageVersionResponse
//     {
//         public string Package { get; set; }
//         public Dictionary<string, int> Downloads { get; set; }
//     }

//     public class NpmPackageVersionData : ITableEntity
//     {
//         public int DayOfYear { get; set; }
//         public string PackageName { get; set; }
//         public string? VersionString { get; set; }
//         public long? Downloads { get; set; }
//         public string PartitionKey { get; set; }
//         public string RowKey { get; set; }
//         public DateTimeOffset? Timestamp { get; set; }
//         public ETag ETag { get; set; }

//         public override string ToString()
//         {
//             return $"{PackageName} {VersionString} {Downloads}";
//         }
//     }
// }