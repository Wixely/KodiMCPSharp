using System.Net;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Hosting;
using KodiMCPSharp.Kodi;
using KodiMCPSharp.Security;
using KodiMCPSharp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Settings.Configuration;

var contentRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var isWindowsService = WindowsServiceHelpers.IsWindowsService();
Directory.SetCurrentDirectory(contentRoot);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(contentRoot, "logs", "kodimcp-bootstrap-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        shared: true)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = contentRoot,
    });

    builder.Configuration
        .SetBasePath(contentRoot)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
        .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
        .AddJsonFile("KodiMCPSharp.json", optional: true, reloadOnChange: true)
        .AddJsonFile($"KodiMCPSharp.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
        .AddJsonFile("KodiMCPSharp.Local.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddEnvironmentVariables(prefix: "KODIMCP_")
        .AddCommandLine(args);

    var configuredServer = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new();
    if (isWindowsService)
    {
        builder.Host.UseWindowsService(options => options.ServiceName = configuredServer.WindowsServiceName);
    }

    var serilogReaderOptions = new ConfigurationReaderOptions(
        typeof(ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(FileLoggerConfigurationExtensions).Assembly);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration, serilogReaderOptions)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddSingleton<IValidateOptions<KodiOptions>, KodiOptionsValidator>();
    builder.Services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
    builder.Services.AddOptions<KodiOptions>()
        .Bind(builder.Configuration.GetSection(KodiOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddOptions<ServerOptions>()
        .Bind(builder.Configuration.GetSection(ServerOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<SafeText>();
    builder.Services.AddSingleton<KodiInstanceRegistry>();
    builder.Services.AddSingleton<IHandleStore, InMemoryHandleStore>();
    builder.Services.AddSingleton<KodiService>();
    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
        kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        if (configuredServer.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            kestrel.ListenLocalhost(configuredServer.Port);
        }
        else if (IPAddress.TryParse(configuredServer.Host, out var address))
        {
            kestrel.Listen(address, configuredServer.Port);
        }
        else
        {
            kestrel.ListenAnyIP(configuredServer.Port);
        }
    });

    var app = builder.Build();
    var server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
    var kodi = app.Services.GetRequiredService<IOptions<KodiOptions>>().Value;
    var controlsEnabled = !kodi.ReadOnly &&
        (kodi.Controls.AllowPlayback || kodi.Controls.AllowPlayerControl || kodi.Controls.AllowSeek ||
         kodi.Controls.AllowVolume || kodi.Controls.AllowStreamSelection || kodi.Controls.AllowPlaybackModes ||
         kodi.Controls.AllowPlaylists);

    app.UseSerilogRequestLogging();
    app.UseMiddleware<McpPasswordMiddleware>();
    app.MapGet("/healthz", () => Results.Ok(new
    {
        status = "ok",
        server = "KodiMCPSharp",
        configuredInstances = kodi.Instances.Count,
        readOnly = kodi.ReadOnly,
        controlsEnabled,
        timeUtc = DateTimeOffset.UtcNow,
    }));
    app.MapGet("/readyz", (KodiInstanceRegistry registry) => Results.Ok(new
    {
        status = registry.Count > 0 ? "ready" : "configuration-required",
        configuredInstances = registry.Count,
    }));
    app.MapMcp(server.Path);

    Log.Information(
        "KodiMCPSharp starting at http://{Host}:{Port}{Path}; mode={Mode}; instances={InstanceCount}; readOnly={ReadOnly}; controls={ControlsEnabled}",
        server.Host, server.Port, server.Path, isWindowsService ? "WindowsService" : "Interactive", kodi.Instances.Count,
        kodi.ReadOnly, controlsEnabled);

    await app.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "KodiMCPSharp terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
