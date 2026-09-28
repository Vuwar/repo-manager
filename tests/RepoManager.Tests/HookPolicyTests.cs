using System.Text.Json;
using RepoManager.Contracts;
using RepoManager.Core.Hooks;

namespace RepoManager.Tests;

public class HookPolicyTests
{
    private const string Root = @"C:\repos\panel-pro";

    private static readonly ManagedServiceInfo Api = new()
    {
        Id = "panel-pro/api", Name = "api", InstanceKey = "panel-pro", Root = Root, Cwd = Root,
        Up = true, Active = true, Port = 5080, Pids = [4242, 4243], ProcessNames = ["cmd", "dotnet", "panelpro.api"],
        Command = "dotnet", Args = ["run", "--project", "src/PanelPro.Api", "--launch-profile", "http"],
        Url = "http://127.0.0.1:5080",
    };

    private static readonly ManagedServiceInfo Web = new()
    {
        Id = "panel-pro/web", Name = "web", InstanceKey = "panel-pro", Root = Root, Cwd = Root,
        Up = true, Active = true, Port = 5173, Pids = [5151, 5152], ProcessNames = ["cmd", "node"],
        Command = "npm", Args = ["run", "dev", "--prefix", "web"], Url = "http://localhost:5173",
    };

    private static readonly ManagedServiceInfo[] All = [Api, Web];

    private static HookDecisionDto Shell(string command, string cwd = Root, ManagedServiceInfo[]? services = null) =>
        HookPolicy.Evaluate(new HookRequest
        {
            ToolName = "PowerShell",
            ToolInputJson = JsonSerializer.Serialize(new { command }),
            Cwd = cwd,
        }, services ?? All);

    [Theory]
    [InlineData("taskkill /PID 4242 /T /F")]
    [InlineData("taskkill /F /IM node.exe")]
    [InlineData("Stop-Process -Id 5152 -Force")]
    [InlineData("Stop-Process -Name dotnet")]
    [InlineData("Get-Process node | Stop-Process -Force")]
    [InlineData("npx kill-port 5173")]
    [InlineData("Get-NetTCPConnection -LocalPort 5080 | ForEach-Object { Stop-Process -Id $_.OwningProcess }")]
    [InlineData("pkill -f node")]
    [InlineData("killall dotnet")]
    [InlineData("kill -9 4243")]
    [InlineData("for /f \"tokens=5\" %a in ('netstat -ano ^| findstr :5173') do taskkill /PID %a /F")]
    public void Kills_of_managed_processes_are_denied(string command)
    {
        var d = Shell(command);
        Assert.True(d.Deny, command);
        Assert.Contains("devm", d.Reason);
    }

    [Theory]
    [InlineData("taskkill /PID 999 /F")]
    [InlineData("Stop-Process -Name notepad")]
    [InlineData("npx kill-port 3000")]
    [InlineData("git log --oneline -5")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test")]
    [InlineData("npm run build --prefix web")]
    [InlineData("npm ci --prefix web")]
    [InlineData("devm restart api")]
    [InlineData("echo kill")]
    public void Unrelated_commands_are_allowed(string command)
    {
        Assert.False(Shell(command).Deny, command);
    }

    [Theory]
    [InlineData("npm run dev --prefix web", Root)]
    [InlineData("cd web && npm run dev", Root)]
    [InlineData("npm run dev", Root + @"\web")]
    [InlineData("dotnet run --project src/PanelPro.Api --launch-profile http", Root)]
    [InlineData("dotnet run --project src\\PanelPro.Api\\PanelPro.Api.csproj", Root)]
    [InlineData("dotnet watch run", Root + @"\src\PanelPro.Api")]
    [InlineData("cd /c/repos/panel-pro/web; npm run dev", @"C:\somewhere")]
    public void Direct_starts_of_managed_services_are_denied(string command, string cwd)
    {
        var d = Shell(command, cwd);
        Assert.True(d.Deny, command);
        Assert.Contains("devm start", d.Reason);
    }

    [Theory]
    [InlineData("dotnet run --project tools/Harness", Root)]
    [InlineData("npm run dev", Root + @"\other")]
    [InlineData("npm run dev --prefix web", @"C:\repos\another-project")]
    public void Starts_of_other_things_are_allowed(string command, string cwd)
    {
        Assert.False(Shell(command, cwd).Deny, command);
    }

    [Fact]
    public void Nothing_is_denied_when_no_service_runs()
    {
        var stopped = All.Select(s => s with { Up = false, Active = false, Pids = [] }).ToArray();
        Assert.False(Shell("taskkill /F /IM node.exe", Root, stopped).Deny);
    }

    [Fact]
    public void Preview_start_by_name_is_denied_with_running_url()
    {
        var d = HookPolicy.Evaluate(new HookRequest
        {
            ToolName = "mcp__Claude_Browser__preview_start",
            ToolInputJson = JsonSerializer.Serialize(new { name = "web" }),
            Cwd = Root,
        }, All);
        Assert.True(d.Deny);
        Assert.Contains("http://localhost:5173", d.Reason);
    }

    [Fact]
    public void Preview_start_by_url_is_allowed()
    {
        var d = HookPolicy.Evaluate(new HookRequest
        {
            ToolName = "mcp__Claude_Browser__preview_start",
            ToolInputJson = JsonSerializer.Serialize(new { url = "http://localhost:5173" }),
            Cwd = Root,
        }, All);
        Assert.False(d.Deny);
    }

    [Fact]
    public void Garbage_input_is_allowed()
    {
        Assert.False(HookPolicy.Evaluate(new HookRequest { ToolName = "Bash", ToolInputJson = "{not json", Cwd = Root }, All).Deny);
        Assert.False(HookPolicy.Evaluate(new HookRequest { ToolName = "Read", ToolInputJson = "{}", Cwd = Root }, All).Deny);
    }
}
