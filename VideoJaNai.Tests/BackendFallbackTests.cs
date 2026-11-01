using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    public class BackendFallbackTests
    {
        [Fact]
        public void ConfigureFallbackIsReportedWithoutThePrefix()
        {
            Assert.Equal(
                "TensorRT-RTX failed (configure: engine build failed); using TensorRT instead",
                BackendFallback.FromAjiLog("[aji:1] TensorRT-RTX failed (configure: engine build failed); using TensorRT instead"));
        }

        [Fact]
        public void StartFallbackIsReported()
        {
            Assert.Equal(
                "TensorRT-RTX failed to start; using TensorRT instead",
                BackendFallback.FromAjiLog("[aji:1] TensorRT-RTX failed to start; using TensorRT instead"));
        }

        [Theory]
        [InlineData("[aji:1] TensorRT-RTX failed (configure: x) and TensorRT is unavailable (y); continuing on TensorRT-RTX")]
        [InlineData("[aji:2] backend TensorRT-RTX (aji_trtrtx): the backend could not be loaded")]
        [InlineData("[aji:3] backend: TensorRT-RTX (aji_trtrtx)")]
        [InlineData("PROGRESS frame=10")]
        public void OtherLinesAreNotFallbacks(string line)
        {
            Assert.Null(BackendFallback.FromAjiLog(line));
        }
    }
}
