using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Configuration;

public sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        if (options.Port is < 1 or > 65535) return ValidateOptionsResult.Fail("Server:Port must be between 1 and 65535.");
        if (!options.Path.StartsWith('/') || options.Path.Contains('?', StringComparison.Ordinal)) return ValidateOptionsResult.Fail("Server:Path must be an absolute path without a query string.");
        var local = options.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    options.Host is "127.0.0.1" or "::1";
        if (!local && string.IsNullOrWhiteSpace(options.Password))
        {
            return ValidateOptionsResult.Fail("Server:Password is required when binding beyond loopback.");
        }
        return ValidateOptionsResult.Success;
    }
}
