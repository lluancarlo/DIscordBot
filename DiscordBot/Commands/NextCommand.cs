using Discord;
using DiscordBot.Bot;
using DiscordBot.Models;
using DiscordBot.Services;

namespace DiscordBot.Commands;

/// <summary>
/// /next - stops the current track and moves on to the next one in the queue.
/// </summary>
public sealed class NextCommand(MusicService music) : ISlashCommand
{
    public string Name => "next";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Skip the current track and play the next one in the queue")
        .Build();

    public async Task ExecuteAsync(CommandContext context)
    {
        if (context.User is not { } user)
        {
            await context.ErrorAsync("This command only works inside a server.");
            return;
        }

        var result = await music.SkipAsync(user);

        switch (result)
        {
            case SkipResult.NotInVoice:
                await context.ErrorAsync("You need to be in a voice channel first.");
                break;
            case SkipResult.WrongChannel:
                await context.ErrorAsync("I am already playing in another voice channel.");
                break;
            case SkipResult.NothingPlaying:
                await context.ErrorAsync("Nothing is playing right now.");
                break;
            case SkipResult.LastTrack last:
                await context.RespondAsync(Embeds.Track(
                    "Nothing to skip to", last.Track, "This is the last track in the queue."));
                break;
            case SkipResult.Skipped skipped:
                var note = skipped.IsPaused
                    ? "Playback is still paused - run /pause to resume."
                    : null;
                await context.RespondAsync(Embeds.Track("Skipped, now playing", skipped.Next, note));
                break;
        }
    }
}
