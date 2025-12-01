using Dapr.Client;
using Dapr.Workflow;
using Discord;
using Discord.Rest;

namespace DaprStats
{
    public class GetDiscordData : WorkflowActivity<DiscordInput, bool>
    {
        private readonly DiscordRestClient _discordClient;
        private readonly PostgresOutput _output;
        private readonly DaprClient _daprClient;

        public GetDiscordData(DiscordRestClient discordRestClient, PostgresOutput output, DaprClient daprClient)
        {
            _discordClient = discordRestClient;
            _output = output;
            _daprClient = daprClient;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            DiscordInput input)
        {

            const string secretStore = "secretstore";
            const string DiscordBotTokenKey = "DISCORDBOTTOKEN";
            var tokenDictionary = await _daprClient.GetSecretAsync(secretStore, DiscordBotTokenKey);
            await _discordClient.LoginAsync(TokenType.Bot, tokenDictionary[DiscordBotTokenKey]);

            const string DaprDiscordServerIdKey = "DAPRDISCORDSERVERID";
            var serverIdDictionary = await _daprClient.GetSecretAsync(secretStore, DaprDiscordServerIdKey);
            ulong.TryParse(serverIdDictionary[DaprDiscordServerIdKey], out var DaprDiscordServerId);
            var daprServer = await _discordClient.GetGuildAsync(DaprDiscordServerId, withCounts: true);

            var data = new DiscordData(
                CollectionDate: DateTime.UtcNow,
                MemberCount: daprServer.ApproximateMemberCount
            );

            Console.WriteLine($"Disord data: {data.MemberCount}");

            if (!input.SkipStorage)
            {
                const string tableName = "discord_dapr";
                var sqlText = $"insert into {tableName} (collection_date, member_count) values ($1, $2)";
                var sqlParameters = new object[] { data.CollectionDate, data.MemberCount };
                await _output.InsertAsync(sqlText, sqlParameters);
            }

            return true;
        }
    }

    public record DiscordInput(bool SkipStorage);
    public record DiscordData(DateTime CollectionDate, long? MemberCount);
}