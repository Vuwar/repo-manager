using System.Text.RegularExpressions;

namespace RepoManager.Core.Config;

/// <summary>Resolves ${port:svc}, ${root} and ${env:NAME} in config values.</summary>
public static partial class Variables
{
    [GeneratedRegex(@"\$\{(?<kind>[a-zA-Z]+)(?::(?<arg>[^}]*))?\}")]
    private static partial Regex Pattern();

    public sealed record Reference(string Kind, string? Arg, string Raw);

    public static IEnumerable<Reference> Find(string? value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        foreach (Match m in Pattern().Matches(value))
            yield return new Reference(m.Groups["kind"].Value.ToLowerInvariant(), m.Groups["arg"].Success ? m.Groups["arg"].Value : null, m.Value);
    }

    public static IEnumerable<string> AllValues(EffectiveService s)
    {
        yield return s.Command;
        foreach (var a in s.Args) yield return a;
        foreach (var v in s.Env.Values) yield return v;
        yield return s.Cwd;
        if (s.Url != null) yield return s.Url;
        if (s.Health != null) yield return s.Health;
        foreach (var p in s.Prepare) yield return p.Run;
    }

    public sealed class Context
    {
        public required string Root { get; init; }
        public required IReadOnlyDictionary<string, int> Ports { get; init; }
        public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    }

    public static string Resolve(string value, Context ctx)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${")) return value;
        return Pattern().Replace(value, m =>
        {
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var arg = m.Groups["arg"].Success ? m.Groups["arg"].Value : null;
            return kind switch
            {
                "root" => ctx.Root,
                "port" when arg != null && ctx.Ports.TryGetValue(arg, out var p) => p.ToString(),
                "env" when arg != null => ctx.Env(arg) ?? "",
                _ => m.Value,
            };
        });
    }
}
