# Screen Recorder

A small, portable screen recorder for Windows 10 and 11. It's a single `ScreenRecorder.exe`: no installer and no .NET runtime needed.

## Features

- **Capture area:** a frame you can drag anywhere on the screen and resize from its edges or corners. You can also pick a size preset or record a whole display.
- **3-second countdown** before recording starts. Recording runs for up to **2 hours** and stops and saves automatically when it hits the limit.
- **Record, Play, Pause, Stop, Rewind and Fast-forward** buttons. Pause works during recording too.
- **Stays out of the way:** the main window minimizes while you record. A small floating bar, which you can drag anywhere and which never appears in the recording, has Record, Pause and Stop and shows the timer.
- **Global hotkeys** (work from any app): **Ctrl+Alt+R** record, **Ctrl+Alt+P** pause/resume, **Ctrl+Alt+Q** stop and save, **Ctrl+Alt+H** show/hide the capture frame (the hidden frame's area is still what gets recorded). Click **Change…** next to the hotkey list to pick your own; they're saved in `%LOCALAPPDATA%\PortableScreenRecorder\settings.json`.
- **Audio:** records whatever plays on your default playback device. You can pick any other output device from the dropdown, and optionally mix in a microphone.
- **Quality:**
  - Video: H.264 at the full resolution of the area, 30 or 60 fps, near-lossless (CRF 18).
  - Audio: **AAC-LC, 320 kbps, 48 kHz stereo**. Plays on practically every device and app.
- **Export** to **MP4, MKV or GIF**. GIFs use a two-pass, optimised palette, and you can choose their width and frame rate.
- **File size slider** (MP4/MKV): slide left for a smaller file and see the projected size as you go. Below 100% the video is re-encoded in two passes to land on that size. The video can go down to 25% of its original bitrate (never below 150 kbps), and audio steps down from 320k to no less than 160k only when it would take over a quarter of the file.
- Every format has two buttons: **Export…** saves the clip straight away with the default settings (GIFs are capped at 15 seconds; longer clips start from the playhead), and **Open in editor…** opens it in an editor first.
- **Video editor** (MP4 or MKV, then *Open in editor…*): a simple editor that keeps full quality.
  - Preview with play/pause, a scrub bar and ±5 s skip. Set the start and end to trim. Trimming and text now live only in the editors.
  - **Text captions** that show for the time range you choose ("from here" / "to here"), each with its own font, size, colour and outline. Drag them into place.
  - **Crop** presets (square, portrait 4:5, wide 16:9, story 9:16, free) and **resize** to 1080p, 720p or 480p (never upscales).
  - The same **File size** slider, measured against the edited clip.
  - Saves as MP4 or MKV with H.264 (CRF 18) and AAC 320 kbps audio. Captions are drawn exactly as they look in the preview.
- **GIF editor** (choose GIF, then *Open in editor…*): turns up to **15 seconds** of the clip into frames you can edit.
  - Frame strip with thumbnails. Select several frames with Ctrl/Shift to **delete**, **duplicate**, **move** them, or set how long each one shows.
  - **Reverse**, **Boomerang** (forward then back), **Faster / Slower**, and **Reduce** (drops every other frame for a smaller file but keeps the timing). **Undo/Redo** with Ctrl+Z / Ctrl+Y.
  - **Text layers** that appear only on the frames you choose, so captions can come and go. Drag them into place on the preview.
  - **Crop** presets for social media: square 1:1, portrait 4:5, wide 16:9, story 9:16, or free.
  - Loop forever, once, or a set number of times. "Best quality" (dithered) or "Smaller file".
  - The file size is shown after saving, with a warning above common limits (Discord 10 MB, X 15 MB).

- The inside of the capture frame is click-through, so apps under it (even a full-screen frame) stay usable. Move it with the **✥ Move** grip or the blue band; resize from the edges and corners.

The capture frame, countdown, floating bar and app window are hidden from the recording on Windows 10 version 2004 and later.

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
