using System.Net.WebSockets;
using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Hosting;

public sealed partial class KodiWebSocketNotificationService(
    IOptions<KodiOptions> options,
    PlaybackNotificationStore store,
    TimeProvider timeProvider,
    ILogger<KodiWebSocketNotificationService> logger) : BackgroundService
{
    private const int MaximumMessageBytes = 64 * 1024;
    private readonly KodiOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PlaybackNotifications.Enabled) return Task.CompletedTask;
        var workers = _options.Instances
            .Where(instance => !string.IsNullOrEmpty(instance.WebSocketEndpoint))
            .Select(instance => RunInstanceAsync(instance, stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunInstanceAsync(KodiInstanceOptions instance, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? failureKind = null;
            try
            {
                using var socket = CreateSocket(instance);
                await socket.ConnectAsync(new Uri(instance.WebSocketEndpoint), cancellationToken);
                store.SetConnected(instance.Alias, timeProvider.GetUtcNow());
                LogConnected(logger, instance.Alias);
                await ReceiveAsync(instance.Alias, socket, cancellationToken);
                failureKind = "closed";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException)
            {
                failureKind = "websocket";
            }
            catch (JsonException)
            {
                failureKind = "invalid-json";
            }
            catch (InvalidOperationException)
            {
                failureKind = "configuration";
            }
            catch (Exception)
            {
                failureKind = "unexpected";
            }

            store.SetDisconnected(instance.Alias, failureKind);
            LogDisconnected(logger, instance.Alias, failureKind);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PlaybackNotifications.ReconnectDelaySeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        store.SetDisconnected(instance.Alias, null);
    }

    private static ClientWebSocket CreateSocket(KodiInstanceOptions instance)
    {
        var socket = new ClientWebSocket();
        if (instance.AllowInvalidTlsCertificate)
#pragma warning disable CA5359 // Explicit per-instance operator opt-in, matching the HTTP transport.
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        return socket;
    }

    private async Task ReceiveAsync(string alias, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                if (result.EndOfMessage)
                {
                    message.SetLength(0);
                    message.Position = 0;
                }
                continue;
            }
            if (message.Length + result.Count > MaximumMessageBytes)
                throw new InvalidOperationException("Playback notification exceeded the bounded message size.");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            message.Position = 0;
            using var document = await JsonDocument.ParseAsync(message, cancellationToken: cancellationToken);
            store.Record(alias, document.RootElement, timeProvider.GetUtcNow());
            message.SetLength(0);
            message.Position = 0;
        }
    }

    [LoggerMessage(LogLevel.Information, "Kodi playback notification channel connected for alias {Alias}")]
    private static partial void LogConnected(ILogger logger, string alias);

    [LoggerMessage(LogLevel.Warning, "Kodi playback notification channel disconnected for alias {Alias}; kind={FailureKind}")]
    private static partial void LogDisconnected(ILogger logger, string alias, string? failureKind);
}
