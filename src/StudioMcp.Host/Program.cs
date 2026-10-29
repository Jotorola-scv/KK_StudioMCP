using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StudioMcp.Host;

if (args.Length > 0 && (args[0] == "install" || args[0] == "uninstall")) return Installer.Run(args);
if (args.Length == 0 && !Console.IsInputRedirected)
{
    // Double-clicked: an MCP client starts this server itself, so point people at the installer instead of waiting on stdin.
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("這是 StudioMCP 的 MCP 伺服器，由 Claude／Codex 自動啟動，不需要手動開啟。");
    Console.WriteLine("安裝請執行安裝包裡的 Install.bat。按 Enter 關閉。");
    Console.ReadLine();
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<BridgeClient>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
return 0;
