using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Speech;

/// <summary>Raw 16-bit little-endian mono PCM arriving over a stream (HTTP body, process stdout) → audio chunks.</summary>
internal static class PcmStream
{
    /// <summary>Chunks are at least this long (except the last), so tiny network reads don't make tiny chunks…</summary>
    public const int MinChunkMs = 50;

    /// <summary>…and at most this long, so playback can start as soon as the first audio arrives.</summary>
    public const int MaxChunkMs = 100;

    /// <summary>
    /// Yields <see cref="AudioChunk"/>s of 50–100 ms as the bytes arrive. Reads may split a sample anywhere: an odd
    /// byte is carried into the next read, and a dangling byte at the very end is dropped. A stream that breaks
    /// mid-way becomes a <see cref="PluginException"/> naming <paramref name="service"/>; cancellation stays an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async IAsyncEnumerable<AudioChunk> ReadChunksAsync(
        Stream pcm, int sampleRate, string service, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        int minSamples = Math.Max(1, sampleRate * MinChunkMs / 1000);
        int maxSamples = Math.Max(minSamples, sampleRate * MaxChunkMs / 1000);
        // Room for a few chunks per read; what stays behind between reads is < one minimum chunk plus an odd byte.
        var buffer = new byte[maxSamples * 2 * 4];
        int filled = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            int read = await ReadAsync(pcm, buffer.AsMemory(filled), service, cancel).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;

            int samples = filled / 2, offset = 0;
            while (samples >= minSamples)
            {
                int take = Math.Min(samples, maxSamples);
                yield return new AudioChunk(ToSamples(buffer.AsSpan(offset, take * 2)), sampleRate);
                offset += take * 2;
                samples -= take;
            }
            if (offset > 0)
            {
                filled -= offset;
                buffer.AsSpan(offset, filled).CopyTo(buffer);
            }
        }
        cancel.ThrowIfCancellationRequested();
        if (filled >= 2) yield return new AudioChunk(ToSamples(buffer.AsSpan(0, filled & ~1)), sampleRate);
    }

    /// <summary>Little-endian 16-bit samples from bytes (whole samples only).</summary>
    public static short[] ToSamples(ReadOnlySpan<byte> bytes)
    {
        var samples = new short[bytes.Length / 2];
        if (BitConverter.IsLittleEndian)
        {
            bytes[..(samples.Length * 2)].CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
        else
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]);
        }
        return samples;
    }

    static async ValueTask<int> ReadAsync(Stream pcm, Memory<byte> into, string service, CancellationToken cancel)
    {
        try
        {
            return await pcm.ReadAsync(into, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (cancel.IsCancellationRequested)
        {
            // Cancelling often surfaces as a broken stream; report it as what it is.
            throw new OperationCanceledException("Cancelled.", e, cancel);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or ObjectDisposedException)
        {
            throw SpeechHttp.Fail(ServiceErrorKind.Network, null, e.Message, $"The audio from {service} was cut off ({e.Message}).", e);
        }
    }
}
