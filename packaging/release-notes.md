## Download

| Your computer | Download |
|---|---|
| Windows 10 or 11 | **Trellis-win-Setup.exe** |
| Mac with Apple silicon (M1 or later) | the **Setup.pkg** with **osx-arm64** in its name |
| Mac with an Intel processor | the **Setup.pkg** with **osx-x64** in its name |
| Linux (64-bit) | **Trellis.AppImage** |

Not sure which Mac you have?  → About This Mac: "Chip: Apple M…" means Apple silicon; "Processor: Intel" means Intel.

Ignore the other files (`.nupkg`, `.json`, `RELEASES`, `Portable.zip`): Trellis uses them to update itself.

## First start

Trellis isn't signed with a paid certificate, so Windows and macOS warn you the first time. After that it starts
normally, and updates install themselves (Trellis offers them when it starts).

- **Windows:** run the Setup. If "Windows protected your PC" appears, click **More info → Run anyway**. Trellis
  installs for your user only (no admin needed) and adds a Start-menu and desktop shortcut.
- **macOS:** open the .pkg. If macOS says it can't be opened, go to **System Settings → Privacy & Security**, scroll
  down and click **Open Anyway** next to the Trellis message, then open it again. Do the same the first time you start
  Trellis if macOS blocks it. When the Tuner or Recorder first listens, allow the microphone.
- **Linux:** make the file executable and run it: `chmod +x Trellis.AppImage && ./Trellis.AppImage`
  (some distributions need the `libfuse2` package for AppImages).

For guitar input, pick your audio interface in the Tuner and Recorder (the system default may be a webcam mic).

Your practice data stays on your computer (`%LOCALAPPDATA%\GuitarPractice` on Windows,
`~/Library/Application Support/GuitarPractice` on macOS, `~/.local/share/GuitarPractice` on Linux); updates and
uninstalling never touch it.
