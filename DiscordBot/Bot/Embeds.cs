using Discord;
using DiscordBot.Models;

namespace DiscordBot.Bot;

/// <summary>
/// Shared look of the bot's replies, so every command does not repeat the colour and layout.
/// </summary>
public static class Embeds
{
    private static readonly Color Accent = new(0x4F, 0xC3, 0xF7);

    /// <summary>
    /// A track centred embed: title, clickable song name, thumbnail and who asked for it.
    /// <paramref name="note"/> adds a line under the song name when the reply needs to say
    /// something the track itself does not carry.
    /// </summary>
    public static Embed Track(string title, TrackInfo track, string? note = null) =>
        new EmbedBuilder()
            .WithColor(Accent)
            .WithTitle(title)
            .WithDescription(note is null
                ? $"[{track.Title}]({track.Url})"
                : $"[{track.Title}]({track.Url})\n\n{note}")
            .WithThumbnailUrl(track.Thumbnail)
            .WithFooter($"{track.DurationText} · requested by {track.RequestedBy}")
            .WithCurrentTimestamp()
            .Build();

    /// <summary>A plain message embed without a track attached.</summary>
    public static Embed Message(string title, string description) =>
        new EmbedBuilder()
            .WithColor(Accent)
            .WithTitle(title)
            .WithDescription(description)
            .WithCurrentTimestamp()
            .Build();
}
