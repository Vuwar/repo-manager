using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace RepoManager.App.Daemon;

/// <summary>Serves the React build embedded in the assembly (wwwroot/**), with index.html as SPA fallback.</summary>
public static class Ui
{
    public static void Map(WebApplication app)
    {
        IFileProvider files;
        try { files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot"); }
        catch (InvalidOperationException) { files = new NullFileProvider(); }
        var types = new FileExtensionContentTypeProvider();

        app.MapGet("/{**path}", async (HttpContext ctx, string? path) =>
        {
            path = string.IsNullOrEmpty(path) ? "index.html" : path;
            var file = files.GetFileInfo(path);
            if (!file.Exists || file.IsDirectory)
            {
                if (Path.HasExtension(path)) { ctx.Response.StatusCode = 404; return; }
                file = files.GetFileInfo("index.html");
                path = "index.html";
            }
            if (!file.Exists)
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("RepoManager UI is not built. Run: npm run build --prefix web");
                return;
            }
            ctx.Response.ContentType = types.TryGetContentType(path, out var ct) ? ct : "application/octet-stream";
            ctx.Response.Headers.CacheControl = path == "index.html" ? "no-cache" : "public, max-age=31536000, immutable";
            await using var s = file.CreateReadStream();
            await s.CopyToAsync(ctx.Response.Body);
        });
    }
}
