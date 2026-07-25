# Intro sounds

Drop audio files here (mp3, wav, ogg, opus, m4a, aac, flac, webm, wma).

At startup the bot scans this folder: if it finds at least one audio file, the intro feature is
enabled and every time the bot joins a voice channel it plays a random file from here before
starting the requested track. With no audio files the feature stays disabled.

The folder is copied next to the executable on build/publish. In Docker, mount a volume at
`/app/intro` to add or change sounds without rebuilding the image (the bot only rescans on restart).
