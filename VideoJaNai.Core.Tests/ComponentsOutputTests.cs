using System.Linq;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // The updater's --components --json output, with the messages an updater can print.
    public class ComponentsOutputTests
    {
        const string Object = """{"gpu":{"nvidia":true,"sm":"sm120","name":"RTX 5090"},"packs":[]}""";

        [Fact]
        public void AnOlderUpdatersMessageAheadOfTheObjectIsANote()
        {
            var output = "This release's component packs are unavailable (404); RIFE can't be installed.\r\n" + Object + "\r\n";
            var (json, messages) = ComponentsOutput.Split(output);
            Assert.Equal(Object, json);
            Assert.Equal(["This release's component packs are unavailable (404); RIFE can't be installed."],
                ComponentsOutput.Notes(null, messages));
        }

        [Fact]
        public void TheObjectsNotesWin()
        {
            var (json, messages) = ComponentsOutput.Split(Object + "\n");
            Assert.Equal(Object, json);
            Assert.Empty(messages);
            Assert.Equal(["a note"], ComponentsOutput.Notes(new[] { "a note", "", null }, messages));
            Assert.Empty(ComponentsOutput.Notes(Enumerable.Empty<string?>(), messages));
        }

        [Fact]
        public void AnUpdaterErrorHasNoObject()
        {
            var (json, messages) = ComponentsOutput.Split("Updater error: No such host is known.\n");
            Assert.Null(json);
            Assert.Equal(["Updater error: No such host is known."], messages);
        }
    }
}
