
# <img src="logo.png" width="24"></img> VideoJaNai
[![Discord](https://img.shields.io/discord/1121653618173546546?label=Discord&logo=Discord&logoColor=white)](https://discord.gg/EeFfZUBvxj)
## Overview
VideoJaNai (previously called AnimeJaNaiConverterGui) provides a Windows and Linux GUI for upscaling videos with ONNX models, including temporal (multi-frame) models, and frame interpolation with RIFE. Upscaling runs entirely on the GPU, with NVIDIA TensorRT or, on Windows, DirectML for AMD and Intel GPUs, through the [AnimeJaNai inference engine](https://github.com/the-database/animejanai-inference), the same engine that powers [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai).

On Windows, NVIDIA GeForce RTX 20 / GTX 16 series and newer GPUs upscale with **TensorRT-RTX** by default, which builds its engines in seconds instead of minutes. Its component (about 230 MB installed) is installed automatically for those GPUs; on other NVIDIA GPUs it's optional, from App Settings > Components. TensorRT stays installed alongside it: TensorRT-RTX output is equivalent to TensorRT's but not bit-identical, and if it can't start or can't build an engine, VideoJaNai falls back to TensorRT automatically and shows a warning in the status bar (and in the preview). To choose a backend yourself, pick TensorRT, TensorRT-RTX or DirectML under Backend in App Settings.

On a PC with more than one GPU the backend can use, App Settings also has a GPU choice. Auto runs DirectML on the GPU Windows ranks for high performance and TensorRT on the NVIDIA GPU CUDA lists first (usually the most powerful); pick a GPU there if Auto chooses the wrong one. Integrated graphics are listed last and marked, and picking them shows a warning, since they upscale much slower than a graphics card. On Auto, the TensorRT components are installed for every NVIDIA GPU in the PC; with a GPU picked, only that GPU's are recommended (none on DirectML), and components you remove for other GPUs stay removed.

### Linux
Download either the AppImage or the `linux-x64.tar.zst` from the [latest release](https://github.com/the-database/VideoJaNai/releases/latest). An NVIDIA GPU and driver are required.
- **AppImage**: make it executable and run it. Run it once with `--install` to add a menu entry. The TensorRT kernels for your GPU and the RIFE models download on first launch into `~/.local/share/VideoJaNai`.
- **Tarball**: extract it anywhere and run `./install.sh` from inside the extracted folder. This adds a menu entry and installs the components for your GPU. The folder stays in place and updates itself in-app.

### Command line
`videojanai` upscales from the command line, with the same engine and settings as the GUI. Download `videojanai-windows-x64.zip` or `videojanai-linux-x64.tar.zst` from the [latest release](https://github.com/the-database/VideoJaNai/releases/latest), unpack it anywhere, and run `videojanai setup` once to download the components for your GPU (and the RIFE models, unless `--no-rife`). On Windows, if you skip setup and VideoJaNai is installed, it uses VideoJaNai's components.

```sh
videojanai episode.mkv                         # -> episode-animejanai.mkv, next to it
videojanai season1/ -o upscaled/               # a folder, with its subfolders; existing outputs are skipped
videojanai dvd.vob --deinterlace ivtc --rife   # IVTC, upscale, then interpolate to 2x the frame rate
videojanai ep.mkv --no-upscale --temporal-fix 2   # only steady the shimmer
videojanai ep.mkv --workflow "My workflow.awf"    # a workflow exported from VideoJaNai
videojanai devices                             # the GPUs (for --device) and the GPU encoders that work
```

With no model given it uses preset `balanced`: AnimeJaNai HD V3.1 Balanced for 720p to 1080p sources and the SD model below 720p (sources above 1080p keep their size). `--model` runs your own ONNX models. `videojanai init` writes a commented `videojanai.ini` to keep settings in, `--settings appstate2.json` runs the GUI's selected workflow, and `--progress lines` reports progress on stdout for other programs. `videojanai --help` lists every option.

<img width="1908" height="1019" alt="image" src="https://github.com/user-attachments/assets/5592bbed-b0af-4624-9d87-1f4081458ad7" />




## Resources
- [OpenModelDB](https://openmodeldb.info/): Repository of AI upscaling models.
- [chaiNNer](https://github.com/chaiNNer-org/chaiNNer): General purpose tool for AI upscaling, which can be used to convert PyTorch (*.pth) models to ONNX models. 

## Related Projects
- [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai): Real-time anime upscaling in mpv
- [MangaJaNai GUI](https://github.com/the-database/MangaJaNaiGUI): Upscale manga and images
- [yosh](https://github.com/the-database/yosh): Fast manga, comic and image reader
- [traiNNer-redux](https://github.com/the-database/traiNNer-redux): Train your own upscaling models

## Acknowledgments 
- [MPV_lazy](https://github.com/hooke007/MPV_lazy) 
- [vs-mlrt](https://github.com/AmusementClub/vs-mlrt)
- [vs_temporalfix](https://github.com/pifroggi/vs_temporalfix): the temporal fix models (Apache-2.0)
