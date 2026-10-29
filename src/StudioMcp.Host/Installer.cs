using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudioMcp.Host;

// One-step install for the release package: copies the bridge and this host into the game, enables writes,
// and registers both MCP servers (Studio and maker) with the AI clients found on this machine.
public static class Installer
{
    private const string HostFolder = "StudioMCP-Host";
    private const string HostDll = "StudioMcp.Host.dll";
    // Earlier packages shipped a self-extracting single-file exe, which antivirus heuristics block.
    private const string LegacyHostExe = "StudioMcp.Host.exe";

    // KK and KKS use different server names and ports, so both games can be installed side by side.
    private sealed record Edition(string Title, string Plugin, string[] GameExes, string Api, string MaterialEditor,
        string Screencap, string StudioServer, string MakerServer)
    {
        public string[] Servers => new[] { StudioServer, MakerServer };
        public string ServerList => StudioServer + "、" + MakerServer;
    }

    private static readonly Edition[] Editions = {
        new("Koikatsu Sunshine", "KKS_StudioMCP.dll", new[] { "KoikatsuSunshine.exe" }, "KKSAPI.dll",
            "KKS_MaterialEditor.dll", "KKS_Screencap.dll", "studiomcp", "kksmaker"),
        new("Koikatsu", "KK_StudioMCP.dll", new[] { "Koikatu.exe", "Koikatsu Party.exe" }, "KKAPI.dll",
            "KK_MaterialEditor.dll", "Screencap.dll", "kkstudiomcp", "kkmaker") };

    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var uninstall = args[0] == "uninstall";
        try
        {
            var configRoot = Option(args, "--config-root");
            var home = configRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = configRoot != null ? Path.Combine(configRoot, "AppData", "Roaming")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // The package mirrors the game layout, so this also works when run from an installed StudioMCP-Host.
            var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var source = Path.Combine(package, "BepInEx", "plugins", "StudioMCP");
            var edition = Editions.FirstOrDefault(e => File.Exists(Path.Combine(source, e.Plugin)))
                ?? throw new FileNotFoundException("安裝包不完整：找不到 BepInEx/plugins/StudioMCP 裡的外掛 DLL。請重新解壓整個 zip。");
            var game = FindGame(Option(args, "--game"), package, edition);
            Console.WriteLine(edition.Title + " 遊戲資料夾：" + game);
            if (uninstall) Uninstall(game, home, appData, edition);
            else Install(game, home, appData, !args.Contains("--read-only"), source, edition);
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine();
            Console.WriteLine("失敗：" + (string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message));
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindGame(string? given, string package, Edition edition)
    {
        var hint = "需要 CharaStudio.exe、" + string.Join(" 或 ", edition.GameExes) + " 與 BepInEx";
        if (given != null)
        {
            if (IsGame(given, edition)) return Path.GetFullPath(given);
            throw new DirectoryNotFoundException("指定的資料夾不是 " + edition.Title + " 遊戲資料夾（" + hint + "）：" + given);
        }
        foreach (var candidate in new[] { Environment.CurrentDirectory, package, Path.GetDirectoryName(package) })
            if (!string.IsNullOrEmpty(candidate) && IsGame(candidate, edition)) return Path.GetFullPath(candidate);
        Console.WriteLine("找不到遊戲資料夾。請把 " + edition.Title + " 的遊戲資料夾（有 CharaStudio.exe 的那個）拖進這個視窗，然後按 Enter：");
        var typed = (Console.ReadLine() ?? "").Trim().Trim('"');
        if (!IsGame(typed, edition))
        {
            var other = Editions.FirstOrDefault(e => e != edition && IsGame(typed, e));
            throw new DirectoryNotFoundException(other != null
                ? "這是 " + other.Title + " 的資料夾，但這個安裝包是 " + edition.Title + " 版。請下載對應版本的安裝包。"
                : "這不是 " + edition.Title + " 遊戲資料夾（" + hint + "）：" + typed);
        }
        return Path.GetFullPath(typed);
    }

    private static bool IsGame(string folder, Edition edition) => File.Exists(Path.Combine(folder, "CharaStudio.exe"))
        && edition.GameExes.Any(exe => File.Exists(Path.Combine(folder, exe))) && Directory.Exists(Path.Combine(folder, "BepInEx", "plugins"));

    private static void Install(string game, string home, string appData, bool writes, string source, Edition edition)
    {
        CheckDependencies(game, edition);
        var backup = Path.Combine(game, "UserData", "StudioMCP", "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        var plugin = Path.Combine(game, "BepInEx", "plugins", "StudioMCP");
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(plugin), StringComparison.OrdinalIgnoreCase))
            foreach (var file in Directory.GetFiles(source))
                Replace(file, Path.Combine(plugin, Path.GetFileName(file)), backup);
        Console.WriteLine("✔ 已安裝遊戲外掛 → BepInEx/plugins/StudioMCP");

        // The host runs under Microsoft's signed dotnet.exe; no executable of our own is installed.
        var hostFolder = Path.Combine(game, HostFolder);
        var packageHost = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(packageHost, hostFolder, StringComparison.OrdinalIgnoreCase))
            foreach (var file in Directory.GetFiles(packageHost, "*", SearchOption.AllDirectories))
                Replace(file, Path.Combine(hostFolder, Path.GetRelativePath(packageHost, file)), backup);
        var legacy = Path.Combine(hostFolder, LegacyHostExe);
        if (File.Exists(legacy)) { Directory.CreateDirectory(backup); File.Move(legacy, Path.Combine(backup, LegacyHostExe + ".old"), true); }
        var host = Path.Combine(hostFolder, HostDll);
        var dotnet = Path.GetFileName(Environment.ProcessPath ?? "").Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? Environment.ProcessPath! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        Console.WriteLine("✔ 已安裝 MCP 伺服器 → " + HostFolder);

        if (writes) EnableWrites(game);
        Console.WriteLine(writes ? "✔ 已開啟寫入（AI 可以修改材質與場景）" : "・維持唯讀模式（--read-only）");

        var connections = Path.Combine(game, "UserData", "StudioMCP");
        var servers = new Dictionary<string, string> {
            [edition.StudioServer] = Path.Combine(connections, "connection.json"),
            [edition.MakerServer] = Path.Combine(connections, "maker-connection.json") };
        var registered = new List<string>();
        var claudeCode = Path.Combine(home, ".claude.json");
        if (File.Exists(claudeCode)) { WriteJson(claudeCode, dotnet, host, servers, claudeCode: true); registered.Add("Claude Code"); }
        var desktop = Path.Combine(appData, "Claude");
        if (Directory.Exists(desktop)) { WriteJson(Path.Combine(desktop, "claude_desktop_config.json"), dotnet, host, servers, claudeCode: false); registered.Add("Claude Desktop"); }
        var codex = Path.Combine(home, ".codex", "config.toml");
        if (File.Exists(codex)) { WriteToml(codex, dotnet, host, servers, edition); registered.Add("Codex"); }
        var example = Path.Combine(game, HostFolder, "mcp-config-example.json");
        File.WriteAllText(example, new JsonObject { ["mcpServers"] = Servers(dotnet, host, servers, claudeCode: false) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(registered.Count > 0 ? "✔ 已註冊 MCP（" + edition.ServerList + "）到：" + string.Join("、", registered)
            : "・沒有找到 Claude Code／Claude Desktop／Codex，請參考 " + example + " 手動設定");

        Console.WriteLine();
        Console.WriteLine("完成！接下來：");
        Console.WriteLine("  1. 重新啟動遊戲（Studio 或本體都可以）");
        Console.WriteLine("  2. 重新啟動 AI 工具（Claude／Codex），Studio 用 " + edition.StudioServer + "，本體 Maker 用 " + edition.MakerServer);
        if (Directory.Exists(backup)) Console.WriteLine("被取代的舊檔案備份在：" + backup);
    }

    private static void Uninstall(string game, string home, string appData, Edition edition)
    {
        var backup = Path.Combine(game, "UserData", "StudioMCP", "uninstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        // Clean the client configs first: once this single-file host is moved it cannot load further assemblies.
        var claudeCode = Path.Combine(home, ".claude.json");
        if (File.Exists(claudeCode)) RemoveJson(claudeCode, edition);
        var desktopConfig = Path.Combine(appData, "Claude", "claude_desktop_config.json");
        if (File.Exists(desktopConfig)) RemoveJson(desktopConfig, edition);
        var codex = Path.Combine(home, ".codex", "config.toml");
        if (File.Exists(codex))
        {
            var original = File.ReadAllText(codex);
            var stripped = StripToml(original, edition);
            if (stripped.TrimEnd() != original.Replace("\r\n", "\n").TrimEnd()) { Backup(codex); File.WriteAllText(codex, stripped); }
        }
        Console.WriteLine("✔ 已從 AI 工具移除 " + edition.ServerList);
        foreach (var folder in new[] { Path.Combine(game, "BepInEx", "plugins", "StudioMCP"), Path.Combine(game, HostFolder) })
        {
            if (!Directory.Exists(folder)) continue;
            // Loaded DLLs and a running host cannot be deleted or have their folder renamed,
            // but each file can be moved aside on the same drive.
            var saved = Path.Combine(backup, Path.GetFileName(folder));
            Directory.CreateDirectory(saved);
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                File.Move(file, Path.Combine(saved, Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '_')));
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
        Console.WriteLine("✔ 已移除遊戲外掛與 MCP 伺服器（移到 " + backup + "，可手動刪除）");
        Console.WriteLine("UserData/StudioMCP 裡的截圖與存檔點保留未刪。");
    }

    private static void CheckDependencies(string game, Edition edition)
    {
        var plugins = Path.Combine(game, "BepInEx", "plugins");
        bool Has(string pattern) => Directory.EnumerateFiles(plugins, pattern, SearchOption.AllDirectories).Any();
        if (!Has(edition.Api)) throw new FileNotFoundException("缺少 " + Path.GetFileNameWithoutExtension(edition.Api) + "（Modding API）。請先安裝 HF Patch 或 Modding API。");
        if (!Has(edition.MaterialEditor)) Console.WriteLine("⚠ 沒有找到 Material Editor：材質功能將無法使用。");
        if (!Has(edition.Screencap)) Console.WriteLine("⚠ 沒有找到 Screencap：截圖功能將無法使用。");
    }

    private static void Replace(string source, string target, string backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            // Unchanged dependencies stay in place, so an upgrade works while the game still has them loaded.
            if (Same(source, target)) return;
            Directory.CreateDirectory(backup);
            var saved = Path.Combine(backup, Path.GetFileName(target));
            if (File.Exists(saved)) saved += "." + Guid.NewGuid().ToString("N")[..6];
            // Move rather than overwrite: Windows usually allows moving a loaded DLL or running exe on the same drive.
            try { File.Move(target, saved); }
            catch (IOException)
            {
                throw new IOException(Path.GetFileName(target) + " 正在被使用，請先關閉遊戲與 Claude／Codex 後再執行安裝。");
            }
        }
        File.Copy(source, target, true);
    }

    private static bool Same(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var first = File.OpenRead(a);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return sha.ComputeHash(first).AsSpan().SequenceEqual(sha.ComputeHash(second));
    }

    private static void EnableWrites(string game)
    {
        var config = Path.Combine(game, "BepInEx", "config", "com.tomtom.studiomcp.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        if (!File.Exists(config)) { File.WriteAllText(config, "[Server]\n\nAllowWrites = true\n"); return; }
        var text = File.ReadAllText(config);
        var updated = Regex.Replace(text, @"^AllowWrites\s*=\s*\w+", "AllowWrites = true", RegexOptions.Multiline);
        if (updated == text && !Regex.IsMatch(text, @"^AllowWrites\s*=", RegexOptions.Multiline))
            updated = Regex.Replace(text, @"^\[Server\]\s*$", "[Server]\n\nAllowWrites = true", RegexOptions.Multiline);
        File.WriteAllText(config, updated);
    }

    private static JsonObject Servers(string dotnet, string host, Dictionary<string, string> servers, bool claudeCode)
    {
        var result = new JsonObject();
        foreach (var server in servers)
        {
            var entry = new JsonObject { ["command"] = dotnet, ["args"] = new JsonArray(host),
                ["env"] = new JsonObject { ["STUDIOMCP_CONNECTION"] = server.Value } };
            if (claudeCode) entry["type"] = "stdio";
            result[server.Key] = entry;
        }
        return result;
    }

    private static void WriteJson(string path, string dotnet, string host, Dictionary<string, string> servers, bool claudeCode)
    {
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        if (File.Exists(path)) Backup(path);
        if (root["mcpServers"] is not JsonObject existing) root["mcpServers"] = existing = new JsonObject();
        foreach (var server in Servers(dotnet, host, servers, claudeCode).ToList())
            existing[server.Key] = server.Value!.DeepClone();
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RemoveJson(string path, Edition edition)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (root["mcpServers"] is not JsonObject servers || !edition.Servers.Any(servers.ContainsKey)) return;
        Backup(path);
        foreach (var name in edition.Servers) servers.Remove(name);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteToml(string path, string dotnet, string host, Dictionary<string, string> servers, Edition edition)
    {
        Backup(path);
        var text = StripToml(File.ReadAllText(path), edition).TrimEnd() + "\n";
        foreach (var server in servers)
            text += $"\n[mcp_servers.{server.Key}]\ncommand = '{dotnet}'\nargs = ['{host}']\n\n[mcp_servers.{server.Key}.env]\nSTUDIOMCP_CONNECTION = '{server.Value}'\n";
        File.WriteAllText(path, text);
    }

    // Drops this edition's [mcp_servers.<name>] and [mcp_servers.<name>.*] tables, keeping everything else.
    private static string StripToml(string text, Edition edition)
    {
        var output = new StringBuilder();
        var skipping = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var header = Regex.Match(line, @"^\s*\[\s*mcp_servers\.([A-Za-z0-9_-]+)(\.[^\]]*)?\s*\]");
            if (Regex.IsMatch(line, @"^\s*\[")) skipping = header.Success && edition.Servers.Contains(header.Groups[1].Value);
            if (!skipping) output.Append(line).Append('\n');
        }
        return output.ToString().TrimEnd('\n') + "\n";
    }

    // Client configs can hold personal settings, so their backups stay beside the original file.
    private static void Backup(string path)
        => File.Copy(path, path + ".bak-studiomcp-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
}
