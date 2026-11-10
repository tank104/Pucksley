namespace Pucksley.Sdk;

/// <summary>
/// A plugin that animates a face to the assistant's speech (e.g. Openspline): the hub hands it
/// each reply's audio and plays back what it returns, frames and audio together, on pucks that
/// can show video. While no face plugin is ready, replies play as plain audio.
/// </summary>
public interface ITalkingFace
{
    /// <summary>Ready to take a reply now (the service is reachable and the avatar is set up).</summary>
    bool IsReady { get; }

    /// <summary>Starts one reply. Throws (PluginException) when the face can't be used right now.</summary>
    Task<ITalkingFaceTurn> BeginTurnAsync(CancellationToken cancel);
}

/// <summary>One reply: audio in, frames with their audio out.</summary>
public interface ITalkingFaceTurn : IAsyncDisposable
{
    /// <summary>Queues speech (mono 16-bit). Returns once the face service has taken it.</summary>
    Task SendAudioAsync(ReadOnlyMemory<short> pcm, int sampleRate, CancellationToken cancel);

    /// <summary>No more audio for this reply: what was sent plays out, then <see cref="ReadAsync"/> ends.</summary>
    Task EndAsync(CancellationToken cancel);

    /// <summary>The animated reply in order: each piece is one video frame and the audio that goes with it.</summary>
    IAsyncEnumerable<FaceMedia> ReadAsync(CancellationToken cancel);

    /// <summary>Stop now (the person interrupted).</summary>
    Task InterruptAsync();
}

/// <param name="Jpeg">The frame (any size; the puck shows its centre).</param>
/// <param name="Audio">The audio that plays with this frame, mono 16-bit.</param>
/// <param name="SampleRate">The audio's rate in Hz.</param>
public sealed record FaceMedia(byte[] Jpeg, short[] Audio, int SampleRate);
