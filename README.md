# TaskbarVisu

*English · [Français](README.fr.md)*

**Audio visualizers living inside the Windows 11 taskbar.** No window, no frame: tiny lab-grade meters
drawn straight into the empty space on each side of your icons, in a dot-matrix / y2k / test-equipment style.

![TaskbarVisu on the taskbar, three palettes](docs/showcase.png)

```
> CAPTURE ........ WASAPI loopback, everything your PC plays
> RENDER ......... SkiaSharp into layered Win32 windows, click-through, ~0.5 ms per frame
> FOOTPRINT ...... ~3 % of one core at 60 fps, ~0 % in silence
> LAYOUT ......... auto-detects the taskbar (top/bottom, compact, DPI) and widgets like FluentFlyout
```

## Modules

![The nine modules](docs/modules.png)

| | |
|---|---|
| **Levels + LUFS** | segmented L/R peak meter, peak hold, clip, BS.1770 loudness with target |
| **Spectrogram** | scrolling frequency history, coloured by intensity |
| **Spectrum** | bars, LED dots, filled curve or line, held peaks, detected note |
| **VU meter** | LED arc or analogue needle, ~300 ms ballistics, mono or stereo |
| **Stereo imager** | polar vectorscope by band + panorama/width per frequency, phase correlation |
| **Tuner / key** | note + cents, or the song's key on a mini piano (English, solfège or Camelot) |
| **Waveform** | mirror, envelope or triggered oscilloscope |
| **Loudness history** | LUFS over 15 s to 5 min against your target |
| **Piano roll** | the notes actually played, octave included, on a 3–6 octave keyboard with scrolling history |

Pick, order and size them per side; each module can use the global palette, another palette or a solid colour.

## Key detection that knows when to shut up

- **Only sustained music counts.** Peaks must stay put from one block to the next: speech glides, coughs
  have no stable peaks, notification sounds are too short. Silent Discord call → `--`.
- **Harmonics are subtracted**, low to high, before anything is shown in the piano roll: a 5th harmonic
  never becomes a fake major third.
- **Modal songs get the truth, not a coin flip.** When a track sits on another note than its scale's tonic,
  the tuner says so: `B♭ MAJ ◆F MIX` = notes of B♭ major, resting on F (Mixolydian).
- Unsure major/minor shows as `MAJ?` / `MIN?` instead of a confident wrong answer.
- Checked against a permanent test bench (real samples, full songs referenced on Tunebat/Hooktheory/Beatport,
  synthetic songs, chords, speech and noise): see [tests/key-bench.csv](tests/key-bench.csv).

## Settings

![Settings window](docs/settings.png)

One-click presets (Lab, MiniMeters-like, CRT Phosphor, Synthwave, Vaporwave, Chrome Y2K, Toxic, RGB),
14 palettes including animated RGB, real colour picker, pixel or smooth font, scanlines, per-module options
tucked behind *More options*. UI in English, French, Spanish and German (follows Windows).

## Download

Grab the latest build on the [Releases](../../releases) page:

- **Installer** `TaskbarVisu-x.y.z-setup.exe`: per-user install (no admin rights), Start menu entry,
  clean uninstall.
- **Portable** `TaskbarVisu-x.y.z-portable.zip`: one self-contained `.exe`, nothing to install.
  Settings live in `%APPDATA%\TaskbarVisu`.

Windows 11 x64, horizontal taskbar. The binaries are not code-signed, so SmartScreen may show
*Unknown publisher*: click *More info → Run anyway*.

## Build

.NET 10 SDK:

```
dotnet run --project src/TaskbarVisu -c Release
```

Portable build and installer (Inno Setup 6):

```
dotnet publish src/TaskbarVisu -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o build/portable
ISCC.exe /DAppVersion=0.8.3 installer/TaskbarVisu.iss
```

Detailed technical documentation (DSP, test bench, diagnostics flags) is in [README.fr.md](README.fr.md) (French).

## License

[GPL-3.0](LICENSE). Uses [NAudio](https://github.com/naudio/NAudio) (MIT) and
[SkiaSharp](https://github.com/mono/SkiaSharp) (MIT). Inspired by MiniMeters; not affiliated with it.
