namespace Pucksley.Plugins.Speech;

/// <summary>Downloads a (large) model file with progress, stall detection and a format check.</summary>
internal static class ModelDownloader
{
    /// <summary>ggml model files start with the magic number 0x67676d6c ("ggml"), little-endian.</summary>
    const uint GgmlMagic = 0x67676d6c;

    /// <summary>
    /// Streams <paramref name="url"/> into <paramref name="destination"/> (overwritten). Fails with
    /// <see cref="TimeoutException"/> when no data arrives for <paramref name="stallTimeout"/> (default 60 s),
    /// <see cref="IOException"/> when the download ends short, <see cref="InvalidDataException"/> when the file
    /// isn't a ggml model, and <see cref="HttpRequestException"/> for HTTP errors. The shared client's timeout only
    /// covers the wait for headers, so big files are fine.
    /// </summary>
    public static async Task DownloadAsync(
        HttpClient http, Uri url, string destination, Action<long, long?> progress, CancellationToken cancel, TimeSpan? stallTimeout = null)
    {
        var stallAfter = stallTimeout ?? TimeSpan.FromSeconds(60);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(stallAfter);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            long? total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(stallAfter);  // data arrived: restart the stall timer
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    done += read;
                    progress(done, total);
                }
                if (total is { } expected && done != expected)
                    throw new IOException($"the download ended early ({done:N0} of {expected:N0} bytes)");
            }
            if (!IsGgmlFile(destination)) throw new InvalidDataException("the file isn't a Whisper model");
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            throw new TimeoutException("the download stalled");
        }
    }

    internal static bool IsGgmlFile(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && BitConverter.ToUInt32(head) == GgmlMagic;
    }
}
