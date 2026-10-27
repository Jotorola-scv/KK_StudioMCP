using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudioMcp.Host;

public sealed class BridgeClient
{
    public async Task<JsonObject> Call(string method, JsonObject? args = null)
    {
        var connectionFile = Environment.GetEnvironmentVariable("STUDIOMCP_CONNECTION")
            ?? throw new InvalidOperationException("Set STUDIOMCP_CONNECTION to the game's UserData/StudioMCP/connection.json.");
        var connection = JsonNode.Parse(await File.ReadAllTextAsync(connectionFile))!.AsObject();
        var timeoutSetting = Environment.GetEnvironmentVariable("STUDIOMCP_REQUEST_TIMEOUT_SECONDS");
        var timeoutSeconds = 35;
        if (!string.IsNullOrWhiteSpace(timeoutSetting)
            && (!int.TryParse(timeoutSetting, out timeoutSeconds) || timeoutSeconds < 5 || timeoutSeconds > 180))
            throw new InvalidOperationException("STUDIOMCP_REQUEST_TIMEOUT_SECONDS must be an integer between 5 and 180.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(connection["url"]!.GetValue<string>()), timeout.Token);
        var id = Guid.NewGuid().ToString("N");
        var message = new JsonObject { ["id"] = id, ["token"] = connection["token"]!.GetValue<string>(),
            ["method"] = method, ["args"] = args ?? new JsonObject() };
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        using var response = new MemoryStream();
        var buffer = new byte[65536];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Bridge disconnected.");
            await response.WriteAsync(buffer.AsMemory(0, frame.Count), timeout.Token);
            if (response.Length > 16 * 1024 * 1024) throw new IOException("Bridge response exceeds safety limit.");
        } while (!frame.EndOfMessage);
        var result = JsonNode.Parse(response.ToArray())!.AsObject();
        if (result["id"]?.GetValue<string>() != id) throw new IOException("Bridge response id does not match.");
        if (result["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(result["error"]?["message"]?.GetValue<string>() ?? "Bridge request failed.");
        return new JsonObject { ["result"] = result["result"]?.DeepClone() };
    }
}
