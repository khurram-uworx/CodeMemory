using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CodeMemory.Tests.Mcp;

public sealed class AspNetSqlQueryToolTests : BaseToolTests
{
    [Test]
    public async Task SqlQueryTool_AppearsInAspNetDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Storage:Provider"] = "inmemory",
                        ["Repositories:codememory"] = "."
                    });
                });
            });

        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        var toolNames = tools!.Select(tool => tool!["name"]?.GetValue<string>()).ToList();

        Assert.That(toolNames, Does.Contain("sql_query"));
    }
}
