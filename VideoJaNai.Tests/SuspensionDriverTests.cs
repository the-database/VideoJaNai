using System;
using System.IO;
using Xunit;

namespace VideoJaNai.Tests
{
    public class SuspensionDriverTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), $"vjn-test-{Path.GetRandomFileName()}");
        readonly string _file;

        public SuspensionDriverTests()
        {
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "appstate2.json");
            File.WriteAllText(_file, "saved");
        }

        public void Dispose()
        {
            NewtonsoftJsonSuspensionDriver.StartupComplete = false;
            Directory.Delete(_dir, recursive: true);
        }

        // Regression: any crash used to move the state aside, so every workflow reset to defaults.
        [Fact]
        public void CrashAfterStartupKeepsTheState()
        {
            NewtonsoftJsonSuspensionDriver.StartupComplete = true;

            new NewtonsoftJsonSuspensionDriver(_file).InvalidateState();

            Assert.Equal("saved", File.ReadAllText(_file));
            Assert.Equal("saved", File.ReadAllText(_file + ".bak"));
        }

        [Fact]
        public void CrashDuringStartupMovesTheStateAside()
        {
            NewtonsoftJsonSuspensionDriver.StartupComplete = false;

            new NewtonsoftJsonSuspensionDriver(_file).InvalidateState();

            Assert.False(File.Exists(_file));
            Assert.Equal("saved", File.ReadAllText(_file + ".bak"));
        }
    }
}
