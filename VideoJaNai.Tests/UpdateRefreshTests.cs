using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // The first launch after an update refreshes the component packs (updater --auto); a fresh
    // install keeps what its installer's component page picked.
    public class UpdateRefreshTests
    {
        [Theory]
        [InlineData("2.8.0", "2.9.0", true, true)]     // version changed
        [InlineData("2.9.0", "2.9.0", true, false)]    // same version: already refreshed
        [InlineData(null, "2.9.0", true, true)]        // state saved by 2.8.1 or older: no LastRunVersion
        [InlineData("", "2.9.0", true, true)]
        [InlineData(null, "2.9.0", false, false)]      // fresh install: bundled default state
        public void RefreshesOnceAfterAnUpdate(string? lastRun, string version, bool hadSavedState, bool expected) =>
            Assert.Equal(expected, MainWindowViewModel.IsFirstLaunchAfterUpdate(lastRun, version, hadSavedState));
    }
}
