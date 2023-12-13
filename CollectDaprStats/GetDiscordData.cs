using Azure;
using Azure.Data.Tables;
using Discord;
using Discord.Rest;
using Microsoft.Azure.Functions.Worker;

namespace CollectDaprStats
{
    public class GetDiscordData
    {
        private readonly DiscordRestClient _discordClient;
        public GetDiscordData(DiscordRestClient discordRestClient)
        {
            _discordClient = discordRestClient;
        }

        [Function(nameof(GetDiscordData))]
        [TableOutput("DaprDiscord", Connection = "AzureWebJobsStorage")]
        public async Task<DiscordData> Run(
            [ActivityTrigger] FunctionContext executionContext)
        {
            var logger = executionContext.GetLogger(nameof(GetNuGetPackageData));

            await _discordClient.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("DiscordBotToken"));

            ulong.TryParse(Environment.GetEnvironmentVariable("DaprDiscordServerId"), out var DaprDiscordServerId);
            var daprServer = await _discordClient.GetGuildAsync(DaprDiscordServerId, withCounts: true);

            return new DiscordData
            {
                DayOfYear = DateTime.UtcNow.DayOfYear,
                PartitionKey = DateTime.UtcNow.DayOfYear.ToString(),
                RowKey = "Discord",
                MemberCount = daprServer.ApproximateMemberCount
            };
        }
    }

    public class DiscordData : ITableEntity
    {
        public int DayOfYear { get; set; }
        public long? MemberCount { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public override string ToString()
        {
            return $"{MemberCount}";
        }
    }
}