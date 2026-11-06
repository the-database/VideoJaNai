using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoJaNai.Core
{
    // The shared pack store's aji-runtime.json, which the updater has aji-runtime write beside libaji
    // (animejanai/inference): the store folders libaji loads TensorRT, its builder kernels and
    // TensorRT-RTX's runtime from. Without the file (an install from before the store, the AppImage, the
    // offline package before its first update) everything is in the inference folder, as before.
    // AJI_RUNTIME_JSON names another file, as it does for libaji.
    public sealed record RuntimePacks(string? Trt, IReadOnlyList<string> TrtBuilder, string? TrtRtx)
    {
        public const string FileName = "aji-runtime.json";

        public static string FilePath(string inferenceDirectory) =>
            System.Environment.GetEnvironmentVariable("AJI_RUNTIME_JSON") is { Length: > 0 } env
                ? env
                : Path.Join(inferenceDirectory, FileName);

        // Null without a readable file. Relative paths resolve against the file's folder, as libaji's.
        public static RuntimePacks? Read(string inferenceDirectory)
        {
            var path = FilePath(inferenceDirectory);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                string? Dir(JsonElement e) =>
                    e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s
                        ? Path.GetFullPath(Path.IsPathRooted(s) ? s : Path.Join(dir, s))
                        : null;
                string? Key(string k) => root.TryGetProperty(k, out var v) ? Dir(v) : null;
                var builders = root.TryGetProperty("trt_builder", out var b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray().Select(Dir).OfType<string>().ToList()
                    : [];
                return new RuntimePacks(Key("trt"), builders, Key("trtrtx"));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // trtexec: the TensorRT pack's when the file names one (libaji runs that one), else the
        // inference folder's.
        public static string TrtexecPath(string inferenceDirectory) =>
            Path.GetFullPath(Path.Join(Read(inferenceDirectory)?.Trt ?? inferenceDirectory, Platform.Exe("trtexec")));

        // TensorRT-RTX (Windows): aji_trtrtx.dll ships with libaji in the inference folder; its runtime
        // is the store's pack, or (an install from before the store) in the inference folder too.
        public static bool TensorRtRtxInstalled(string inferenceDirectory)
        {
            if (!File.Exists(Path.Join(inferenceDirectory, "aji_trtrtx.dll")))
            {
                return false;
            }
            var runtime = Read(inferenceDirectory)?.TrtRtx ?? inferenceDirectory;
            try
            {
                return Directory.Exists(runtime) && Directory.EnumerateFiles(runtime, "tensorrt_rtx_*.dll").Any();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
