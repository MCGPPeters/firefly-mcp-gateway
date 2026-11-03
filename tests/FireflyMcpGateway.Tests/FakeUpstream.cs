using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Yarp.ReverseProxy.Forwarder;

namespace FireflyMcpGateway.Tests;

/// <summary>Stands in for firefly-iii-mcp: records what the gateway sends and answers with a canned response.</summary>
public sealed class FakeUpstream : HttpMessageHandler
{
    private readonly List<(HttpRequestMessage Request, string? Body)> _requests = [];

    public Func<HttpResponseMessage> Respond { get; set; } = () => Json("{}");

    public (HttpRequestMessage Request, string? Body) LastRequest
    {
        get
        {
            lock (_requests)
            {
                return _requests[^1];
            }
        }
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage EventStream(byte[] body, int chunkSize)
    {
        var content = new StreamContent(new ChunkedStream(body, chunkSize));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_requests)
        {
            _requests.Add((request, body));
        }

        return Respond();
    }
}

public sealed class FakeUpstreamClientFactory(FakeUpstream upstream) : IForwarderHttpClientFactory
{
    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(upstream, disposeHandler: false);
}

/// <summary>A read-only stream that hands out at most <c>chunkSize</c> bytes per read, to exercise chunk boundaries.</summary>
public sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
