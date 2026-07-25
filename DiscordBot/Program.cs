using Discord;
using Discord.WebSocket;
using DiscordBot.Bot;
using DiscordBot.Commands;
using DiscordBot.Configuration;
using DiscordBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));

// Allow DISCORD_TOKEN as an override, which is handy for running the bot outside Visual Studio.
var environmentToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
if (!string.IsNullOrWhiteSpace(environmentToken))
    builder.Services.PostConfigure<BotOptions>(o => o.Token = environmentToken);

builder.Services.AddSingleton(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
    LogLevel = LogSeverity.Info,
    // Discord requires end to end encrypted voice; libdave.dll is copied next to the app by the build.
    EnableVoiceDaveEncryption = true
});
builder.Services.AddSingleton<DiscordSocketClient>();

// Business logic.
builder.Services.AddSingleton<YoutubeDownloader>();
builder.Services.AddSingleton<IntroService>();
builder.Services.AddSingleton<MusicService>();

// Slash commands: add a file under Commands/ and one line here.
builder.Services.AddSingleton<ISlashCommand, PlayCommand>();
builder.Services.AddSingleton<ISlashCommand, PauseCommand>();
builder.Services.AddSingleton<ISlashCommand, StopCommand>();
builder.Services.AddSingleton<ISlashCommand, ListCommand>();
builder.Services.AddSingleton<ISlashCommand, ClearCommand>();
builder.Services.AddSingleton<ISlashCommand, QuitCommand>();

builder.Services.AddSingleton<CommandRegistry>();
builder.Services.AddHostedService<DiscordBotService>();

var host = builder.Build();
await host.RunAsync();
