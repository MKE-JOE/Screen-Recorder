# Screen Recorder

A small, portable screen recorder for Windows 10 and 11. It's a single `ScreenRecorder.exe`: no installer and no .NET runtime needed.

## Features

- **Capture area:** a frame you can drag anywhere on the screen and resize from its edges or corners. You can also pick a size preset or record a whole display.
- **3-second countdown** before recording starts. Recording runs for up to **2 hours** and stops and saves automatically when it hits the limit.
- **Record, Play, Pause, Stop, Rewind and Fast-forward** buttons. Pause works during recording too.
- **Audio:** records whatever plays on your default playback device. You can pick any other output device from the dropdown, and optionally mix in a microphone.
- **Quality:**
  - Video: H.264 at the full resolution of the area, 30 or 60 fps, near-lossless (CRF 18).
  - Audio: **AAC-LC, 320 kbps, 48 kHz stereo**. Plays on practically every device and app.
- **Trim:** set the start and end of the clip before exporting.
- **Add text:** type text, pick a font, size and colour, and drag it into place on the preview. It's burned into GIF exports, and into MP4/MKV too if you tick the box.
- **Export** to **MP4, MKV or GIF**. GIFs use a two-pass, optimised palette, and you can choose their width and frame rate.

The capture frame, countdown and app window are hidden from the recording on Windows 10 version 2004 and later.

## Download

Get `ScreenRecorder.exe` from the latest release on the [Releases](../../releases) page and run it.

## Where files go

- **Recordings:** `Videos\Screen Recorder\Recording <date time>.mp4`
- **Exports:** wherever you choose in the Save dialog.
- **Encoder:** on first launch the app unpacks its built-in encoder (FFmpeg, about 85 MB) once into `%LOCALAPPDATA%\PortableScreenRecorder`. Delete that folder to remove every trace. If you put an `ffmpeg.exe` next to `ScreenRecorder.exe`, the app uses that one instead.

## How it works

- **Screen capture:** FFmpeg's `ddagrab` (Desktop Duplication API, GPU-accelerated). If that's unavailable, for example over Remote Desktop or on some hybrid-GPU laptops, it falls back to `gdigrab`.
- **Audio capture:** WASAPI via NAudio, loopback for playback devices. The audio is streamed to FFmpeg over named pipes and paced against the wall clock, so it stays in sync over long recordings.
- **Pause:** each pause closes a segment. Stop joins the segments losslessly into one MP4.
- **Text:** rendered by FFmpeg's `drawtext` with the same Windows font used for the preview.

## Building

GitHub Actions builds the exe on every push. See `.github/workflows/build.yml`. To build it yourself on Windows:

```powershell
# 1. Put ffmpeg.exe (a build with libx264 and libfreetype, e.g. gyan.dev "essentials") here:
#    src\FfmpegPayload\Assets\ffmpeg.exe
# 2. Publish:
dotnet publish src\ScreenRecorder\ScreenRecorder.csproj -c Release -o publish
# -> publish\ScreenRecorder.exe
```

Requires the .NET 8 SDK.

## Licence notes

The bundled FFmpeg build is GPL-licensed (it includes libx264). See https://ffmpeg.org/legal.html.
