namespace CodeMemory.AspNet.Configuration;

public sealed class RepoScopedMiddleware
{
    readonly RequestDelegate next;

    public RepoScopedMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Items.TryGetValue("RepoName", out var repoNameObj) && repoNameObj is string repoName)
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
        app.UseMiddleware<RepoScopedMiddleware>();
        return app;
    }
}
