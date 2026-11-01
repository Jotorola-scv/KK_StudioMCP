// Added 2026-11-02 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace StudioMcp.Host;

/// <summary>
/// Lists and deletes what the bridge writes under UserData/StudioMCP. Runs in the host, so it also works with the game closed.
/// Only names the bridge generates count; anything else in that folder (the user's own images, textures/) is never touched.
/// </summary>
public static class OutputCleaner
{
    private sealed record Output(string Kind, string Path, bool IsFolder, long Bytes, int Files, DateTime Modified);

    private static readonly Regex Capture = new(@"^[0-9a-f]{32}(-turntable|\.depth\.[a-z0-9]+)?\.(png|exr|json|raw)$|^[0-9a-f]{32}\.depth\.[a-z0-9]+$|^dof-buffer-[0-9a-f]{32}\.png$", RegexOptions.Compiled);
    private static readonly Regex Checkpoint = new(@"^scene-[0-9a-f]{32}\.png$", RegexOptions.Compiled);
    private static readonly Regex Stamped = new(@"^\d{8}-\d{6}-", RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex Backup = new(@"^(backup|uninstall)-\d{8}-\d{6}$", RegexOptions.Compiled);
    private static readonly string[] Kinds = ["captures", "exports", "samples", "masks", "checkpoints", "backups"];
    // Outputs from before this server started belong to earlier work, possibly another agent's.
    private static readonly DateTime SessionStart = Process.GetCurrentProcess().StartTime;

    private static string Root()
    {
        var connection = Environment.GetEnvironmentVariable("STUDIOMCP_CONNECTION")
            ?? throw new McpException("Set STUDIOMCP_CONNECTION to the game's UserData/StudioMCP/connection.json.");
        return Path.GetDirectoryName(Path.GetFullPath(connection))!;
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static Output? File_(string kind, FileInfo file) => IsLink(file) ? null : new Output(kind, file.FullName, false, file.Length, 1, file.LastWriteTime);

    private static Output? Folder(string kind, DirectoryInfo folder)
    {
        if (IsLink(folder)) return null;
        var files = folder.GetFiles("*", SearchOption.AllDirectories);
        var modified = files.Length == 0 ? folder.LastWriteTime : files.Max(f => f.LastWriteTime);
        return new Output(kind, folder.FullName, true, files.Sum(f => f.Length), files.Length, modified);
    }

    private static List<Output> Scan()
    {
        var root = new DirectoryInfo(Root());
        var result = new List<Output?>();
        if (!root.Exists) return [];
        foreach (var file in root.GetFiles())
        {
            if (Capture.IsMatch(file.Name)) result.Add(File_("captures", file));
            else if (Checkpoint.IsMatch(file.Name)) result.Add(File_("checkpoints", file));
        }
        foreach (var folder in root.GetDirectories())
            if (Backup.IsMatch(folder.Name)) result.Add(Folder("backups", folder));
        void Under(string name, string kind, Regex pattern)
        {
            var parent = new DirectoryInfo(Path.Combine(root.FullName, name));
            if (!parent.Exists) return;
            foreach (var folder in parent.GetDirectories())
                if (pattern.IsMatch(folder.Name)) result.Add(Folder(kind, folder));
        }
        Under("exports", "exports", Stamped);
        Under("samples", "samples", Stamped);
        Under("masks", "masks", Hex);
        return result.OfType<Output>().ToList();
    }

    /// <summary>Totals for studio_status; a scan failure must not break the status call.</summary>
    public static JsonObject Summary()
    {
        try
        {
            var outputs = Scan();
            return new JsonObject { ["folder"] = Root(), ["files"] = outputs.Sum(o => o.Files), ["bytes"] = outputs.Sum(o => o.Bytes),
                ["sessionBytes"] = outputs.Where(o => o.Modified >= SessionStart).Sum(o => o.Bytes) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new JsonObject { ["error"] = error.Message }; }
    }

    public static JsonObject List(string[]? kinds, bool items, int limit)
    {
        var wanted = Check(kinds) ?? Kinds;
        var outputs = Scan().Where(o => wanted.Contains(o.Kind)).ToList();
        var result = new JsonObject { ["folder"] = Root(), ["sessionStart"] = SessionStart.ToString("yyyy-MM-dd HH:mm:ss"),
            ["totalBytes"] = outputs.Sum(o => o.Bytes), ["sessionBytes"] = outputs.Where(o => o.Modified >= SessionStart).Sum(o => o.Bytes) };
        var groups = new JsonObject();
        foreach (var kind in wanted)
        {
            var group = outputs.Where(o => o.Kind == kind).ToList();
            groups[kind] = new JsonObject { ["count"] = group.Count, ["bytes"] = group.Sum(o => o.Bytes),
                ["sessionCount"] = group.Count(o => o.Modified >= SessionStart), ["sessionBytes"] = group.Where(o => o.Modified >= SessionStart).Sum(o => o.Bytes),
                ["oldest"] = group.Count == 0 ? null : group.Min(o => o.Modified).ToString("yyyy-MM-dd HH:mm"),
                ["newest"] = group.Count == 0 ? null : group.Max(o => o.Modified).ToString("yyyy-MM-dd HH:mm") };
        }
        result["kinds"] = groups;
        if (items)
            result["items"] = new JsonArray(outputs.OrderByDescending(o => o.Modified).Take(Math.Clamp(limit, 1, 500)).Select(o => (JsonNode)new JsonObject {
                ["kind"] = o.Kind, ["path"] = o.Path, ["bytes"] = o.Bytes, ["modified"] = o.Modified.ToString("yyyy-MM-dd HH:mm:ss"), ["session"] = o.Modified >= SessionStart }).ToArray());
        return result;
    }

    public static async Task<JsonObject> Cleanup(BridgeClient bridge, string[]? kinds, string[]? paths, string[]? keepPaths, string scope, float? olderThanHours, int keepLatest, bool dryRun)
    {
        if (scope != "session" && scope != "all") throw new McpException("scope must be session or all.");
        if (keepLatest < 0) throw new McpException("keepLatest must be 0 or more.");
        var wanted = Check(kinds);
        if ((wanted == null || wanted.Length == 0) && (paths == null || paths.Length == 0)) throw new McpException("Give kinds or paths: nothing was selected.");
        if (!dryRun) await RefuseWhileBusy(bridge);

        var outputs = Scan();
        var keep = new HashSet<string>((keepPaths ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var chosen = new List<Output>();
        var refused = new JsonArray();
        foreach (var path in paths ?? [])
        {
            // A path must be something Scan recognises: never an arbitrary file, even inside this folder.
            var match = outputs.FirstOrDefault(o => string.Equals(o.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (match == null) refused.Add((JsonNode)new JsonObject { ["path"] = path, ["reason"] = "not a StudioMCP output" });
            else chosen.Add(match);
        }
        if (wanted != null)
            foreach (var kind in wanted)
            {
                var group = outputs.Where(o => o.Kind == kind).OrderByDescending(o => o.Modified).ToList();
                // Rollback copies are worth one spare, whatever was asked.
                var skip = kind == "backups" ? Math.Max(1, keepLatest) : keepLatest;
                foreach (var output in group.Skip(skip))
                {
                    if (scope == "session" && output.Modified < SessionStart) continue;
                    if (olderThanHours != null && output.Modified > DateTime.Now.AddHours(-olderThanHours.Value)) continue;
                    chosen.Add(output);
                }
            }
        chosen = chosen.Where(o => !keep.Contains(o.Path)).DistinctBy(o => o.Path).ToList();

        var removed = new JsonArray();
        long bytes = 0;
        var files = 0;
        foreach (var output in chosen)
        {
            try
            {
                if (!dryRun) Delete(output);
                bytes += output.Bytes;
                files += output.Files;
                if (removed.Count < 200) removed.Add((JsonNode)new JsonObject { ["kind"] = output.Kind, ["path"] = output.Path, ["bytes"] = output.Bytes });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                refused.Add((JsonNode)new JsonObject { ["path"] = output.Path, ["reason"] = error.Message });
            }
        }
        return new JsonObject { ["dryRun"] = dryRun, ["scope"] = scope, ["removedFiles"] = files, ["removedBytes"] = bytes, ["removed"] = removed,
            ["refused"] = refused, ["remainingBytes"] = dryRun ? outputs.Sum(o => o.Bytes) - bytes : Scan().Sum(o => o.Bytes) };
    }

    private static string[]? Check(string[]? kinds)
    {
        if (kinds == null) return null;
        foreach (var kind in kinds)
            if (!Kinds.Contains(kind)) throw new McpException("kinds may contain " + string.Join(", ", Kinds) + ".");
        return kinds;
    }

    private static void Delete(Output output)
    {
        if (!output.IsFolder)
        {
            File.Delete(output.Path);
            // A checkpoint's settings sidecar goes with it.
            if (output.Kind == "checkpoints" && File.Exists(output.Path + ".bridge.json")) File.Delete(output.Path + ".bridge.json");
            return;
        }
        // Junctions were filtered out by Scan, so this stays inside the output's own folder.
        Directory.Delete(output.Path, true);
    }

    private static async Task RefuseWhileBusy(BridgeClient bridge)
    {
        JsonObject? status = null;
        try { status = (await bridge.Call("status"))["result"]!.AsObject(); }
        catch (Exception error) when (error is McpException or IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException) { }
        if (status != null)
        {
            if (status["writesEnabled"]?.GetValue<bool>() == false) throw new McpException("Writes are disabled in bridge configuration, so nothing is deleted. dryRun still works.");
            var jobs = status["jobs"]?.AsObject();
            if (jobs != null && jobs.Any(job => job.Value?.GetValue<bool>() == true))
                throw new McpException("A capture, sample job or character load is running; clean up when it has finished.");
            return;
        }
        // The game is closed, so read the setting it would have used.
        var config = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Root()))!, "BepInEx", "config", "com.tomtom.studiomcp.cfg");
        if (File.Exists(config) && Regex.IsMatch(await File.ReadAllTextAsync(config), @"^AllowWrites\s*=\s*false", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            && Environment.GetEnvironmentVariable("STUDIOMCP_ALLOW_WRITES") != "1")
            throw new McpException("Writes are disabled in bridge configuration, so nothing is deleted. dryRun still works.");
    }
}
