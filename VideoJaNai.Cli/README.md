# videojanai

Anime video upscaling from the command line, with ONNX models on TensorRT (also TensorRT-RTX and
DirectML on Windows), temporal fix and RIFE frame interpolation. The command-line counterpart of
[VideoJaNai](https://github.com/the-database/VideoJaNai): the same engine
([libaji](https://github.com/the-database/animejanai-inference)'s `aji_encode`), the same components
and the same settings code (`VideoJaNai.Core`), so a workflow runs the same in both.

- Videos (any container libav reads) and folders (recursive; the output keeps the subfolders).
- AnimeJaNai presets by source size, your own ONNX models (one or a chain), or a workflow exported
  from VideoJaNai.
- Deinterlacing (IVTC, bwdif), square pixels, temporal fix, RIFE, a final resize.
- NVENC, AMF and QSV on the upscaling GPU, or x265, x264 and FFV1. Audio, subtitles and chapters
  are kept.

## Install

1. Download `videojanai-windows-x64.zip` or `videojanai-linux-x64.tar.zst` from
   [VideoJaNai's releases](https://github.com/the-database/VideoJaNai/releases) and unpack it anywhere.
2. Run `videojanai setup`. It detects the GPU and downloads what this PC needs into the same folder:
   - the TensorRT runtime and the engine-builder kernels for your GPU's generation (NVIDIA);
   - TensorRT-RTX (Windows, RTX 20 / GTX 16 series and newer);
   - the RIFE models (skip them with `--no-rife`).

   DirectML (Windows, any GPU) and the AnimeJaNai models ship in the archive. `videojanai setup
   --check` lists what it would download without downloading; each download is checked against
   its SHA-256. After unpacking a new release over the folder, run `videojanai setup` again: it
   fetches what changed.
3. Run `videojanai <video>`.

All you need beforehand is a GPU driver. TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or
newer GPU (compute capability 7.5+); older NVIDIA GPUs (GTX 10-series and earlier) run on DirectML,
as other GPUs do. On Linux, videojanai runs on TensorRT only, so it needs such a GPU and its driver.

The TensorRT runtime and the GPU kernels go into one folder that all your JaNai apps share
(`%LOCALAPPDATA%\the-database\aji`; on Linux `~/.local/share/the-database/aji`). To put it on
another drive, close the apps that use it and run `aji-runtime move <folder>` (aji-runtime is next
to videojanai); `aji-runtime move --default` moves it back. If that drive is gone for good, run
`aji-runtime reset`, then `videojanai setup`.

On Windows, if you skip setup and VideoJaNai is installed, videojanai uses VideoJaNai's components
and models. `--aji-dir <folder>` runs another VideoJaNai's `animejanai` folder (or an `aji_encode`
folder).

## Use

```sh
videojanai episode.mkv                          # -> episode-animejanai.mkv, next to it
videojanai season1/ -o upscaled/                # a folder with its subfolders
videojanai ep.mkv -o ep-4k.mp4 --height 2160    # one video to a named file, resized after the upscale
videojanai dvd.vob --deinterlace ivtc --square-pixels before --rife
videojanai ep.mkv --model 2x_MyModel.onnx --encoder x265
videojanai ep.mkv --no-upscale --temporal-fix 2 # only steady the shimmer
videojanai ep.mkv --no-upscale --rife-factor 5/2   # only interpolate (24 -> 60 fps)
videojanai devices                              # the GPUs (numbers for --device) and the GPU encoders
```

Outputs that exist already are skipped (`--overwrite` redoes them). Each video is encoded to a
`.videojanai-partial` file next to its output and renamed when it's done, so a stopped or failed
encode never leaves a file that a later run would skip. Ctrl+C stops at once and cleans up.
`videojanai --help` lists every option.

TV recordings and incomplete downloads can have damaged frames. Such a video fails, and the error
says where the damage is (`damaged video at 12:34.5 (frame 18093)`). `--keep-damaged` (or
`keep_damaged = yes` in the settings file) finishes it instead, with a note listing the damaged
spots (`Finished with 3 damaged frames (12:34, 12:35, 41:02)`).

### Settings file

Keep your settings in a file so a run is just `videojanai`:

```sh
videojanai init          # writes a commented videojanai.ini
# edit it in any text editor
videojanai               # runs it
videojanai --rife        # the same, with one setting added for this run
```

```ini
[input]
path = D:\anime\My Show

[output]
folder = D:\anime\upscaled

[upscale]
preset = balanced

[temporal_fix]
strength = 2

[encode]
encoder = nvenc-av1
```

The file is `-c <file>`, else `videojanai.ini` in the current folder, else next to the program.
Flags override it, and it overrides a workflow it imports (`[upscale] workflow =`). Values are
written as you'd say them: paths as they are (quotes from "Copy as path" are fine), `yes`/`no`,
`2x`, `75%`; a list is the same line repeated; an empty value means the default. Mistakes stop the
run with the line number and, for a misspelled setting, the right name. `videojanai init`
documents every setting.

### Models

With no model given, videojanai uses preset `balanced`: AnimeJaNai HD V3.1 Balanced for 720p to
1080p sources and the SD model below 720p. That is mpv-AnimeJaNai's default, without its frame rate
limit for real-time playback. Sources above 1080p keep their size. Also: `--preset performance`,
`balanced-sharp`, `performance-sharp`.

`--model` runs your own model for every source: a path, or a file name in the models folder
(`animejanai/onnx`). Repeat it to run models one after another. `--no-upscale` runs no model, for
deinterlacing, temporal fix or interpolation alone.

### VideoJaNai's workflows

- `--workflow "My workflow.awf"` imports a workflow exported from VideoJaNai: its models, source,
  temporal fix, interpolation and output settings. Flags and the settings file still override it.
- `--settings appstate2.json` runs VideoJaNai's selected workflow from its saved state
  (`%APPDATA%\VideoJaNai\appstate2.json`, `~/.config/VideoJaNai/appstate2.json`), with its input
  and output and the backend and GPU from its App Settings.

### GPUs and backends

- `--backend auto` (the default) is TensorRT-RTX where it's installed, else TensorRT on an NVIDIA
  GPU it runs on (RTX 20 / GTX 16 series and newer), else DirectML (Windows). `tensorrt`, `tensorrt-rtx` and `directml` pick one. TensorRT-RTX
  builds engines in seconds instead of minutes, and falls back to TensorRT if it can't start.
- `--device <n>` picks the GPU by its number from `videojanai devices` (or its name). The default
  is the backend's: CUDA's first GPU on TensorRT, the high-performance GPU on DirectML.
- `--encoder auto` (the default) is HEVC on the upscaling GPU when it has a hardware encoder,
  else x265. `--ffmpeg "<encoder> <options>"` gives your own encoder settings.

TensorRT builds an engine the first time a model meets a source size (a few minutes, once); the
engines are kept next to the models.

## For programs

`--progress lines` writes one event per line on stdout (the log goes to stderr):

| Line | Meaning |
|---|---|
| `PLAN <videos> <to encode> <to skip>` | what the run will do |
| `VIDEO_START <n> <of> <input>` | a video starts (n counts from 1) |
| `PROGRESS phase=<p> frame=<n> total=<n> fps=<f> pct=<n>` | aji_encode's own progress line, as is (`build_engine`, then `encode`) |
| `VIDEO_SKIP <n> <of> <output>` | the output exists |
| `VIDEO_DONE <n> <of> <output>` | the video is written |
| `VIDEO_ERROR <n> <of> <message>` | the video failed; the run goes on |
| `NOTE <text>` / `BACKEND_FALLBACK <text>` | worth showing: no chain for the size, a video finished with damaged frames (`<input>: Finished with ...`), TensorRT-RTX -> TensorRT |
| `DONE <written> <skipped> <failed>` | the end |

`videojanai setup --progress lines` reports with mangajanai setup's lines: `SETUP_PLAN <pack>
<bytes> installed|download`, `SETUP_TOTAL <bytes>`, `SETUP_DOWNLOAD <pack> <got> <total>`,
`SETUP_EXTRACT <pack>` and `SETUP_DONE`.

Exit codes: 0 done, 1 some videos failed, 2 the run couldn't start (the reason is on stderr), 130
stopped.

## Build

```sh
dotnet build VideoJaNai.Cli
dotnet test VideoJaNai.Core.Tests
dotnet publish VideoJaNai.Cli -c Release -r win-x64     # or linux-x64: NativeAOT, a single small file
```

NativeAOT needs the Visual Studio C++ tools on Windows, or clang and zlib on Linux. The release
workflow builds the archives with `BuildVideoJaNai --cli` (see the repo's `deploy.yml`).

## License

GPL-3.0, like VideoJaNai. See [LICENSE](../LICENSE).
