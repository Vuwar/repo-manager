using RepoManager.Core.Config;
using RepoManager.Core.Git;

namespace RepoManager.Tests;

public class ConfigTests
{
    private static LaunchJsonFile Launch(params LaunchConfiguration[] c) => new() { Configurations = [.. c] };

    [Fact]
    public void Layers_merge_field_by_field_with_higher_layer_winning()
    {
        var launch = Launch(new LaunchConfiguration { Name = "api", RuntimeExecutable = "dotnet", RuntimeArgs = ["run"], Port = 5080 });
        var dev = new RepoConfigFile
        {
            Services = { ["api"] = new ServiceConfig { Health = "http://127.0.0.1:${port:api}/health", Env = new() { ["A"] = "1", ["B"] = "dev" } } },
        };
        var central = new RepoConfigFile
        {
            Services = { ["api"] = new ServiceConfig { Port = 6000, Env = new() { ["B"] = "central" } } },
        };

        var cfg = ConfigLoader.Merge("p", @"C:\p", null, launch, dev, central);
        var api = cfg.Services["api"];

        Assert.Equal("dotnet", api.Command);
        Assert.Equal(["run"], api.Args);
        Assert.Equal(6000, api.Port);
        Assert.Equal("1", api.Env["A"]);
        Assert.Equal("central", api.Env["B"]);
        Assert.Equal(Layers.LaunchJson, api.Origins["command"]);
        Assert.Equal(Layers.Central, api.Origins["port"]);
        Assert.Equal(Layers.DevServers, api.Origins["health"]);
        Assert.Equal("devservers.json+central", api.Origins["env"]);
        Assert.Equal(Layers.Default, api.Origins["cwd"]);
    }

    [Fact]
    public void Args_are_replaced_not_merged_and_new_services_are_allowed()
    {
        var launch = Launch(new LaunchConfiguration { Name = "web", RuntimeExecutable = "npm", RuntimeArgs = ["run", "dev"] });
        var dev = new RepoConfigFile
        {
            Services =
            {
                ["web"] = new ServiceConfig { Args = ["run", "dev", "--", "--port", "${port:web}"], AutoPort = true },
                ["worker"] = new ServiceConfig { Command = "node", Args = ["worker.js"] },
            },
        };
        var cfg = ConfigLoader.Merge("p", @"C:\p", null, launch, dev, null);
        Assert.Equal(["run", "dev", "--", "--port", "${port:web}"], cfg.Services["web"].Args);
        Assert.True(cfg.Services["web"].UsesPortVariable);
        Assert.Equal(["web", "worker"], cfg.Services.Keys);
    }

    [Fact]
    public void Validation_reports_unknown_dependency_cycle_and_bad_variables()
    {
        var dev = new RepoConfigFile
        {
            Services =
            {
                ["a"] = new ServiceConfig { Command = "x", DependsOn = ["b"] },
                ["b"] = new ServiceConfig { Command = "x", DependsOn = ["a"] },
                ["c"] = new ServiceConfig { Command = "x", DependsOn = ["nope"], Args = ["${port:a}", "${bogus}"] },
                ["d"] = new ServiceConfig { Args = ["x"] },
            },
        };
        var cfg = ConfigLoader.Merge("p", @"C:\p", null, null, dev, null);
        ConfigLoader.Validate(cfg);

        Assert.Contains(cfg.Errors, e => e.Contains("unknown service 'nope'"));
        Assert.Contains(cfg.Errors, e => e.StartsWith("dependency cycle"));
        Assert.Contains(cfg.Errors, e => e.Contains("${port:a}") && e.Contains("no port"));
        Assert.Contains(cfg.Errors, e => e.Contains("unknown variable ${bogus}"));
        Assert.Contains(cfg.Errors, e => e.Contains("'d' has no command"));
        Assert.False(cfg.Valid);
    }

    [Fact]
    public void Start_order_puts_dependencies_first()
    {
        var dev = new RepoConfigFile
        {
            Services =
            {
                ["web"] = new ServiceConfig { Command = "x", DependsOn = ["api"] },
                ["api"] = new ServiceConfig { Command = "x", DependsOn = ["db"] },
                ["db"] = new ServiceConfig { Command = "x" },
                ["other"] = new ServiceConfig { Command = "x" },
            },
        };
        var cfg = ConfigLoader.Merge("p", @"C:\p", null, null, dev, null);
        Assert.Equal(["db", "api", "web"], ConfigLoader.StartOrder(cfg, ["web"]));
    }

    [Theory]
    [InlineData("exists(web/node_modules)", false, "web/node_modules")]
    [InlineData("!exists(web/node_modules)", true, "web/node_modules")]
    [InlineData(" ! exists( 'a b' ) ", true, "a b")]
    public void Prepare_conditions_parse(string input, bool negate, string path)
    {
        var c = ConfigLoader.ParseCondition(input);
        Assert.NotNull(c);
        Assert.Equal(negate, c!.Value.Negate);
        Assert.Equal(path, c.Value.Path);
    }

    [Fact]
    public void Invalid_prepare_condition_is_an_error()
    {
        Assert.Null(ConfigLoader.ParseCondition("file(x)"));
    }

    [Fact]
    public void Variables_resolve_ports_root_and_env()
    {
        var ctx = new Variables.Context
        {
            Root = @"C:\repo",
            Ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["api"] = 5080 },
            Env = n => n == "HOME" ? "/h" : null,
        };
        Assert.Equal(@"http://127.0.0.1:5080 C:\repo /h  ${port:web}",
            Variables.Resolve("http://127.0.0.1:${port:api} ${root} ${env:HOME} ${env:NOPE} ${port:web}", ctx));
    }

    [Fact]
    public void Real_launch_json_shape_is_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude", "launch.json"), """
            {
              "version": "0.0.1",
              "configurations": [
                { "name": "api", "runtimeExecutable": "dotnet", "runtimeArgs": ["run", "--project", "src/Api"], "port": 5080, "url": "http://127.0.0.1:5080" },
                { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"], "port": 5173, "autoPort": true },
              ]
            }
            """);
        try
        {
            var cfg = ConfigLoader.Load("p", dir, null, null);
            Assert.True(cfg.Valid, string.Join("; ", cfg.Errors));
            Assert.Equal(5080, cfg.Services["api"].Port);
            Assert.Equal("http://127.0.0.1:5080", cfg.Services["api"].Url);
            Assert.True(cfg.Services["web"].AutoPort);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Broken_json_is_reported_not_thrown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "devservers.json"), "{ \"services\": { ");
        try
        {
            var cfg = ConfigLoader.Load("p", dir, null, null);
            Assert.False(cfg.Valid);
            Assert.Contains(cfg.Errors, e => e.StartsWith("devservers.json"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Git_status_parses_branch_tracking_and_dirty()
    {
        var info = GitService.ParseStatus("## main...origin/main [ahead 2, behind 1]\n M file.cs\n");
        Assert.Equal("main", info.Branch);
        Assert.Equal("origin/main", info.Upstream);
        Assert.Equal(2, info.Ahead);
        Assert.Equal(1, info.Behind);
        Assert.True(info.Dirty);

        var clean = GitService.ParseStatus("## feature/x\n");
        Assert.Equal("feature/x", clean.Branch);
        Assert.False(clean.Dirty);
    }

    [Fact]
    public void Git_worktree_list_excludes_main_and_missing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "rm-tests", "wt-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        try
        {
            var output = $"worktree C:/repo\nHEAD abc\nbranch refs/heads/main\n\nworktree {tmp.Replace('\\', '/')}\nHEAD def\nbranch refs/heads/feat-x\n\nworktree C:/gone\nHEAD 123\nbranch refs/heads/old\nprunable gitdir file points to non-existent location\n\n";
            var list = GitService.ParseWorktrees(output, @"C:\repo");
            var wt = Assert.Single(list);
            Assert.Equal(Path.GetFileName(tmp), wt.Name);
            Assert.Equal("feat-x", wt.Branch);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Ids_parse_worktree_instances()
    {
        Assert.True(Ids.TryParse("panel-pro@feat-x/api", out var p, out var w, out var n));
        Assert.Equal(("panel-pro", "feat-x", "api"), (p, w, n));
        Assert.True(Ids.TryParse("panel-pro/web", out p, out w, out n));
        Assert.Null(w);
        Assert.False(Ids.TryParse("api", out _, out _, out _));
    }

    [Fact]
    public void Folder_list_skips_hidden_and_build_folders_and_marks_projects()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm-tests", "f-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var d in new[] { "src/Api/bin/Debug", "src/Api/obj", "web/node_modules/x", ".git/objects", "docs" })
            Directory.CreateDirectory(Path.Combine(dir, d));
        File.WriteAllText(Path.Combine(dir, "src", "Api", "Api.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "web", "package.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "App.sln"), "");
        try
        {
            var list = FolderLister.List(dir);
            Assert.Equal([".", "docs", "src", "src/Api", "web"], list.Select(f => f.Path));
            Assert.Equal(["sln"], list[0].Markers);
            Assert.Equal(["csproj"], list.Single(f => f.Path == "src/Api").Markers);
            Assert.Equal(["package.json"], list.Single(f => f.Path == "web").Markers);
            Assert.Empty(FolderLister.List(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
