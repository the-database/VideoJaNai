using System.Globalization;
using System.IO;
using System.Text;

namespace VideoJaNai.Core
{
    // Where aji_encode, its runtime and the models are: the inference folder of a VideoJaNai tree
    // (animejanai/inference), the models next to it (animejanai/onnx, animejanai/rife), and the conf
    // written for a run.
    public sealed record AjiPaths(
        string AjiEncodePath,
        string TrtexecPath,
        string InferenceDirectory,
        string ModelsDirectory,
        string RifeModelsDirectory,
        string ConfPath)
    {
        // A VideoJaNai tree's animejanai folder (the conf goes there too, unless given).
        public static AjiPaths ForAnimeJaNaiFolder(string animeJaNaiDirectory, string? confPath = null)
        {
            var inference = Path.Join(animeJaNaiDirectory, "inference");
            return new AjiPaths(
                Path.GetFullPath(Path.Join(inference, Platform.Exe("aji_encode"))),
                RuntimePacks.TrtexecPath(inference),
                inference,
                Path.Join(animeJaNaiDirectory, "onnx"),
                Path.Join(animeJaNaiDirectory, "rife"),
                confPath ?? Path.Join(animeJaNaiDirectory, "animejanai.conf"));
        }
    }

    public enum AjiMode { Encode, Preview }

    // The libaji conf for a job: libaji's config_version=2 schema (see AnimeJaNaiConfEditor), slot 1.
    // No backend_path, per-chain final_resize or per-chain TensorRT settings in this schema.
    public static class AjiConf
    {
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;

        public static string Build(EncodeJob job)
        {
            var configText = new StringBuilder();
            configText.AppendLine("[global]");
            configText.AppendLine("config_version=2");
            configText.AppendLine("logging=yes");
            configText.AppendLine($"backend={BackendNames.Conf(job.Backend)}");

            // trt_engine_settings (TensorRT, and TensorRT-RTX: it builds in-process but reads the
            // shape / optimization-level / workspace flags, and its TensorRT fallback uses it all):
            // written only when changed from the engine default, which libaji then uses itself.
            if (job.Backend != Backend.DirectMl &&
                !string.IsNullOrWhiteSpace(job.TrtEngineSettings) &&
                job.TrtEngineSettings != TrtEngine.DefaultSettings)
            {
                configText.AppendLine($"trt_engine_settings={job.TrtEngineSettings}");
            }

            configText.AppendLine("[slot_1]");
            configText.AppendLine("profile_name=encode");
            for (var c = 0; c < job.Chains.Count; c++)
            {
                var chain = job.Chains[c];
                var p = $"chain_{c + 1}_";
                configText.AppendLine($"{p}min_resolution={chain.MinResolution}");
                configText.AppendLine($"{p}max_resolution={chain.MaxResolution}");
                configText.AppendLine($"{p}min_fps=0");
                configText.AppendLine($"{p}max_fps=0");

                // aji resolves models by bare name against --model-dir (StageModels copies them in)
                for (var i = 0; i < chain.Models.Count; i++)
                {
                    var m = chain.Models[i];
                    var n = i + 1;
                    configText.AppendLine(string.Create(English, $"{p}model_{n}_resize_height_before_upscale={m.ResizeHeight}"));
                    configText.AppendLine(string.Create(English, $"{p}model_{n}_resize_factor_before_upscale={m.ResizeFactor}"));
                    configText.AppendLine($"{p}model_{n}_name={Path.GetFileNameWithoutExtension(m.OnnxPath)}");
                    if (m.TemporalSceneThreshold is decimal threshold)
                    {
                        configText.AppendLine(string.Create(English, $"{p}model_{n}_temporal_scene_threshold={threshold}"));
                    }
                }

                var r = job.Rife;
                configText.AppendLine($"{p}rife={(r.Enabled ? "yes" : "no")}");
                configText.AppendLine($"{p}rife_model={r.Model}");
                configText.AppendLine(string.Create(English, $"{p}rife_factor_numerator={r.Numerator}"));
                configText.AppendLine(string.Create(English, $"{p}rife_factor_denominator={r.Denominator}"));
                configText.AppendLine(string.Create(English, $"{p}rife_scene_detect_threshold={r.SceneDetectThreshold}"));
                configText.AppendLine($"{p}rife_ensemble={(r.Ensemble ? "yes" : "no")}");
                configText.AppendLine($"{p}rife_before_upscale={(r.BeforeUpscale ? "yes" : "no")}");
            }
            return configText.ToString();
        }

        // Copies each model the job names into the models folder, where aji resolves them by name
        // (a model already there is left as it is).
        public static void StageModels(EncodeJob job, string modelsDirectory)
        {
            Directory.CreateDirectory(modelsDirectory);
            foreach (var m in job.AllModels)
            {
                var target = Path.Combine(modelsDirectory, Path.GetFileName(m.OnnxPath));
                if (Path.GetFullPath(target) != Path.GetFullPath(m.OnnxPath))
                {
                    File.Copy(m.OnnxPath, target, true);
                }
            }
        }

        // StageModels, then the conf at paths.ConfPath.
        public static void Write(EncodeJob job, AjiPaths paths)
        {
            StageModels(job, paths.ModelsDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.ConfPath))!);
            File.WriteAllText(paths.ConfPath, Build(job));
        }
    }

    // aji_encode's command line for a job.
    public static class AjiArgs
    {
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;

        // previewPath: --preview <jpg> for an encode (aji_encode keeps a JPEG of its latest frame
        // there and prints PREVIEW lines); null for none.
        public static string Build(EncodeJob job, AjiPaths paths, string inputFilePath, string? outputFilePath,
                                   AjiMode mode, string? previewPath = null)
        {
            var sb = new StringBuilder();
            sb.Append(English, $"--input \"{inputFilePath}\" ");
            sb.Append(English, $"--conf \"{paths.ConfPath}\" ");
            sb.Append("--slot 1 ");
            sb.Append(English, $"--model-dir \"{paths.ModelsDirectory}\" ");
            sb.Append(English, $"--rife-model-dir \"{paths.RifeModelsDirectory}\" ");
            sb.Append(English, $"--trtexec \"{paths.TrtexecPath}\" ");
            sb.Append(English, $"--backend {BackendNames.Arg(job.Backend)} ");
            if (job.Device is string device)
            {
                sb.Append(English, $"--device {device} ");
            }

            // Engine builds see it too: deinterlacing changes the frame rate chains are matched on.
            if (job.Deinterlace != "off")
            {
                sb.Append(English, $"--deinterlace {job.Deinterlace} ");
            }

            // Engine builds too: stretched before processing, the frames the chain sees change size.
            if (job.SquarePixels != "off")
            {
                sb.Append(English, $"--square-pixels {job.SquarePixels} ");
            }

            // The preview session takes the encode's settings too: the output format and the final
            // resize change the frames it shows, and the encoder is validated against them.
            if (mode == AjiMode.Encode)
            {
                sb.Append(English, $"--output \"{outputFilePath}\" ");
            }

            var (vcodec, vquality) = SplitFfmpegSettings(job.FfmpegVideoSettings);
            sb.Append(English, $"--vcodec {vcodec} ");
            if (mode == AjiMode.Encode && !string.IsNullOrWhiteSpace(vquality))
            {
                sb.Append(English, $"--vquality \"{vquality}\" ");
            }

            sb.Append(English, $"--pix-fmt {job.PixFmt} ");

            // final_resize has no conf field in the engine; aji_encode takes it directly.
            if (job.FinalResizeHeight is int frh && frh > 0)
            {
                sb.Append(English, $"--final-resize-height {frh} ");
            }
            else if (job.FinalResizeFactor is int frf && frf > 0 && frf != 100)
            {
                sb.Append(English, $"--final-resize-factor {frf} ");
            }

            if (mode == AjiMode.Preview)
            {
                sb.Append("--preview-session ");
            }
            else
            {
                if (job.Overwrite)
                {
                    sb.Append("--overwrite ");
                }
                // only when on: an aji_encode from before the option doesn't know it (and stops at
                // damage anyway, without saying where)
                if (job.ContinueOnDamage)
                {
                    sb.Append("--on-damage continue ");
                }
                if (previewPath != null)
                {
                    sb.Append(English, $"--preview \"{previewPath}\" ");
                }
            }

            sb.Append("--progress line");
            return sb.ToString();
        }

        // Splits a VideoJaNai ffmpeg preset ("<codec> <encoder args...>") into the aji_encode
        // --vcodec / --vquality pair. The muxer-level option is dropped (aji_encode owns muxing).
        public static (string vcodec, string vquality) SplitFfmpegSettings(string settings)
        {
            var cleaned = settings.Replace("-max_interleave_delta 0", "").Trim();
            var spaceIdx = cleaned.IndexOf(' ');
            if (spaceIdx < 0)
            {
                return (cleaned, string.Empty);
            }
            return (cleaned[..spaceIdx], cleaned[(spaceIdx + 1)..].Trim());
        }
    }
}
