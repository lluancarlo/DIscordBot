using Discord;
using Discord.Audio;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;

namespace DiscordBot.Services;

/// <summary>
/// Playback state for a single guild: the voice connection, the track queue and the
/// background loop that streams the current track into Discord.
/// Only <see cref="MusicService"/> talks to this; commands go through that facade.
/// </summary>
internal sealed class GuildPlayer(ulong guildId, YoutubeDownloader downloader, TimeSpan channelTimeout, ILogger logger) : IAsyncDisposable
{
    private const int BufferMilliseconds = 1000;
    private const int ReadBufferSize = 3840 * 4; // a few 20 ms Opus frames of 48 kHz stereo PCM

    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private readonly Queue<QueuedTrack> _queue = new();

    private IAudioClient? _audioClient;
    private ulong _voiceChannelId;
    private CancellationTokenSource? _timeoutCts;
    private CancellationTokenSource? _playbackCts;
    private Task _playbackLoop = Task.CompletedTask;
    private bool _loopRunning;

    public TrackInfo? CurrentTrack { get; private set; }

    public bool IsPaused { get; private set; }

    public bool IsPlaying => CurrentTrack is not null;

    public IReadOnlyList<TrackInfo> Queue
    {
        get
        {
            _sync.Wait();
            try
            {
                return [.. _queue.Select(q => q.Track)];
            }
            finally
            {
                _sync.Release();
            }
        }
    }

    /// <summary>
    /// Adds a track to the queue and starts the playback loop when it is not running yet.
    /// </summary>
    /// <returns>The queue position, 0 meaning it starts playing right away.</returns>
    public async Task<int> EnqueueAsync(IVoiceChannel channel, TrackInfo track)
    {
        await _sync.WaitAsync();
        try
        {
            // Replaced instead of disposed: a download dropped by an earlier failed connect may
            // still hold the old token.
            if (!_loopRunning)
                _playbackCts = new CancellationTokenSource();

            // Start the download right away so it overlaps the voice connect and whatever is
            // playing ahead of it; by its turn the file is usually already cached.
            var download = downloader.DownloadAudioAsync(track, _playbackCts!.Token);

            try
            {
                await ConnectAsync(channel);
            }
            catch
            {
                // The track is not queued, but the download keeps warming the cache for a retry.
                Observe(download);
                throw;
            }

            _queue.Enqueue(new QueuedTrack(track, download));
            var position = _queue.Count - (IsPlaying ? 0 : 1);

            if (!_loopRunning)
            {
                _loopRunning = true;
                _playbackLoop = Task.Run(() => RunPlaybackLoopAsync(_playbackCts.Token));
            }

            return position;
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Pauses or resumes playback. Returns the new paused state.</summary>
    public async Task<bool> TogglePauseAsync()
    {
        await _sync.WaitAsync();
        try
        {
            if (IsPaused)
            {
                IsPaused = false;
                _pauseGate.Release();
            }
            else
            {
                // Taking the gate makes the streaming loop block on its next write.
                await _pauseGate.WaitAsync();
                IsPaused = true;
            }

            return IsPaused;
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Removes every queued track, leaving the one currently playing untouched.</summary>
    /// <returns>How many tracks were removed.</returns>
    public async Task<int> ClearQueueAsync()
    {
        await _sync.WaitAsync();
        try
        {
            return DrainQueueLocked();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Clears the queue, stops the current track and leaves the voice channel.</summary>
    public async Task StopAsync()
    {
        Task loop;

        await _sync.WaitAsync();
        try
        {
            DrainQueueLocked();

            if (IsPaused)
            {
                IsPaused = false;
                _pauseGate.Release();
            }

            _playbackCts?.Cancel();
            loop = _playbackLoop;
        }
        finally
        {
            _sync.Release();
        }

        await loop.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        await DisconnectAsync();
    }

    private async Task ConnectAsync(IVoiceChannel channel)
    {
        if (_audioClient is { ConnectionState: ConnectionState.Connected } && _voiceChannelId == channel.Id)
            return;

        // A half-dead client left behind by a dropped voice session makes the next connect time
        // out, so tear it down completely before joining again.
        await DisconnectAsync();

        logger.LogInformation("Guild {GuildId}: connecting to voice channel {Channel}", guildId, channel.Name);

        try
        {
            // Self-deafen: the bot never uses incoming audio, and receiving it costs real CPU
            // (per-speaker decryption) on small hosts like a Raspberry Pi.
            _audioClient = await channel.ConnectAsync(selfDeaf: true);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Guild {GuildId}: voice connect timed out, retrying once", guildId);
            await DisconnectAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
            _audioClient = await channel.ConnectAsync(selfDeaf: true);
        }

        _voiceChannelId = channel.Id;
        StartChannelTimeout();
    }

    /// <summary>
    /// Arms the auto-leave timer. It counts from the moment the bot joins the channel and is
    /// disarmed again by <see cref="DisconnectAsync"/>, so reconnecting restarts the clock.
    /// </summary>
    private void StartChannelTimeout()
    {
        CancelChannelTimeout();

        if (channelTimeout <= TimeSpan.Zero)
            return;

        var cts = new CancellationTokenSource();
        _timeoutCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(channelTimeout, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            logger.LogInformation(
                "Guild {GuildId}: connected for more than {Timeout}, leaving the voice channel",
                guildId, channelTimeout);
            await StopAsync();
        });
    }

    private void CancelChannelTimeout()
    {
        var cts = _timeoutCts;
        _timeoutCts = null;

        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task DisconnectAsync()
    {
        CancelChannelTimeout();

        if (_audioClient is null)
            return;

        try
        {
            await _audioClient.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Guild {GuildId}: error while leaving the voice channel", guildId);
        }
        finally
        {
            _audioClient.Dispose();
            _audioClient = null;
            _voiceChannelId = 0;
        }
    }

    private async Task RunPlaybackLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                QueuedTrack? queued;

                await _sync.WaitAsync(ct);
                try
                {
                    if (!_queue.TryDequeue(out queued))
                    {
                        // Nothing left to play. Leaving the channel and clearing the running flag
                        // happens under the lock so a /play racing with this cannot be lost.
                        CurrentTrack = null;
                        await DisconnectAsync();
                        _loopRunning = false;
                        return;
                    }

                    CurrentTrack = queued.Track;
                }
                finally
                {
                    _sync.Release();
                }

                try
                {
                    await PlayTrackAsync(queued, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Guild {GuildId}: playback of '{Title}' failed, skipping", guildId, queued.Track.Title);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Guild {GuildId}: playback stopped", guildId);
        }
        finally
        {
            CurrentTrack = null;

            if (_loopRunning)
            {
                // Cancelled or crashed out of the loop: release the flag so /play can start it again.
                await _sync.WaitAsync(CancellationToken.None);
                _loopRunning = false;
                _sync.Release();
            }
        }
    }

    private async Task PlayTrackAsync(QueuedTrack queued, CancellationToken ct)
    {
        var track = queued.Track;
        var file = await queued.Download.WaitAsync(ct);

        var audioClient = _audioClient
                          ?? throw new InvalidOperationException("Not connected to a voice channel.");

        logger.LogInformation("Guild {GuildId}: now playing '{Title}'", guildId, track.Title);

        using var ffmpeg = FfmpegPcmStream.Open(file);
        await using var discord = audioClient.CreatePCMStream(AudioApplication.Music, bufferMillis: BufferMilliseconds);

        var buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                var read = await ffmpeg.Output.ReadAsync(buffer, ct);
                if (read == 0)
                    break;

                // Blocks here for as long as the player is paused.
                await _pauseGate.WaitAsync(ct);
                _pauseGate.Release();

                await discord.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            try
            {
                await discord.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Guild {GuildId}: flushing the voice stream failed", guildId);
            }
        }
    }

    /// <summary>
    /// Empties the queue, detaching the prefetch downloads: they finish into the cache on their
    /// own, they just must not fail unobserved. Callers hold <see cref="_sync"/>.
    /// </summary>
    private int DrainQueueLocked()
    {
        var removed = _queue.Count;

        while (_queue.TryDequeue(out var queued))
            Observe(queued.Download);

        return removed;
    }

    /// <summary>Swallows the eventual failure of a task nobody awaits anymore.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>A queued track plus the download that started the moment it was enqueued.</summary>
    private sealed record QueuedTrack(TrackInfo Track, Task<string> Download);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _playbackCts?.Dispose();
        _sync.Dispose();
        _pauseGate.Dispose();
    }
}
