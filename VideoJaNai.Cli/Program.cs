using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VideoJaNai.Core;

namespace VideoJaNai.Cli
{
    public static class Program
    {
        static string Version =>
            typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

        static string Help => $$"""
videojanai {{Version}}: anime video upscaling from the command line, the command-line counterpart of
VideoJaNai (TensorRT, also TensorRT-RTX and DirectML on Windows), through aji_encode and libaji.

Usage:
  videojanai [inputs...] [options]       upscale (settings from {{Config.FileName}} and/or flags)
  videojanai init [file]                 write a commented {{Config.FileName}} to start from
  videojanai devices                     list the GPUs (the numbers --device takes) and encoders
  videojanai setup [--no-rife] [--check] download what this PC needs (the GPU runtime and the RIFE
                                         models), next to the program; run it again after an
                                         update (--progress lines: also report on stdout for
                                         programs)

With no inputs on the command line, [input] path from the settings file is used. The settings file
is -c <file>, else {{Config.FileName}} in the current folder, else next to the program; flags
override it, and it overrides a workflow it imports.

Inputs and output:
  <inputs...>               videos (any container libav reads) or folders (searched recursively;
                            the output keeps their subfolders)
  -o, --output <dir|file>   where results go (default: next to each video; for a folder,
                            "<folder> animejanai"); with one video, a file name like out.mkv
  --filename <template>     output name, %filename% = the video's name (default
                            %filename%-animejanai.mkv); the extension picks the container
  --suffix <text>           or just what's added: ep01.mkv -> ep01<text>.mkv
  --skip-existing           keep outputs that exist (default)    --overwrite   redo them
  --stop-on-damage          a video with damaged frames (TV recordings, incomplete downloads)
                            fails, saying where (default)
  --keep-damaged            finish it instead, and list the damaged spots
  -c, --config <file>       settings file to use    --no-config   ignore any settings file

Upscaling (one of; default: --preset balanced):
  --preset <p>              balanced: AnimeJaNai HD V3.1 Balanced for 720p-1080p sources, the SD
                            model below 720p; also performance, balanced-sharp, performance-sharp.
                            Sources above 1080p keep their size.
  --model <file.onnx>       this model for every source (a path, or a name in the models folder);
                            repeat it to run models one after another
  --workflow <file.awf>     a workflow exported from VideoJaNai (all of its settings)
  --no-upscale              no models: only the steps below
Source:
  --deinterlace <d>         off (default) | ivtc (telecined film) | bwdif (interlaced video)
  --square-pixels <s>       off (default) | before | after the upscale (DVD, DV)
Temporal fix (vs_temporalfix, steadies shimmer):
  --temporal-fix <1-3>      on, at this strength (0: off)
  --temporal-fix-before-upscale   at the source resolution: faster, but not for the upscale's shimmer
Interpolation (RIFE):
  --rife                    on (2x)    --rife-factor <n|a/b>   e.g. 2 or 5/2
  --rife-model <v>          e.g. 4.22 (default), 4.25-lite    --rife-ensemble
  --rife-after-upscale      interpolate the upscaled frames (default: before, faster)
Encoding:
  --encoder <e>             auto (default: HEVC on the upscaling GPU, else x265) | nvenc-hevc |
                            nvenc-av1 | nvenc-lossless | amf-hevc | amf-av1 | qsv-hevc | qsv-av1 |
                            x265 | x264 | ffv1
  --ffmpeg "<enc> <opts>"   your own encoder settings, e.g. "libx265 -crf 18 -preset slow"
  --pix-fmt <f>             yuv420p10 (default) | yuv420p | yuv444p10 | yuv444p (4:4:4: CPU encoders)
  --height <px>             final height after upscaling    --resize-percent <n>   or a percentage
GPU:
  --backend <b>             auto (default: TensorRT-RTX where installed, else TensorRT on NVIDIA,
                            else DirectML) | tensorrt | tensorrt-rtx | directml
  --device <d>              auto (default) | a GPU number from `videojanai devices` | its name
  --engine-settings "<f>"   TensorRT engine build flags (trtexec style)
  --aji-dir <dir>           a VideoJaNai animejanai folder to run (default: next to the program,
                            else an installed VideoJaNai's)

VideoJaNai's saved settings:
  --settings <appstate2.json>   run the GUI's selected workflow (its input, output and App
                                Settings' backend and GPU); flags still override it

Output:
  -v, --verbose             aji_encode's log    --progress lines   machine-readable lines on stdout
  -h, --help                -V, --version
""";

        public static async Task<int> Main(string[] args)
        {
            try
            {
                return await Dispatch(args);
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 2;
            }
            catch (IniException e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 2;
            }
            catch (RunException e)
            {
                foreach (var line in e.Message.Split('\n'))
                {
                    Console.Error.WriteLine($"error: {line}");
                }
                return 2;
            }
        }

        static async Task<int> Dispatch(string[] args)
        {
            var list = args.ToList();
            string? sub = list.Count > 0 && list[0] is "init" or "devices" or "setup" && !File.Exists(list[0]) && !Directory.Exists(list[0])
                ? list[0] : null;
            if (sub != null)
            {
                list.RemoveAt(0);
            }
            var f = Flags.Parse(list);
            if (f.Help)
            {
                Console.Out.Write(Help);
                return 0;
            }
            if (f.Version)
            {
                Console.Out.WriteLine($"videojanai {Version}");
                return 0;
            }
            switch (sub)
            {
                case "init": return Init(f);
                case "devices": return await Devices(f);
                case "setup": return await Setup(f);
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                // stop aji_encode and clean up, then exit (a second Ctrl+C ends at once)
                if (!cts.IsCancellationRequested)
                {
                    e.Cancel = true;
                    cts.Cancel();
                }
            };
            // `kill` (SIGTERM) too, on Linux: without this, .NET exits at once and leaves aji_encode
            // running and the partial file behind
            using var sigterm = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        ctx.Cancel = true;
                        cts.Cancel();
                    }
                });
            var plan = await Resolve.PlanAsync(f);
            Report report = f.Lines ? new LinesReport() : new HumanReport(f.Verbose);
            try
            {
                return await Run.ExecuteAsync(plan, report, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Stopped.");
                return 130;
            }
        }

        static int Init(Flags f)
        {
            var path = f.Positional.FirstOrDefault() ?? Config.FileName;
            if (f.Positional.Count > 1)
            {
                throw new UsageException("init takes one file name");
            }
            if (File.Exists(path))
            {
                throw new UsageException($"{path} exists already; delete it first, or give another name");
            }
            File.WriteAllText(path, Config.Template.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            Console.Error.WriteLine($"Wrote {Path.GetFullPath(path)}. Edit it, then run `videojanai`.");
            return 0;
        }

        static async Task<int> Devices(Flags f)
        {
            var install = Install.Find(f.Options.AjiDir);
            if (!install.AjiEncodeExists)
            {
                throw new RunException($"aji_encode not found in {install.Paths.InferenceDirectory}; run `videojanai setup`");
            }
            var probe = EncoderProbe.Parse(await install.ProbeAsync(fresh: true))?.WithNvidia(Nvml.Gpus)
                        ?? throw new RunException("this aji_encode can't list GPUs (--probe-encoders); update VideoJaNai");
            Console.Out.WriteLine("GPUs (the number is what --device takes):");
            for (var i = 0; i < probe.Gpus.Count; i++)
            {
                var g = probe.Gpus[i];
                var backends = new List<string>();
                if (g.TensorRtCapable)
                {
                    backends.Add("TensorRT");
                    if (Platform.TensorRtRtxAvailable)
                    {
                        backends.Add("TensorRT-RTX");
                    }
                }
                if (Platform.DirectMlAvailable)
                {
                    backends.Add("DirectML");
                }
                var tags = new List<string>();
                if (g.IsDefault && Platform.DirectMlAvailable)
                {
                    tags.Add("DirectML's default");
                }
                if (g.Integrated == true)
                {
                    tags.Add("integrated");
                }
                if (g.Vendor == EncoderGroup.Nvidia && !g.TensorRtCapable && g.Nvidia is NvidiaGpu old)
                {
                    // TensorRT needs compute capability 7.5+ (GeForce RTX 20 / GTX 16 series or newer)
                    tags.Add($"too old for TensorRT: compute {old.ComputeCapability}, it needs {TensorRtGpus.MinText}");
                }
                var tagText = tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "";
                Console.Out.WriteLine($"  {i}  {EncoderProbe.Label(probe.Gpus, g)}{tagText}: {(backends.Count > 0 ? string.Join(", ", backends) : "none")}");
            }
            if (probe.Gpus.Count == 0)
            {
                Console.Out.WriteLine("  (none listed by this aji_encode)");
            }
            var usable = EncoderNames.All.Where(e => e.Preset.IsGpu && probe.Tested(e.Preset.Codec) && probe.Opens(e.Preset.Codec)).ToList();
            Console.Out.WriteLine("GPU encoders that work here: " + (usable.Count > 0
                ? string.Join(", ", usable.Select(e => probe.Opens(e.Preset.Codec, tenBit: true) ? e.Name : $"{e.Name} (8-bit only)"))
                : "none (x265, x264 and ffv1 always work)"));
            if (f.Verbose)
            {
                Console.Out.WriteLine($"aji_encode: {install.Paths.AjiEncodePath}");
            }
            return 0;
        }

        // The updater next to the program installs the packs this PC needs into the program's
        // folder (the same packs, and the same choice of them, as VideoJaNai's installer and App
        // Settings make).
        static async Task<int> Setup(Flags f)
        {
            var updater = Path.Join(Install.ExeDir, Platform.Exe("VideoJaNaiUpdater"));
            if (!File.Exists(updater))
            {
                throw new RunException($"{Path.GetFileName(updater)} isn't next to videojanai; setup works from the videojanai archive (videojanai-windows-x64.zip / videojanai-linux-x64.tar.zst)");
            }
            // aji-runtime can't install into a store whose folder is missing
            if (await Install.MissingStoreFolderAsync(Install.ExeDir) is string missing)
            {
                throw new RunException(Install.StoreMissingText(missing));
            }
            var args = new List<string> { "--auto" };
            var o = f.Options;
            if (o.Backend == "directml")
            {
                args.AddRange(["--gpus", "none"]);
            }
            else if (!string.IsNullOrWhiteSpace(o.Device) && !o.Device.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var install = Install.Find(null);
                var probe = EncoderProbe.Parse(await install.ProbeAsync())?.WithNvidia(Nvml.Gpus)
                            ?? throw new RunException("can't pick a GPU: aji_encode can't list them");
                var t = o.Device.Trim();
                var gpu = (int.TryParse(t, out var n) && n >= 0 && n < probe.Gpus.Count ? probe.Gpus[n] : null)
                          ?? probe.Gpus.FirstOrDefault(g => g.Id == t || g.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
                          ?? throw new RunException($"no GPU '{t}' (see `videojanai devices`)");
                args.AddRange(["--gpus", gpu.TensorRtCapable && gpu.Id != null ? gpu.Id : "none"]);
            }
            // RIFE: wanted, or left out even on a fresh folder (where the updater preselects it)
            args.AddRange([f.NoRife ? "--exclude" : "--include", "rife"]);
            if (f.Check)
            {
                args.Add("--check");
            }
            if (f.Lines)
            {
                args.AddRange(["--progress", "lines"]);
            }
            var psi = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = Install.ExeDir };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            if (!f.Lines)
            {
                // human mode: the updater's text on stderr, as the rest of videojanai's
                psi.RedirectStandardOutput = true;
            }
            using var p = Process.Start(psi) ?? throw new RunException($"couldn't start {updater}");
            if (!f.Lines)
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    Console.Error.WriteLine(line);
                }
            }
            await p.WaitForExitAsync();
            if (p.ExitCode == 0 && !f.Check && !f.Lines)
            {
                Console.Error.WriteLine("Setup done. Run `videojanai devices` to see the GPUs, or `videojanai <video>` to upscale.");
            }
            return p.ExitCode;
        }
    }
}
