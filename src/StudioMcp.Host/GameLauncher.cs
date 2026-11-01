// Added 2026-11-02 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace StudioMcp.Host;

/// <summary>Starts the game this server belongs to and waits for its bridge. Runs in the host, because no bridge is up yet.</summary>
public static class GameLauncher
{
    private static readonly string[] MainGames = ["KoikatsuSunshine.exe", "Koikatu.exe", "Koikatsu Party.exe"];

    public static async Task<JsonObject> Launch(BridgeClient bridge, float timeoutSeconds)
    {
        var connection = Environment.GetEnvironmentVariable("STUDIOMCP_CONNECTION")
            ?? throw new McpException("Set STUDIOMCP_CONNECTION to the game's UserData/StudioMCP/connection.json.");
        // <game>/UserData/StudioMCP/<connection file>
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(connection))));
        if (root == null) throw new McpException("Cannot find the game folder from " + connection + ".");
        var names = ToolRegistry.Mode == "maker" ? MainGames : ["CharaStudio.exe"];
        var exe = names.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists)
            ?? throw new McpException("No " + string.Join(" or ", names) + " in " + root + ".");

        var started = Stopwatch.StartNew();
        var processName = Path.GetFileNameWithoutExtension(exe);
        var running = Find(processName, exe, out var elsewhere);
        // Installs of the same game share a port, so a second one could not open its bridge.
        if (running == null && elsewhere != null)
            throw new McpException(processName + " is already running from " + elsewhere + "; it uses the same MCP port. Close it first.");
        var launched = running == null;
        if (launched)
        {
            running = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = true })
                ?? throw new McpException("Windows did not start " + exe + ".");
        }

        var process = running!;
        var deadline = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600));
        while (started.Elapsed < deadline)
        {
            try
            {
                var status = (await bridge.Call("status"))["result"]!.AsObject();
                return new JsonObject { ["launched"] = launched, ["processId"] = process.Id, ["exe"] = exe, ["seconds"] = Math.Round(started.Elapsed.TotalSeconds, 1),
                    ["mode"] = status["mode"]?.DeepClone(), ["game"] = status["game"]?.DeepClone(), ["ready"] = status["ready"]?.DeepClone(),
                    ["sceneSession"] = status["sceneSession"]?.DeepClone(), ["maker"] = status["maker"]?.DeepClone() };
            }
            catch (Exception error) when (error is McpException or IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or System.Text.Json.JsonException) { }
            if (process.HasExited)
                throw new McpException(Path.GetFileName(exe) + " exited with code " + process.ExitCode + " before its bridge started. Check " + Path.Combine(root, "output_log.txt") + ".");
            await Task.Delay(1000);
        }
        throw new McpException(Path.GetFileName(exe) + " is running but its bridge did not answer within " + deadline.TotalSeconds + " s. Is the StudioMCP plugin installed in " + root + "?");
    }

    private static Process? Find(string processName, string exe, out string? elsewhere)
    {
        elsewhere = null;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            if (path == null) continue;
            if (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) return process;
            elsewhere = path;
        }
        return null;
    }
}
