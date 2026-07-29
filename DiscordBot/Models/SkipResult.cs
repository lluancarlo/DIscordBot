namespace DiscordBot.Models;

/// <summary>Outcome of a /next request.</summary>
public abstract record SkipResult
{
    /// <summary>The caller is not connected to a voice channel.</summary>
    public sealed record NotInVoice : SkipResult;

    /// <summary>The bot is playing in a different voice channel.</summary>
    public sealed record WrongChannel : SkipResult;

    /// <summary>There is no track to skip.</summary>
    public sealed record NothingPlaying : SkipResult;

    /// <summary>Nothing is queued behind the current track, so it kept playing.</summary>
    public sealed record LastTrack(TrackInfo Track) : SkipResult;

    /// <summary>
    /// The current track was cut short and <paramref name="Next"/> took over.
    /// <paramref name="IsPaused"/> reports that playback is still paused, so the new track will
    /// not be heard until /pause resumes it.
    /// </summary>
    public sealed record Skipped(TrackInfo Track, TrackInfo Next, bool IsPaused) : SkipResult;
}
