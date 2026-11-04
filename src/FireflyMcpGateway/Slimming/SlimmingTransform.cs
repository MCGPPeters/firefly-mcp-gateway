using Yarp.ReverseProxy.Transforms;

namespace FireflyMcpGateway.Slimming;

/// <summary>
/// The YARP response transform that runs tool results through <see cref="ResultSlimmer"/>.
/// </summary>
/// <remarks>
/// Only successful, uncompressed responses to POST are touched: that is where tools/call results come back,
/// as JSON or as an event stream. GET streams (server notifications, resumption) are proxied as before,
/// because YARP's own copy is what keeps a long-lived stream's activity timeout alive.
/// </remarks>
public static class SlimmingTransform
{
    public static async ValueTask ApplyAsync(ResponseTransformContext context, ResultSlimmer slimmer)
    {
        var response = context.ProxyResponse;
        var http = context.HttpContext;

        if (!slimmer.Enabled || response is null || !HttpMethods.IsPost(http.Request.Method))
        {
            return;
        }

        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentEncoding.Count > 0)
        {
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var cancellationToken = http.RequestAborted;

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            context.SuppressResponseBody = true;
            http.Response.ContentLength = null;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await SseRewriter.RewriteAsync(source, http.Response.Body, slimmer, cancellationToken);
        }
        else if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.SuppressResponseBody = true;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffered = new MemoryStream();
            var complete = await CopyUpToAsync(source, buffered, slimmer.MaxMessageBytes, cancellationToken);
            var original = buffered.GetBuffer().AsMemory(0, (int)buffered.Length);

            if (!complete)
            {
                // Too large to slim: forward it unchanged. The upstream's headers were copied already.
                await http.Response.Body.WriteAsync(original, cancellationToken);
                await source.CopyToAsync(http.Response.Body, cancellationToken);
                return;
            }

            ReadOnlyMemory<byte> body = slimmer.SlimMessage(original.Span) ?? original;
            http.Response.ContentLength = body.Length;
            await http.Response.Body.WriteAsync(body, cancellationToken);
        }
    }

    /// <summary>Copies until the source ends (true) or more than <paramref name="limit"/> bytes were read (false).</summary>
    private static async Task<bool> CopyUpToAsync(Stream source, MemoryStream destination, int limit, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        int read;

        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            destination.Write(chunk, 0, read);

            if (destination.Length > limit)
            {
                return false;
            }
        }

        return true;
    }
}
