using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Janet.Mcp.Security;

/// <summary>Puts the loopback guard and the bearer check in front of a server's endpoints.</summary>
public static class McpSecurityExtensions
{
    /// <summary>
    /// Installs <see cref="LoopbackGuard"/> and then the bearer check, in that order. Call it
    /// first, before any Map. Do not also call <see cref="LoopbackGuard.UseLoopbackGuard"/>.
    /// </summary>
    /// <remarks>
    /// The guard runs first, always, and there is no public way to install the bearer check on its
    /// own -- so the order cannot be got wrong. A forged Host or Origin is refused for what it is
    /// (400 or 403) before the token is looked at, and a request from a rebound page never reaches
    /// the provider or the counters.
    ///
    /// In <see cref="BearerMode.Off"/> only the guard is installed. In
    /// <see cref="BearerMode.Report"/> every request goes through and its outcome is counted. In
    /// <see cref="BearerMode.Require"/> anything but the right token is answered 401, plain text
    /// with nosniff. <see cref="BearerAuthenticator.ExemptPath"/> (/health) is never refused or
    /// counted.
    ///
    /// The 401 carries no WWW-Authenticate header. The MCP authorization spec has clients answer a
    /// 401 by discovering an OAuth authorization server -- from the header's resource_metadata
    /// parameter when there is one, and by probing /.well-known/oauth-protected-resource when there
    /// is not -- and nothing confirms that a bare <c>Bearer realm=...</c> keeps a client out of
    /// that flow. There is no OAuth here to discover.
    /// </remarks>
    /// <param name="app">The application pipeline.</param>
    /// <param name="bearer">
    /// The bearer check, which the server keeps to read its snapshot and disposes when it stops.
    /// </param>
    public static IApplicationBuilder UseMcpSecurity(this IApplicationBuilder app, BearerAuthenticator bearer)
    {
        ArgumentNullException.ThrowIfNull(bearer);
        app.UseLoopbackGuard();

        if (bearer.Mode == BearerMode.Off)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals(BearerAuthenticator.ExemptPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            BearerOutcome outcome = await bearer.AuthenticateAsync(context.Request.Headers.Authorization);
            if (outcome == BearerOutcome.Ok || bearer.Mode == BearerMode.Report)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await context.Response.WriteAsync(bearer.RefusalMessage(outcome) + Environment.NewLine);
        });
    }
}
