namespace KodiMCPSharp.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5719;
    public string Path { get; set; } = "/mcp";
    public string WindowsServiceName { get; set; } = "KodiMCPSharp";
    public string Password { get; set; } = string.Empty;
}
