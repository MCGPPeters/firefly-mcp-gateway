using System.Buffers;
using System.IO.Pipelines;

namespace FireflyMcpGateway.Slimming;

/// <summary>
/// Copies a server-sent event stream event by event and slims the JSON-RPC message in each event's data.
/// </summary>
/// <remarks>
/// firefly-iii-mcp answers a tools/call over Streamable HTTP with an event stream, so this is where the
/// results actually travel. Only an event's data changes: its <c>event</c>, <c>id</c> and <c>retry</c>
/// fields and comments are copied as they are, which keeps resumption by Last-Event-ID working. An event
/// that does not change, is larger than <see cref="ResultSlimmer.MaxMessageBytes"/>, or is cut off by the
/// end of the stream is copied byte for byte. Each event is flushed as soon as it is complete.
/// Lines may end in LF or CRLF; a lone CR, which the event-stream format also allows, is not split on.
/// </remarks>
public static class SseRewriter
{
    private static readonly byte[] DataField = "data:"u8.ToArray();
    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] Lf = "\n"u8.ToArray();
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();

    public static async Task RewriteAsync(
        Stream source, Stream destination, ResultSlimmer slimmer, CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(source);
        var lines = new List<byte[]>();
        var pending = 0L;
        var scanned = 0L;
        var passthrough = false; // the current event is too large to slim: copy it as it arrives
        var midLine = false;     // the start of the current line has already been copied

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;

                while (TryReadLine(ref buffer, ref scanned, out var line))
                {
                    var raw = line.ToArray();

                    if (passthrough)
                    {
                        await destination.WriteAsync(raw, cancellationToken);
                        var endsEvent = !midLine && IsBlank(raw);
                        midLine = false;

                        if (endsEvent)
                        {
                            passthrough = false;
                            await destination.FlushAsync(cancellationToken);
                        }

                        continue;
                    }

                    lines.Add(raw);
                    pending += raw.Length;

                    if (IsBlank(raw))
                    {
                        await WriteEventAsync(lines, destination, slimmer, cancellationToken);
                        lines.Clear();
                        pending = 0;
                    }
                    else if (pending > slimmer.MaxMessageBytes)
                    {
                        await WriteRawAsync(lines, destination, cancellationToken);
                        lines.Clear();
                        pending = 0;
                        passthrough = true;
                    }
                }

                if (result.IsCompleted)
                {
                    // The stream ended inside an event: copy what is left as it is.
                    await WriteRawAsync(lines, destination, cancellationToken);
                    await WriteAsync(buffer, destination, cancellationToken);
                    reader.AdvanceTo(buffer.End);
                    break;
                }

                if (pending + buffer.Length > slimmer.MaxMessageBytes)
                {
                    // A single line beyond the limit: stop holding this event back.
                    await WriteRawAsync(lines, destination, cancellationToken);
                    await WriteAsync(buffer, destination, cancellationToken);
                    lines.Clear();
                    pending = 0;
                    scanned = 0;
                    passthrough = true;
                    midLine = true;
                    reader.AdvanceTo(buffer.End);
                    continue;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
            }

            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    /// <summary>
    /// Takes the next complete line, line terminator included, off the front of the buffer. Only bytes not
    /// searched on an earlier call are searched, so an event arriving in many small reads stays linear.
    /// </summary>
    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, ref long scanned, out ReadOnlySequence<byte> line)
    {
        var position = buffer.Slice(scanned).PositionOf((byte)'\n');

        if (position is null)
        {
            scanned = buffer.Length;
            line = default;
            return false;
        }

        var end = buffer.GetPosition(1, position.Value);
        line = buffer.Slice(0, end);
        buffer = buffer.Slice(end);
        scanned = 0;
        return true;
    }

    private static async Task WriteEventAsync(
        List<byte[]> lines, Stream destination, ResultSlimmer slimmer, CancellationToken cancellationToken)
    {
        var payload = DataPayload(lines);
        var slimmed = payload is null ? null : slimmer.SlimMessage(payload);

        if (slimmed is null)
        {
            await WriteRawAsync(lines, destination, cancellationToken);
        }
        else
        {
            var written = false;

            foreach (var line in lines)
            {
                if (!IsDataLine(line))
                {
                    await destination.WriteAsync(line, cancellationToken);
                }
                else if (!written)
                {
                    // Compact JSON holds no raw line breaks, so one data line carries the whole message.
                    await destination.WriteAsync(DataPrefix, cancellationToken);
                    await destination.WriteAsync(slimmed, cancellationToken);
                    await destination.WriteAsync(EndsWithCrLf(line) ? CrLf : Lf, cancellationToken);
                    written = true;
                }
            }
        }

        await destination.FlushAsync(cancellationToken);
    }

    /// <summary>The event's data: the values of its data lines joined by line feeds, or null when it has none.</summary>
    private static byte[]? DataPayload(List<byte[]> lines)
    {
        List<byte[]>? values = null;

        foreach (var line in lines)
        {
            if (!IsDataLine(line))
            {
                continue;
            }

            var value = line.AsSpan(DataField.Length, line.Length - DataField.Length - TerminatorLength(line));

            if (value.Length > 0 && value[0] == (byte)' ')
            {
                value = value[1..];
            }

            (values ??= new List<byte[]>()).Add(value.ToArray());
        }

        if (values is null)
        {
            return null;
        }

        var payload = new byte[values.Sum(value => value.Length) + values.Count - 1];
        var offset = 0;

        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                payload[offset++] = (byte)'\n';
            }

            values[i].CopyTo(payload, offset);
            offset += values[i].Length;
        }

        return payload;
    }

    private static async Task WriteRawAsync(List<byte[]> lines, Stream destination, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            await destination.WriteAsync(line, cancellationToken);
        }
    }

    private static async Task WriteAsync(ReadOnlySequence<byte> buffer, Stream destination, CancellationToken cancellationToken)
    {
        foreach (var segment in buffer)
        {
            await destination.WriteAsync(segment, cancellationToken);
        }
    }

    private static bool IsDataLine(byte[] line) => line.AsSpan().StartsWith(DataField);

    private static bool IsBlank(byte[] line) => line.Length - TerminatorLength(line) == 0;

    private static bool EndsWithCrLf(byte[] line) => line.Length >= 2 && line[^2] == '\r' && line[^1] == '\n';

    private static int TerminatorLength(byte[] line) =>
        EndsWithCrLf(line) ? 2 : line.Length >= 1 && line[^1] == '\n' ? 1 : 0;
}
