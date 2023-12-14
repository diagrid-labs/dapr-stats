using Dapr.Client;
using Dapr.Workflow;
using Discord;
using Discord.Rest;

namespace DaprStats
{
    public class GetDiscordData : WorkflowActivity<string, bool>
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
            string input)
        {

            const string secretStore = "secretstore";
            const string DiscordBotTokenKey = "DiscordBotToken";
            var tokenDictionary = await _daprClient.GetSecretAsync(secretStore, DiscordBotTokenKey);
            Console.WriteLine($"DiscordBotToken: {tokenDictionary[DiscordBotTokenKey]}");
            await _discordClient.LoginAsync(TokenType.Bot, tokenDictionary[DiscordBotTokenKey]);

            const string DaprDiscordServerIdKey = "DaprDiscordServerId";
            var serverIdDictionary = await _daprClient.GetSecretAsync(secretStore, DaprDiscordServerIdKey);
            Console.WriteLine($"DaprDiscordServerId: {tokenDictionary[DiscordBotTokenKey]}");
            ulong.TryParse(serverIdDictionary[DaprDiscordServerIdKey], out var DaprDiscordServerId);
            var daprServer = await _discordClient.GetGuildAsync(DaprDiscordServerId, withCounts: true);

            var data = new DiscordData
            {
                CollectionDate = DateTime.UtcNow,
                MemberCount = daprServer.ApproximateMemberCount
            };

            const string tableName = "discord_dapr";
            var sqlText = $"insert into {tableName} (collection_date, member_count) values ($1, $2)";
            var sqlParameters = new object[] { data.CollectionDate, data.MemberCount };
            await _output.InsertAsync(sqlText, sqlParameters);

            return true;
        }
    }

    public class DiscordData
    {
        public DateTime CollectionDate { get; set; }
        public long? MemberCount { get; set; }

    }
}