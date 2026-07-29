# DiscordBot

A Discord music bot written in C# .NET 10

It plays audio from YouTube links in a voice channel: the link is downloaded with **yt-dlp**, decoded to PCM with **ffmpeg** and streamed into Discord with end-to-end encrypted voice (DAVE protocol).

## Commands

| Command | Description |
|---|---|
| `/play <link>` | Play a YouTube link. If something is already playing, the track is added to the queue. |
| `/pause` | Pause the current track; run it again to resume. |
| `/next` | Skip the current track and play the next one. The last track is never skipped. |
| `/stop` | Stop playback, clear the queue and leave the voice channel. |

Rules: you must be in a voice channel to use a command, and in the *same* channel as the bot when it is already playing somewhere. `/pause`, `/next` and `/stop` reply that nothing is playing when the bot is idle.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An internet connection for the **first** build (it downloads the bundled tools, see below)
- A Discord bot token ([Discord Developer Portal](https://discord.com/developers/applications)) with the **Guilds** and **Guild Voice States** gateway intents

Nothing else needs to be installed globally — ffmpeg and yt-dlp are **not** system dependencies.

## Getting started

1. Configure the token. Either edit `DiscordBot/appsettings.json`:

   ```json
   {
     "Discord": {
       "Token": "YOUR_BOT_TOKEN_HERE",
       "GuildId": 0
     }
   }
   ```

   or set the `DISCORD_TOKEN` environment variable (it overrides the file).

   `GuildId` is optional: `0` registers the slash commands in every guild the bot is in; a specific guild id restricts registration to that guild.

2. Build and run:

   ```
   dotnet run --project DiscordBot
   ```

   The first build downloads ffmpeg (~140 MB zip) and yt-dlp (~18 MB) into `DiscordBot/tools/` and copies them next to the executable. Later builds skip the download.

3. Invite the bot to your server with the `applications.commands` and `bot` scopes (with *Connect* and *Speak* permissions), join a voice channel and run `/play`.

Slash commands are registered per guild on startup, so they show up immediately (global registration can take up to an hour).

## Project structure

```
DiscordBot/
  Program.cs               Host + DI wiring. New commands are registered here.
  Bot/                     Discord infrastructure, no business logic
    DiscordBotService.cs   Login, gateway events, log bridge
    CommandRegistry.cs     name -> command map, guild registration, dispatch
    ISlashCommand.cs       Contract every command implements
    CommandContext.cs      Options + replies, hides the raw Discord.Net API
    Embeds.cs              Shared reply look (colour, layout)
  Commands/                One file per slash command
    PlayCommand.cs
    PauseCommand.cs
    StopCommand.cs
  Services/                Business logic
    MusicService.cs        Facade: play / pause / stop / queue, voice-channel rules
    GuildPlayer.cs         Per-guild queue + playback loop (internal)
    YoutubeDownloader.cs   yt-dlp wrapper: metadata + audio download with cache
    FfmpegPcmStream.cs     ffmpeg wrapper: file -> 48 kHz stereo PCM
    BundledTools.cs        Locates the bundled ffmpeg/yt-dlp executables
  Models/
    TrackInfo.cs           A queued track
    PlayResult.cs          Result types the services return to the commands
    PauseResult.cs
    StopResult.cs
  Configuration/
    BotOptions.cs          Settings bound from appsettings.json
  tools/                   ffmpeg.exe + yt-dlp.exe (downloaded by the build, gitignored)
```

### Adding a new command

1. Create `Commands/MyCommand.cs` implementing `ISlashCommand` (`Name`, `Build()`, `ExecuteAsync`).
2. Add one line to `Program.cs`:

   ```csharp
   builder.Services.AddSingleton<ISlashCommand, MyCommand>();
   ```

Keep the command thin: parse options, call a service in `Services/`, turn the result into an embed via `Embeds`. Business logic belongs in the services so it stays testable without Discord.

## How playback works

```
/play link
   └─ yt-dlp  ──►  cache/<video-id>.m4a        (skipped when already cached)
        └─ ffmpeg  ──►  48 kHz stereo s16le PCM (stdout pipe)
             └─ Discord.Net CreatePCMStream ──► Opus + DAVE E2EE ──► voice channel
```

- Each guild has its own `GuildPlayer` with its own queue; playback runs on a background loop that pulls the next track until the queue is empty, then leaves the channel.
- `/pause` blocks the streaming loop with a gate instead of tearing anything down, so resume is instant.
- Downloaded audio is cached in `bin/.../cache/` by video id — replaying a track never re-downloads it.

## Native dependencies

All native pieces are bundled, none are taken from the system:

| Piece | Source | Purpose |
|---|---|---|
| `ffmpeg.exe` | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (LGPL, downloaded by the build) | Decode audio to PCM |
| `yt-dlp.exe` | [yt-dlp](https://github.com/yt-dlp/yt-dlp) (downloaded by the build) | YouTube metadata + download |
| `opus.dll` | NuGet `DSharpPlus.Natives.Opus` | Voice encoding |
| `libsodium.dll` | NuGet `libsodium` | Voice encryption |
| `libdave.dll` | NuGet `libdave` | Discord's DAVE E2EE protocol (required — voice closes with error 4017 without it) |

## Troubleshooting

- **`Bundled tool 'ffmpeg.exe' was not found`** — run `dotnet build` once with an internet connection; the build downloads the tools.
- **Voice connection times out** — make sure command handlers never run on the gateway task (see `CommandRegistry.Dispatch`); Discord.Net needs that task free for the voice handshake.
- **Close code 4017 (`E2EE/DAVE protocol required`)** — `libdave.dll` must sit next to the executable and `EnableVoiceDaveEncryption` must be `true` (both are handled by the project file / `Program.cs`).
- **Commands don't appear in Discord** — the bot registers commands per guild at startup; check the log for `Registered 3 commands in guild ...` and re-invite the bot with the `applications.commands` scope if needed.
