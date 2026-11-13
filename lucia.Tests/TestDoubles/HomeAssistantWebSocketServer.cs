using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace lucia.Tests.TestDoubles;

/// <summary>
/// In-process Kestrel WebSocket server that emulates the Home Assistant
/// WebSocket API: sends <c>auth_required</c>, accepts one auth message, replies
/// <c>auth_ok</c>, records the single command it receives, and answers with a
/// result chosen by <see cref="CommandHandler"/>.
/// </summary>
public sealed class HomeAssistantWebSocketServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public HomeAssistantWebSocketServer()
    {
        BaseUrl = $"http://127.0.0.1:{GetFreePort()}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(BaseUrl);

        _app = builder.Build();
        _app.UseWebSockets();

        _app.Map("/api/websocket", HandleConnectionAsync);

        _app.StartAsync().GetAwaiter().GetResult();
    }

    /// <summary>Base URL (scheme + host + port) to point a client at this server.</summary>
    public string BaseUrl { get; }

    /// <summary>Raw JSON text of every command received, in order.</summary>
    public List<string> ReceivedCommands { get; } = [];

    /// <summary>The access token from the auth message, or null when none arrived yet.</summary>
    public string? LastAuthToken { get; private set; }

    /// <summary>
    /// Chooses the result for a received command. Default: success with no result payload.
    /// </summary>
    public Func<string, (bool Success, object? Result, string? Error)>? CommandHandler { get; set; }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleConnectionAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        // Step 1: announce auth_required (HA's handshake starts server-side).
        await SendJsonAsync(socket, """{"type":"auth_required","ha_version":"2026.11.0"}""");

        // Step 2: receive the auth message and record its token.
        var authRaw = await ReceiveJsonAsync(socket);
        using (var authDoc = JsonDocument.Parse(authRaw))
        {
            LastAuthToken = authDoc.RootElement.TryGetProperty("access_token", out var token)
                ? token.GetString()
                : null;
        }

        // Step 3: acknowledge.
        await SendJsonAsync(socket, """{"type":"auth_ok","ha_version":"2026.11.0"}""");

        // Step 4: receive the single command.
        var commandRaw = await ReceiveJsonAsync(socket);
        ReceivedCommands.Add(commandRaw);

        // Step 5: answer according to the configured handler (default: success).
        var (success, result, error) = CommandHandler?.Invoke(commandRaw) ?? (true, null, null);
        using var commandDoc = JsonDocument.Parse(commandRaw);
        var id = commandDoc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 1;

        if (success)
        {
            var payload = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["type"] = "result",
                ["success"] = true,
                ["result"] = result
            };
            await SendJsonAsync(socket, JsonSerializer.Serialize(payload));
        }
        else
        {
            var payload = new Dictionary<string, object>
            {
                ["id"] = id,
                ["type"] = "result",
                ["success"] = false,
                ["error"] = new Dictionary<string, string>
                {
                    ["message"] = error ?? "Unknown error",
                    ["code"] = "failed"
                }
            };
            await SendJsonAsync(socket, JsonSerializer.Serialize(payload));
        }

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
            }
            catch
            {
                // The client may have already closed; cleanup is best-effort.
            }
        }
    }

    private static async Task SendJsonAsync(WebSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<string> ReceiveJsonAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
