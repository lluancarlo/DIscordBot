using Discord;
using DiscordBot.Bot;
using DiscordBot.Models;
using DiscordBot.Services;

namespace DiscordBot.Commands;

/// <summary>
/// /play &lt;link&gt; - starts a YouTube link, or puts it behind whatever is already playing.
/// </summary>
public sealed class PlayCommand(MusicService music) : ISlashCommand
{
    private const string LinkOption = "link";

    public string Name => "play";

    public SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(Name)
        .WithDescription("Play a YouTube link, or add it to the queue when something is already playing")
        .AddOption(LinkOption, ApplicationCommandOptionType.String, "YouTube or YouTube Music link", isRequired: true)
        .Build();

    public async Task ExecuteAsync(CommandContext context)
    {
        if (context.User is not { } user)
        {
            await context.ErrorAsync("This command only works inside a server.");
            return;
        }

        // Reading the video and downloading it takes longer than Discord's three second window.
        await context.DeferAsync();

        PlayResult result;
        try
        {
            result = await music.PlayAsync(user, context.GetString(LinkOption), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            await context.ErrorAsync("Could not join the voice channel — please try again.");
            return;
        }

        switch (result)
        {
            case PlayResult.NotInVoice:
                await context.ErrorAsync("You need to be in a voice channel first.");
                break;
            case PlayResult.WrongChannel:
                await context.ErrorAsync("I am already playing in another voice channel.");
                break;
            case PlayResult.InvalidLink:
                await context.ErrorAsync("That does not look like a link. Use something like `https://www.youtube.com/watch?v=...`.");
                break;
            case PlayResult.NotFound:
                await context.ErrorAsync("Could not read that video, is the link correct?");
                break;
            case PlayResult.Started started:
                await context.RespondAsync(Embeds.Track("Now playing", started.Track));
                break;
            case PlayResult.Queued queued:
                await context.RespondAsync(Embeds.Track($"Added to queue (#{queued.Position})", queued.Track));
                break;
        }
    }
}
