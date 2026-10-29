
# <img src="logo.png" width="24"></img> VideoJaNai
[![Discord](https://img.shields.io/discord/1121653618173546546?label=Discord&logo=Discord&logoColor=white)](https://discord.gg/EeFfZUBvxj)
## Overview
VideoJaNai (previously called AnimeJaNaiConverterGui) provides a Windows and Linux GUI for upscaling videos with ONNX models, including temporal (multi-frame) models, and frame interpolation with RIFE. Upscaling runs entirely on the GPU, with NVIDIA TensorRT or, on Windows, DirectML for AMD and Intel GPUs, through the [AnimeJaNai inference engine](https://github.com/the-database/animejanai-inference), the same engine that powers [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai).

### Linux
Download either the AppImage or the `linux-x64.tar.zst` from the [latest release](https://github.com/the-database/VideoJaNai/releases/latest). An NVIDIA GPU and driver are required.
- **AppImage**: make it executable and run it. Run it once with `--install` to add a menu entry. The TensorRT kernels for your GPU and the RIFE models download on first launch into `~/.local/share/VideoJaNai`.
- **Tarball**: extract it anywhere and run `./install.sh` from inside the extracted folder. This adds a menu entry and installs the components for your GPU. The folder stays in place and updates itself in-app.

<img width="1908" height="1019" alt="image" src="https://github.com/user-attachments/assets/5592bbed-b0af-4624-9d87-1f4081458ad7" />




## Resources
- [OpenModelDB](https://openmodeldb.info/): Repository of AI upscaling models.
- [chaiNNer](https://github.com/chaiNNer-org/chaiNNer): General purpose tool for AI upscaling, which can be used to convert PyTorch (*.pth) models to ONNX models. 

## Related Projects
- [mpv-upscale-2x_animejanai](https://github.com/the-database/mpv-upscale-2x_animejanai): A video player which supports real-time upscaling using compact ONNX models on higher end GPUs.
- [traiNNer-redux](https://github.com/the-database/traiNNer-redux): Software for training upscaling models

## Acknowledgments 
- [MPV_lazy](https://github.com/hooke007/MPV_lazy) 
- [vs-mlrt](https://github.com/AmusementClub/vs-mlrt)
- [vs_temporalfix](https://github.com/pifroggi/vs_temporalfix): the temporal fix models (Apache-2.0)
