using Dapr.Client;
using Dapr.Workflow;
using Discord;
using Discord.Rest;

namespace CollectDaprStats
{
    public class GetDiscordData : WorkflowActivity<string, bool>
    {
        private readonly DiscordRestClient _discordClient;
        private readonly DaprClient _daprClient;

        public GetDiscordData(DiscordRestClient discordRestClient, DaprClient daprClient)
        {
            _discordClient = discordRestClient;
            _daprClient = daprClient;
        }

        public override async Task<bool> RunAsync(
            WorkflowActivityContext context,
            string input)
        {

            await _discordClient.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("DiscordBotToken"));

            ulong.TryParse(Environment.GetEnvironmentVariable("DaprDiscordServerId"), out var DaprDiscordServerId);
            var daprServer = await _discordClient.GetGuildAsync(DaprDiscordServerId, withCounts: true);

            var data = new DiscordData
            {
                CollectionDate = DateTime.Today,
                ServerName = daprServer.Name,
                MemberCount = daprServer.ApproximateMemberCount
            };

            return true;
        }
    }

    public class DiscordData
    {
        public string ServerName { get; set; }
        public DateTime CollectionDate { get; set; }
        public long? MemberCount { get; set; }

    }
}