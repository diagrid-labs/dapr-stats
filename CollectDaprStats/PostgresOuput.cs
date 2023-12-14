using System.Text.Json;
using Dapr.Client;

namespace DaprStats
{
    public class PostgresOutput
    {
        private readonly DaprClient _daprClient;

        public PostgresOutput(DaprClient daprClient)
        {
            _daprClient = daprClient;
        }

        public async Task InsertAsync(string sqlText, object[] sqlParameters)
        {
            var paramsText = JsonSerializer.Serialize(sqlParameters);
            var metadata = new Dictionary<string, string>
            {
                {"sql", sqlText},
                {"params", paramsText}
            };

            const string bindingName = "daprstats";
            const string operation = "exec";
            const string data = "";

            await _daprClient.InvokeBindingAsync(bindingName, operation, data, metadata);
        }
    }
}