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

        private const string TableName = "discord_dapr";

        private static readonly string[] KeyColumns = ["collection_week"];

        private static readonly string[] UpdateColumns =
            ["collection_date", "member_count"];

        internal static readonly string InsertSql =
            $"insert into {TableName} (collection_date, member_count) " +
            "values ($1, $2) " +
            UpsertBuilder.BuildOnConflict(KeyColumns, UpdateColumns);

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
                var sqlParameters = new object[] { data.CollectionDate, data.MemberCount };
                await _output.InsertAsync(InsertSql, sqlParameters);
            }

            return true;
        }
    }

    public record DiscordInput(bool SkipStorage);
    public record DiscordData(DateTime CollectionDate, long? MemberCount);
}