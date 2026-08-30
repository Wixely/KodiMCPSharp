using System.Security.Cryptography;
using System.Text;
using KodiMCPSharp.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Hosting;

public sealed class McpPasswordMiddleware(RequestDelegate next, IOptions<ServerOptions> options)
{
    private readonly string _mcpPath = options.Value.Path.TrimEnd('/');
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.Password);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_expected.Length == 0 || !context.Request.Path.StartsWithSegments(_mcpPath))
        {
            await next(context);
            return;
        }

        var supplied = GetPassword(context.Request);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        if (suppliedBytes.Length != _expected.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, _expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        await next(context);
    }

    private static string GetPassword(HttpRequest request)
    {
        const string bearer = "Bearer ";
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return authorization[bearer.Length..].Trim();
        }
        return request.Headers["X-MCP-Password"].ToString();
    }
}
