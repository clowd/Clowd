[![Discord](https://img.shields.io/discord/767856501477343282?style=flat-square&color=purple)](https://discord.gg/M6he8ZPAAJ)
[![Build](https://img.shields.io/github/actions/workflow/status/clowd/Clowd/ci.yml?branch=v4-rewrite&style=flat-square)](https://github.com/clowd/Clowd/actions)

![73130051](https://user-images.githubusercontent.com/1287295/177045040-15601340-0380-418e-975a-cc1d0dd06ebc.png)

# Clowd

Clowd is a minimalist screen capture / screen recording tool. It sits out of your way in your system tray, and is activated when you press the `PrntScr` key.

## Features
 - [Region capture](#region-capture--prntscr-replacement) as a fast, zoomable `PrntScr` replacement
 - [Video recording](#video-recorder) to MKV, MP4, or GIF, with speaker and microphone audio
 - [Image editor](#image-editor) for quick annotations and edits
 - [Upload anything](#upload-anything) to a variety of file sharing websites, with the URL copied to your clipboard
 - [Accelerated uploads](#accelerated-uploads) - share the link immediately, before the upload has finished
 - [Color picker](#color-picker) that can sample any pixel on screen
 - Window detection - selections snap to window borders
 - Fully keyboard accessible
 - Global hotkeys, configurable in settings
 - Lives in the system tray and stays out of your way
 - Automatic updates, with opt-in experimental builds

## Downloads

The latest stable release can always be downloaded below. Installed builds keep themselves up to date automatically; experimental (pre-release) builds can be opted into via the app settings.

| Platform | Installer | Portable |
| --- | --- | --- |
| Windows x64 | [Clowd-win-x64-Setup.exe](https://github.com/clowd/Clowd/releases/latest/download/Clowd-win-x64-Setup.exe) | [Clowd-win-x64-Portable.zip](https://github.com/clowd/Clowd/releases/latest/download/Clowd-win-x64-Portable.zip) |
| Windows arm64 | [Clowd-win-arm64-Setup.exe](https://github.com/clowd/Clowd/releases/latest/download/Clowd-win-arm64-Setup.exe) | [Clowd-win-arm64-Portable.zip](https://github.com/clowd/Clowd/releases/latest/download/Clowd-win-arm64-Portable.zip) |
| macOS Apple Silicon | [Clowd-osx-arm64-Setup.pkg](https://github.com/clowd/Clowd/releases/latest/download/Clowd-osx-arm64-Setup.pkg) | [Clowd-osx-arm64-Portable.zip](https://github.com/clowd/Clowd/releases/latest/download/Clowd-osx-arm64-Portable.zip) |
| macOS Intel | [Clowd-osx-x64-Setup.pkg](https://github.com/clowd/Clowd/releases/latest/download/Clowd-osx-x64-Setup.pkg) | [Clowd-osx-x64-Portable.zip](https://github.com/clowd/Clowd/releases/latest/download/Clowd-osx-x64-Portable.zip) |

I will respond to bug reports or questions in GitHub issues. Also feel free to bug me (@caesay) in the Clowd Discord server:

[![discordimg2](https://user-images.githubusercontent.com/1287295/150318745-cbfcf5d0-3697-4bef-ac1a-b0d751f53b48.png)](https://discord.gg/M6he8ZPAAJ)

----

## Region Capture / PrntScr Replacement
 - Uses Direct3D so is super fast and responsive
 - Scroll to zoom in on any part of your screen, pixel perfect selections (or just looking at stuff on your screen closely)
 - Fully keyboard accessibly
 - Snaps selection to window borders
 - Click on any window to quickly bring it to the foreground
 - Select any color to open a color picker

https://user-images.githubusercontent.com/1287295/177042825-48707490-ae67-4a75-acee-216529f49c23.mp4

----

## Video Recorder
 - Record MKV, MP4, or GIF's easily
 - Capture Speaker and Microphone audio
 - Optionally show animation where mouse was clicked
 
![image of video capture ui](https://user-images.githubusercontent.com/1287295/177043599-853d4718-e879-4007-919a-7aee91776c7d.png)

----

## Image Editor
 - Minimalistic / Easy-To-Learn UI for quick edits
 - Save and return to recent seessions
 - Copy to Clipboard or Upload to the web in one click
 - Pin the editor above every other window, so a capture stays visible while you work
 
![picture of image editor](https://user-images.githubusercontent.com/1287295/177043066-46f6fe23-260c-4b06-9c2c-da2970e9f249.png)

## Upload Anything
 - Can upload any file or screenshot from PC with one click
 - URL is copied to clipboard
 - Supports a vareity of file sharing websites
 
![picture of upload](https://user-images.githubusercontent.com/1287295/177044201-1b510910-4211-4eda-9f3c-508fac4c8fba.png)

----

## Accelerated Uploads

Uploading a large file to your own cloud storage normally means waiting for the whole transfer to
finish before you have anything to share. Accelerated uploads remove that wait.

 - **The link is ready instantly.** Clowd copies a shareable URL to your clipboard the moment the
   upload *starts*, not when it finishes.
 - **Recipients don't wait either.** Anyone who opens the link starts downloading right away — the
   bytes stream through to them as they arrive, so they can begin watching a video or viewing an
   image while you are still uploading it.
 - **Your files still land in your own storage.** The transfer is relayed to your bucket in the
   background. Once it completes the link simply redirects there, so nothing stays in the middle.

Enable it with the ⚡ toggle next to any provider on the Upload settings page. It is available for
the providers that upload to storage you own — Azure Blob Storage, Amazon S3 (and S3-compatible
services), Cloudflare R2, and Backblaze B2 — and is on by default for those. Turn it off to upload
directly to your storage as usual.

----

## Color Picker
 - Select any color on screen using the PrntScr Screen Capture. Press 'H' when your cursor is over the desired color.
 - Can also open the color picker from the tray icon.
 
![picture of color picker](https://user-images.githubusercontent.com/1287295/177043307-91a17f2b-3b5f-4b76-9e71-7962cc6cf5e0.png)

----

Interested in building Clowd from source? See [BUILDING.md](BUILDING.md).
