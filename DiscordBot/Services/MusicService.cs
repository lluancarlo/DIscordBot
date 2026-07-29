using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using DiscordBot.Configuration;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordBot.Services;

/// <summary>
/// The one place that knows how music playback works: it owns a <see cref="GuildPlayer"/> per
/// guild, applies the voice channel rules and reports back what happened. Commands only translate
/// the results into Discord replies.
/// </summary>
public sealed class MusicService(
    YoutubeDownloader downloader,
    IntroService introService,
    IOptions<BotOptions> options,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<ulong, GuildPlayer> _players = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _aloneTimers = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<MusicService>();

    /// <summary>Queues a link, connecting to the caller's voice channel when needed.</summary>
    public async Task<PlayResult> PlayAsync(SocketGuildUser user, string link, CancellationToken ct)
    {
        if (!TryResolveVoiceChannel(user, out var channel, out var denial))
        {
            return denial == VoiceDenial.NotInVoice
                ? new PlayResult.NotInVoice()
                : new PlayResult.WrongChannel();
        }

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new PlayResult.InvalidLink();
        }

        var requestedBy = user.GlobalName ?? user.Username;
        var track = await downloader.GetTrackInfoAsync(uri.ToString(), requestedBy, ct);
        if (track is null)
            return new PlayResult.NotFound();

        var position = await GetPlayer(user.Guild.Id).EnqueueAsync(channel, track);

        return position == 0
            ? new PlayResult.Started(track)
            : new PlayResult.Queued(track, position);
    }

    /// <summary>Pauses the current track, or resumes it when it is already paused.</summary>
    public async Task<PauseResult> TogglePauseAsync(SocketGuildUser user)
    {
        if (!TryResolveVoiceChannel(user, out _, out var denial))
        {
            return denial == VoiceDenial.NotInVoice
                ? new PauseResult.NotInVoice()
                : new PauseResult.WrongChannel();
        }

        var player = FindPlayer(user.Guild.Id);
        if (player is null || player.CurrentTrack is not { } track)
            return new PauseResult.NothingPlaying();

        var paused = await player.TogglePauseAsync();

        return paused
            ? new PauseResult.Paused(track)
            : new PauseResult.Resumed(track);
    }

    /// <summary>
    /// Skips to the next queued track. The last track is left playing: there is nothing to move on
    /// to, and silently stopping it would make /next a surprising /stop.
    /// </summary>
    public async Task<SkipResult> SkipAsync(SocketGuildUser user)
    {
        if (!TryResolveVoiceChannel(user, out _, out var denial))
        {
            return denial == VoiceDenial.NotInVoice
                ? new SkipResult.NotInVoice()
                : new SkipResult.WrongChannel();
        }

        var player = FindPlayer(user.Guild.Id);
        if (player is null || player.CurrentTrack is not { } current)
            return new SkipResult.NothingPlaying();

        var next = await player.SkipAsync();

        return next is null
            ? new SkipResult.LastTrack(current)
            : new SkipResult.Skipped(current, next, player.IsPaused);
    }

    /// <summary>Clears the queue, stops playback and leaves the voice channel.</summary>
    public async Task<StopResult> StopAsync(SocketGuildUser user)
    {
        if (!TryResolveVoiceChannel(user, out _, out var denial))
        {
            return denial == VoiceDenial.NotInVoice
                ? new StopResult.NotInVoice()
                : new StopResult.WrongChannel();
        }

        var player = FindPlayer(user.Guild.Id);
        if (player is null || !player.IsPlaying)
            return new StopResult.NothingPlaying();

        await player.StopAsync();
        return new StopResult.Stopped();
    }

    /// <summary>
    /// A snapshot of what a guild is playing: the current track and the queue behind it.
    /// Read-only, so unlike the other operations it needs no voice channel guard.
    /// </summary>
    public ListResult GetPlaylist(ulong guildId)
    {
        var player = FindPlayer(guildId);
        if (player is null || player.CurrentTrack is not { } current)
            return new ListResult.NothingPlaying();

        return new ListResult.Playing(current, player.Queue, player.IsPaused);
    }

    /// <summary>Empties the queue but lets the current track finish playing.</summary>
    public async Task<ClearResult> ClearAsync(SocketGuildUser user)
    {
        if (!TryResolveVoiceChannel(user, out _, out var denial))
        {
            return denial == VoiceDenial.NotInVoice
                ? new ClearResult.NotInVoice()
                : new ClearResult.WrongChannel();
        }

        var player = FindPlayer(user.Guild.Id);
        if (player is null || !player.IsPlaying)
            return new ClearResult.NothingPlaying();

        var removed = await player.ClearQueueAsync();
        return new ClearResult.Cleared(removed);
    }

    /// <summary>
    /// Forces the bot out of whatever voice channel it is in, dropping the queue and the current
    /// track. Unlike /stop this works no matter which channel the caller sits in.
    /// </summary>
    public async Task<QuitResult> QuitAsync(SocketGuild guild)
    {
        var player = FindPlayer(guild.Id);
        var botChannel = guild.CurrentUser.VoiceChannel;

        if (botChannel is null && (player is null || !player.IsPlaying))
            return new QuitResult.NotInChannel();

        if (player is not null)
            await player.StopAsync();
        else if (botChannel is not null)
            await botChannel.DisconnectAsync();

        return new QuitResult.Left();
    }

    /// <summary>
    /// Reacts to users joining or leaving voice channels: when the bot ends up alone in its
    /// channel a countdown starts, and unless someone comes back in time it leaves like /quit.
    /// Wired to <see cref="DiscordSocketClient.UserVoiceStateUpdated"/>.
    /// </summary>
    public Task HandleVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        // Mute/deafen toggles fire this event too; only channel changes matter here.
        if (before.VoiceChannel?.Id == after.VoiceChannel?.Id)
            return Task.CompletedTask;

        var guilds = new[] { before.VoiceChannel?.Guild, after.VoiceChannel?.Guild }
            .OfType<SocketGuild>()
            .DistinctBy(g => g.Id);

        foreach (var guild in guilds)
            UpdateAloneTimer(guild);

        return Task.CompletedTask;
    }

    private void UpdateAloneTimer(SocketGuild guild)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.AloneTimeoutSeconds);
        if (timeout <= TimeSpan.Zero)
            return;

        if (!IsAlone(guild))
        {
            if (_aloneTimers.TryRemove(guild.Id, out var armed))
            {
                armed.Cancel();
                armed.Dispose();
            }

            return;
        }

        var cts = new CancellationTokenSource();
        if (!_aloneTimers.TryAdd(guild.Id, cts))
        {
            // A countdown is already running for this guild.
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(timeout, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Whoever removes the entry disposes it; losing the race means a voice event is
            // cancelling this very timer right now.
            if (_aloneTimers.TryRemove(new KeyValuePair<ulong, CancellationTokenSource>(guild.Id, cts)))
                cts.Dispose();

            // The event that would clear the timer may have been missed; check again.
            if (!IsAlone(guild))
                return;

            _logger.LogInformation(
                "Guild {GuildId}: alone in the voice channel for {Timeout}, leaving", guild.Id, timeout);
            await QuitAsync(guild);
        });
    }

    /// <summary>True when the bot sits in a voice channel with no human (non-bot) users.</summary>
    private static bool IsAlone(SocketGuild guild) =>
        guild.CurrentUser.VoiceChannel is { } channel && channel.ConnectedUsers.All(u => u.IsBot);

    /// <summary>Tracks waiting behind the current one, oldest first.</summary>
    public IReadOnlyList<TrackInfo> GetQueue(ulong guildId) => FindPlayer(guildId)?.Queue ?? [];

    /// <summary>The track a guild is playing right now, or <c>null</c> when idle.</summary>
    public TrackInfo? GetCurrentTrack(ulong guildId) => FindPlayer(guildId)?.CurrentTrack;

    /// <summary>
    /// The rules the JavaScript bot applies as well: the caller has to be in a voice channel,
    /// and in the same one as the bot when it is already connected somewhere.
    /// </summary>
    private static bool TryResolveVoiceChannel(SocketGuildUser user, out IVoiceChannel channel, out VoiceDenial denial)
    {
        channel = null!;

        if (user.VoiceChannel is null)
        {
            denial = VoiceDenial.NotInVoice;
            return false;
        }

        var botChannel = user.Guild.CurrentUser.VoiceChannel;
        if (botChannel is not null && botChannel.Id != user.VoiceChannel.Id)
        {
            denial = VoiceDenial.WrongChannel;
            return false;
        }

        channel = user.VoiceChannel;
        denial = VoiceDenial.None;
        return true;
    }

    private GuildPlayer GetPlayer(ulong guildId) =>
        _players.GetOrAdd(guildId, id => new GuildPlayer(
            id,
            downloader,
            introService,
            TimeSpan.FromSeconds(options.Value.IdleTimeoutSeconds),
            loggerFactory.CreateLogger<GuildPlayer>()));

    private GuildPlayer? FindPlayer(ulong guildId) =>
        _players.TryGetValue(guildId, out var player) ? player : null;

    private enum VoiceDenial
    {
        None,
        NotInVoice,
        WrongChannel
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var timer in _aloneTimers.Values)
        {
            timer.Cancel();
            timer.Dispose();
        }

        _aloneTimers.Clear();

        foreach (var player in _players.Values)
            await player.DisposeAsync();

        _players.Clear();
    }
}
