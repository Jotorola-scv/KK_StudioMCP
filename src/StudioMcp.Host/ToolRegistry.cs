// Added 2026-10-30 in the maker-support fork of KK_StudioMCP (AGPL-3.0); see README.md.
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace StudioMcp.Host;

/// <summary>The tool only works in Studio; hidden from the maker server.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StudioOnlyAttribute : Attribute { }

/// <summary>The tool only works in the character maker; hidden from the Studio server.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MakerOnlyAttribute : Attribute { }

/// <summary>
/// JSON schema for a free-form JSON parameter. The SDK emits `true` (any value) for JsonElement, which some clients
/// turn into a string field; an explicit schema keeps them sending objects. Strings holding JSON are still accepted.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class JsonSchemaAttribute(string schema) : Attribute
{
    public string Schema { get; } = schema;
}

public static class ToolRegistry
{
    /// <summary>studio or maker, from the connection file this server talks to.</summary>
    public static string Mode { get; } = DetectMode();
    public static int Exposed { get; private set; }
    public static string Version { get; } = typeof(ToolRegistry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";
    public static string Build { get; } = HashSelf();

    /// <summary>Sent to the client at connection, so agents know the workflow and that disk space is theirs to manage.</summary>
    public static string Instructions => (Mode == "maker"
        ? "Koikatsu character maker control. Typical porting session: game_launch, maker_enter, character_load, coordinate_load; inspect and fix; coordinate_save.\n"
        : "CharaStudio control. Start with game_launch if the game is closed, then scene_snapshot.\n")
        + "Disk space is yours to manage: captures, mesh exports, rig samples and hair mask previews stay in UserData/StudioMCP until deleted, and a sample job can reach gigabytes. "
        + "studio_status host.outputs shows the totals. Use outputs_cleanup without being asked: delete a capture or mask preview once you have looked at it and will not cite its file, "
        + "delete a mesh export or sample job once you have read what you need from it, always before a large sample job when space is short, and everything left from this session when the task ends. "
        + "Keep files the user asked for or that your report points at (keepPaths). The default scope only touches this session's outputs; "
        + "use scope=all only when the user asked to free space or the outputs are clearly yours, because older exports may be another agent's work. Delete checkpoints only when you will not need to restore them. "
        + "Saved cards in UserData/chara and UserData/coordinate are never outputs: do not delete those.";

    private static string DetectMode()
    {
        var file = Environment.GetEnvironmentVariable("STUDIOMCP_CONNECTION");
        if (string.IsNullOrEmpty(file)) return "studio";
        if (Path.GetFileName(file).Equals("maker-connection.json", StringComparison.OrdinalIgnoreCase)) return "maker";
        try { return JsonNode.Parse(File.ReadAllText(file))?["mode"]?.GetValue<string>() ?? "studio"; }
        catch (Exception) { return "studio"; }
    }

    private static string HashSelf()
    {
        try { return Convert.ToHexString(MD5.HashData(File.ReadAllBytes(typeof(ToolRegistry).Assembly.Location)))[..12].ToLowerInvariant(); }
        catch (Exception) { return "unknown"; }
    }

    /// <summary>Registers the tools that work in this server's mode (STUDIOMCP_TOOLS=all shows every tool) with fixed schemas.</summary>
    public static IMcpServerBuilder WithModeTools(this IMcpServerBuilder builder)
    {
        var all = Environment.GetEnvironmentVariable("STUDIOMCP_TOOLS") == "all";
        foreach (var method in new[] { typeof(StudioTools), typeof(AnimationTools) }.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() == null) continue;
            if (!all && Mode == "maker" && method.GetCustomAttribute<StudioOnlyAttribute>() != null) continue;
            if (!all && Mode == "studio" && method.GetCustomAttribute<MakerOnlyAttribute>() != null) continue;
            Exposed++;
            builder.Services.AddSingleton(services =>
            {
                var tool = McpServerTool.Create(method, (object?)null, new McpServerToolCreateOptions { Services = services });
                FixSchema(tool, method);
                return tool;
            });
        }
        return builder;
    }

    private static void FixSchema(McpServerTool tool, MethodInfo method)
    {
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        if (schema["properties"] is not JsonObject properties) return;
        var changed = false;
        foreach (var parameter in method.GetParameters())
        {
            var custom = parameter.GetCustomAttribute<JsonSchemaAttribute>();
            if (custom == null || parameter.Name == null) continue;
            var key = properties.Select(p => p.Key).FirstOrDefault(k => k.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (key == null) continue;
            var replacement = JsonNode.Parse(custom.Schema)!.AsObject();
            if (properties[key] is JsonObject old && old["default"] != null) replacement["default"] = old["default"]!.DeepClone();
            properties[key] = replacement;
            changed = true;
        }
        if (changed) tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }
}
