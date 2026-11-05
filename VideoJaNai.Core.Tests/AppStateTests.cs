using System;
using System.IO;
using System.Linq;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // The GUI's saved state as Newtonsoft writes it (type names, $values lists, nulls).
    public class AppStateTests
    {
        [Fact]
        public void TheBundledDefaultStateReads()
        {
            var state = AppState.Load(Path.Join(AppContext.BaseDirectory, "appstate2.json"));
            Assert.Equal(10, state.Workflows.Count);
            Assert.Equal("Default Workflow", state.Workflows[0].WorkflowName);
            Assert.Equal(0, state.SelectedWorkflowIndex);
            Assert.Same(state.Workflows[0], state.SelectedWorkflow);
            var wf = state.Workflows[0];
            Assert.Equal("%filename%-animejanai.mkv", wf.OutputFilename);
            Assert.Equal("RIFE 4.14", wf.RifeModel);
            Assert.Equal(0.150M, wf.RifeSceneDetectThreshold);
            Assert.Single(wf.UpscaleSettings);
            Assert.Equal(100.0M, wf.UpscaleSettings[0].ResizeFactorBeforeUpscale);
            // fields the file doesn't have keep the GUI's defaults
            Assert.True(wf.EnableUpscale);
            Assert.Equal("yuv420p10", wf.OutputPixFmt);
            Assert.Equal(TrtEngine.DefaultSettings, wf.TrtEngineSettings);
            // the backend from before it was app-wide: the selected workflow's
            Assert.False(state.UseDirectMl);
        }

        const string State = """
            {
              "$type": "VideoJaNai.ViewModels.MainWindowViewModel, VideoJaNai",
              "UseDirectMl": false,
              "UseTensorRtRtx": true,
              "SelectedGpuId": "9223372036854776064",
              "SelectedGpuName": "NVIDIA GeForce RTX 5090",
              "SelectedGpuPosition": 0,
              "SelectedWorkflowIndex": 1,
              "Workflows": {
                "$type": "Avalonia.Collections.AvaloniaList`1[[VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai]], Avalonia.Base",
                "$values": [
                  { "$type": "VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai", "WorkflowName": "one", "UpscaleSettings": { "$values": [] } },
                  {
                    "$type": "VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai",
                    "WorkflowName": "two",
                    "SelectedTabIndex": 1,
                    "InputFolderPath": "D:\\anime",
                    "Deinterlace": null,
                    "FinalResizeHeight": null,
                    "TrtEngineSettings": "--fp16 --builderOptimizationLevel=4 --tacticSources=-CUDNN",
                    "EnableTemporalFix": true,
                    "TemporalFixStrength": 3,
                    "TemporalFixSceneThreshold": 0.25,
                    "UpscaleSettings": {
                      "$type": "Avalonia.Collections.AvaloniaList`1[[VideoJaNai.ViewModels.UpscaleModel, VideoJaNai]], Avalonia.Base",
                      "$values": [
                        { "OnnxModelPath": "C:\\m\\a.onnx", "ResizeHeightBeforeUpscale": 540, "TemporalSceneThreshold": null }
                      ]
                    }
                  }
                ]
              }
            }
            """;

        [Fact]
        public void AppSettingsAndTheSelectedWorkflow()
        {
            var state = AppState.Parse(State);
            Assert.Equal(false, state.UseDirectMl);
            Assert.Equal(true, state.UseTensorRtRtx);
            Assert.Equal("9223372036854776064", state.SelectedGpuId);
            Assert.Equal(0, state.SelectedGpuPosition);
            var wf = state.SelectedWorkflow!;
            Assert.Equal("two", wf.WorkflowName);
            Assert.Equal(@"D:\anime", wf.InputFolderPath);
            // nulls are the defaults, as the GUI's setters make them
            Assert.Equal("off", wf.Deinterlace);
            Assert.Equal(0, wf.FinalResizeHeight);
            Assert.Equal(ModelSettings.DefaultTemporalSceneThreshold, wf.UpscaleSettings[0].TemporalSceneThreshold);
            Assert.Equal(540, wf.UpscaleSettings[0].ResizeHeightBeforeUpscale);
            // engine settings are normalized as the GUI does on load
            Assert.Equal("--builderOptimizationLevel=4", wf.TrtEngineSettings);
            Assert.Equal(3, wf.TemporalFixStrength);
            Assert.Equal(0.25M, wf.TemporalFixSceneThreshold);
        }

        [Fact]
        public void AnExportedWorkflow()
        {
            var path = Path.Join(Path.GetTempPath(), $"vj-{Guid.NewGuid():N}.awf");
            File.WriteAllText(path, """
                {"$type":"VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai","WorkflowName":"x","EnableRife":true,
                 "RifeModel":"RIFE 4.25 Lite","UpscaleSettings":{"$values":[{"OnnxModelPath":"m.onnx"}]}}
                """);
            try
            {
                var wf = AppState.LoadWorkflow(path);
                Assert.True(wf.EnableRife);
                Assert.Equal("RIFE 4.25 Lite", wf.RifeModel);
                Assert.Equal("m.onnx", wf.UpscaleSettings.Single().OnnxModelPath);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void NotAStateFile()
        {
            Assert.Throws<FormatException>(() => AppState.Parse("{\"a\": 1}"));
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => AppState.Parse("not json"));
        }
    }
}
