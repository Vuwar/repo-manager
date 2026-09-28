using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoManager.Contracts;

public static class Protocol
{
    public const int DefaultPort = 4000;
    public const string TokenHeader = "X-RepoManager-Token";
    public const string Version = "1.0.0";

    public static readonly JsonSerializerOptions Json = CreateJson();

    public static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>
    /// Folder holding registry.json, state.json, logs, token and daemon.json.
    /// REPOMANAGER_HOME overrides it (used by tests and side-by-side installs).
    /// </summary>
    public static string DataDir()
    {
        var overridden = Environment.GetEnvironmentVariable("REPOMANAGER_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoManager");
    }

    public static string TokenPath(string dataDir) => Path.Combine(dataDir, "token");

    /// <summary>Written by the daemon while it runs: port and pid, so clients can find it.</summary>
    public static string DaemonInfoPath(string dataDir) => Path.Combine(dataDir, "daemon.json");
}
