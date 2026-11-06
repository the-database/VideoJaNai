using System;
using System.IO;
using System.Linq;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // aji-runtime.json, which the shared pack store writes beside libaji. Tests don't set
    // AJI_RUNTIME_JSON (process-wide), so the file is the inference folder's.
    public class RuntimePacksTests
    {
        private static string Json(string s) => s.Replace("\\", "\\\\");

        [Fact]
        public void WithoutTheFileEverythingIsInTheInferenceFolder()
        {
            using var t = new TempDir();
            var inference = Path.Join(t.Path, "inference");
            Directory.CreateDirectory(inference);
            Assert.Null(RuntimePacks.Read(inference));
            Assert.Equal(Path.GetFullPath(Path.Join(inference, Platform.Exe("trtexec"))), RuntimePacks.TrtexecPath(inference));
            var env = AjiProcess.Environment(inference);
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(env);
            }
            else
            {
                Assert.StartsWith(inference, env!["LD_LIBRARY_PATH"]);
            }
        }

        [Fact]
        public void TheStorePacksAreUsedWhenTheFileNamesThem()
        {
            using var t = new TempDir();
            var inference = Path.Join(t.Path, "inference");
            var trt = Path.Join(t.Path, "store", "trt-runtime", "1");
            var rtx = Path.Join(t.Path, "store", "trtrtx-runtime", "1");
            t.File(Path.Join("store", "trtrtx-runtime", "1", "tensorrt_rtx_1_7.dll"));
            t.File(Path.Join("inference", "aji_trtrtx.dll"));
            t.File(Path.Join("inference", "aji-runtime.json"),
                $"{{\"format\": 1, \"trt\": \"{Json(trt)}\", \"trt_builder\": [\"../store/trt-sm120/1\"], \"trtrtx\": \"{Json(rtx)}\"}}");

            var packs = RuntimePacks.Read(inference)!;
            Assert.Equal(Path.GetFullPath(trt), packs.Trt);
            Assert.Equal(Path.GetFullPath(Path.Join(t.Path, "store", "trt-sm120", "1")), packs.TrtBuilder.Single());
            Assert.Equal(Path.GetFullPath(Path.Join(trt, Platform.Exe("trtexec"))), RuntimePacks.TrtexecPath(inference));
            Assert.Equal(Path.GetFullPath(Path.Join(trt, Platform.Exe("trtexec"))), AjiPaths.ForAnimeJaNaiFolder(t.Path).TrtexecPath);
            Assert.True(RuntimePacks.TensorRtRtxInstalled(inference));

            // aji_encode links cudart itself: the TensorRT pack goes on its library path
            var env = AjiProcess.Environment(inference)!;
            if (OperatingSystem.IsWindows())
            {
                Assert.StartsWith(Path.GetFullPath(trt) + ";", env["PATH"]);
            }
            else
            {
                Assert.StartsWith($"{inference}:{Path.GetFullPath(trt)}", env["LD_LIBRARY_PATH"]);
            }
        }

        [Fact]
        public void TensorRtRtxNeedsItsRuntimeAndTheBackend()
        {
            using var t = new TempDir();
            var inference = Path.Join(t.Path, "inference");
            t.File(Path.Join("inference", "aji_trtrtx.dll"));
            Assert.False(RuntimePacks.TensorRtRtxInstalled(inference));
            t.File(Path.Join("inference", "tensorrt_rtx_1_7.dll"));
            Assert.True(RuntimePacks.TensorRtRtxInstalled(inference));
            // a file naming a pack that's gone: not installed
            t.File(Path.Join("inference", "aji-runtime.json"), "{\"format\": 1, \"trtrtx\": \"missing\"}");
            Assert.False(RuntimePacks.TensorRtRtxInstalled(inference));
        }

        [Fact]
        public void AnUnreadableFileIsIgnored()
        {
            using var t = new TempDir();
            var inference = Path.Join(t.Path, "inference");
            t.File(Path.Join("inference", "aji-runtime.json"), "{ not json");
            Assert.Null(RuntimePacks.Read(inference));
        }
    }
}
