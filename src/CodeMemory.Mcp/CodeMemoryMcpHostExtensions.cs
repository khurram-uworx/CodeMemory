using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeMemory.Mcp;

/// <summary>Extension methods for registering CodeMemory MCP services.</summary>
public static class CodeMemoryMcpHostExtensions
{
    /// <summary>Registers CodeMemory MCP options with the given configuration delegate.</summary>
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
