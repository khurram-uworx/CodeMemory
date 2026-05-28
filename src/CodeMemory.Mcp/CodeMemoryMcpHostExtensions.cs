using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeMemory.Mcp;

public static class CodeMemoryMcpHostExtensions
{
    public static IServiceCollection AddCodeMemoryMcp(this IServiceCollection services, Action<CodeMemoryMcpOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IOptions<CodeMemoryMcpOptions>>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<CodeMemoryMcpOptions>>();
            return Options.Create(options.CurrentValue);
        });
        return services;
    }
}
