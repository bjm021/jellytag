using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTag.Middleware;

/// <summary>
/// Middleware that adds the JellyTag detail page script to the web client's index.html.
/// </summary>
public class WebInjectionMiddleware
{
    private const string Marker = "JellyTag/detail.js";

    private readonly RequestDelegate _next;
    private readonly ILogger<WebInjectionMiddleware> _logger;

    public WebInjectionMiddleware(RequestDelegate next, ILogger<WebInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration;
        var path = context.Request.Path.Value;
        if (config == null || !config.Enabled || !config.ShowOnDetailPage
            || !HttpMethods.IsGet(context.Request.Method)
            || !TryGetBasePrefix(path, out var prefix))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The body is edited in place, so it must be uncompressed and never a 304/206
        var headers = context.Request.Headers;
        headers.Remove("Accept-Encoding");
        headers.Remove("If-None-Match");
        headers.Remove("If-Modified-Since");
        headers.Remove("Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            buffer.Position = 0;
            var isHtml = context.Response.StatusCode == 200
                && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

            if (isHtml)
            {
                var html = Encoding.UTF8.GetString(buffer.ToArray());
                var closeIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closeIndex >= 0 && !html.Contains(Marker, StringComparison.Ordinal))
                {
                    var tag = $"<script src=\"{prefix}/{Marker}\" defer></script>";
                    var bytes = Encoding.UTF8.GetBytes(html.Insert(closeIndex, tag));
                    context.Response.ContentLength = bytes.Length;
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    await originalBody.WriteAsync(bytes).ConfigureAwait(false);
                    return;
                }
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to inject JellyTag script into web client");
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>
    /// Matches the web client entry page (/web, /web/, /web/index.html) and returns whatever precedes "/web" (the base URL).
    /// </summary>
    private static bool TryGetBasePrefix(string? path, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var trimmed = path.TrimEnd('/');
        if (trimmed.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/index.html".Length];
        }

        if (!trimmed.EndsWith("/web", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        prefix = trimmed[..^"/web".Length];
        return true;
    }
}
