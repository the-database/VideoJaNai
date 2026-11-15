using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoJaNai.Core
{
    // The updater's `--components --json` output: the JSON object is its last line that starts with
    // '{'. An updater from before its "notes" printed messages ("This release's component packs are
    // unavailable (...)") on stdout ahead of the object; Messages are the other lines, which are all of
    // the output when there is no object ("Updater error: ...").
    public static class ComponentsOutput
    {
        public static (string? Json, IReadOnlyList<string> Messages) Split(string output)
        {
            var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return (lines.LastOrDefault(l => l.StartsWith('{')), [.. lines.Where(l => !l.StartsWith('{'))]);
        }

        // What the updater said: the object's "notes" when it has them (null when it doesn't), else
        // an older updater's lines ahead of it.
        public static IReadOnlyList<string> Notes(IEnumerable<string?>? notes, IReadOnlyList<string> messages) =>
            notes is null ? messages : [.. notes.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)];
    }
}
