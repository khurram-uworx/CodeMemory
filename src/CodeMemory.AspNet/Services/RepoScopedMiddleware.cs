using System.Collections.Concurrent;

namespace CodeMemory.AspNet.Configuration;

public sealed class RepoScopedMiddleware
{
    const string McpPrefix = "/api/mcp";
    readonly RequestDelegate next;
    readonly HashSet<string> validRepoNames;

    public RepoScopedMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        this.next = next;
        var repos = configuration.GetSection("Repositories").Get<Dictionary<string, string>>();
        validRepoNames = new HashSet<string>(repos?.Keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        string? repoName = null;

        if (path is not null && path.StartsWith($"{McpPrefix}/", StringComparison.OrdinalIgnoreCase))
        {
            var remaining = path[(McpPrefix.Length + 1)..];
            var slashIdx = remaining.IndexOf('/');
            repoName = slashIdx >= 0 ? remaining[..slashIdx] : remaining;

            if (string.IsNullOrEmpty(repoName) || (validRepoNames.Count > 0 && !validRepoNames.Contains(repoName)))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync(string.IsNullOrEmpty(repoName)
                    ? "Repository name is required"
                    : $"Unknown repository '{repoName}'");
                return;
            }

            context.Items["RepoName"] = repoName;
        }

        if (repoName is not null)
        {
            var repoScoped = new RepoScopedServices(context.RequestServices, repoName);
            var originalServices = context.RequestServices;
            context.RequestServices = repoScoped;
            try
            {
                await next(context);
            }
            finally
            {
                context.RequestServices = originalServices;
            }
        }
        else
        {
            await next(context);
        }
    }
}

public static class RepoMiddlewareExtensions
{
    public static IApplicationBuilder UseRepoScopedMcp(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RepoScopedMiddleware>();
    }
}
