namespace DbEmulator.Hosting;

/// <summary>The app's web UI (Gui/ui/*), embedded in the exe. Used by the desktop window and by /_emulator/.</summary>
internal static class UiAssets
{
    public static bool TryGet(string path, out byte[] content, out string contentType)
    {
        content = [];
        contentType = "application/octet-stream";
        path = path.TrimStart('/');
        if (path.Length == 0)
        {
            path = "index.html";
        }

        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream("ui/" + path)
            ?? (path == "app.ico" ? typeof(UiAssets).Assembly.GetManifestResourceStream("app.ico") : null);
        if (stream is null)
        {
            return false;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        content = buffer.ToArray();
        contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
        return true;
    }
}
