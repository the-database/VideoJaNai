using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoJaNai.Cli
{
    // Plain INI, made forgiving for people editing it in Notepad (the same rules as mangajanai.ini):
    // values are taken as typed (no quotes or escapes, so Windows paths work as is; one pair of
    // quotes around a value, as Explorer's "Copy as path" adds, is dropped), switches accept
    // yes/no, true/false, on/off or 1/0, a list is the same line repeated, and an empty value means
    // "not set". Comments are whole lines starting with ; or # (both are common in file names).
    // Section and setting names ignore case. Anything unknown or malformed is an error naming the
    // line, so a typo is caught.
    public sealed class IniException(string message) : Exception(message);

    public sealed class IniSection(string name, int line)
    {
        public string Name { get; } = name;
        public int Line { get; } = line;
        public List<(int Line, string Key, string Value)> Entries { get; } = [];
    }

    public static class Ini
    {
        // The text of a file Notepad may have saved as UTF-8 (with or without a BOM) or as
        // "Unicode" (UTF-16 with a BOM).
        public static string Decode(byte[] bytes)
        {
            try
            {
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
                }
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
                }
                var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
            }
            catch (DecoderFallbackException)
            {
                throw new IniException("the file is not UTF-8 text (save it as UTF-8)");
            }
        }

        // A value as typed, minus surrounding whitespace and one pair of matching quotes.
        public static string Clean(string v)
        {
            v = v.Trim();
            foreach (var q in new[] { '"', '\'' })
            {
                if (v.Length >= 2 && v[0] == q && v[^1] == q)
                {
                    return v[1..^1].Trim();
                }
            }
            return v;
        }

        public static List<IniSection> Sections(string text)
        {
            var output = new List<IniSection>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var n = i + 1;
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                {
                    continue;
                }
                if (line[0] == '[')
                {
                    if (!line.EndsWith(']'))
                    {
                        throw new IniException($"line {n}: a section name needs a closing ]: {line}");
                    }
                    output.Add(new IniSection(line[1..^1].Trim().ToLowerInvariant(), n));
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    throw new IniException($"line {n}: expected setting = value, found: {line}");
                }
                var key = line[..eq].Trim();
                if (output.Count == 0)
                {
                    throw new IniException($"line {n}: '{key}' comes before any [section]; put it under one, like [output]");
                }
                output[^1].Entries.Add((n, key.ToLowerInvariant(), Clean(line[(eq + 1)..])));
            }
            return output;
        }

        // Levenshtein distance, for "did you mean" on a misspelled name.
        public static int Distance(string a, string b)
        {
            var prev = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 0; i < a.Length; i++)
            {
                var cur = new int[b.Length + 1];
                cur[0] = i + 1;
                for (var j = 0; j < b.Length; j++)
                {
                    cur[j + 1] = Math.Min(Math.Min(prev[j] + (a[i] != b[j] ? 1 : 0), prev[j + 1] + 1), cur[j] + 1);
                }
                prev = cur;
            }
            return prev[b.Length];
        }

        public static string Suggest(string name, IReadOnlyList<string> known)
        {
            var best = known.Select(k => (d: Distance(name, k), k)).OrderBy(x => x.d).ThenBy(x => x.k, StringComparer.Ordinal).FirstOrDefault();
            return best.k != null && best.d <= Math.Max(2, name.Length / 3)
                ? $" (did you mean {best.k}?)"
                : $" (known here: {string.Join(", ", known)})";
        }
    }

    // A section's settings, read one by one; whatever is left at the end is unknown.
    public sealed class IniReader(IniSection section)
    {
        private readonly bool[] _used = new bool[section.Entries.Count];
        public IniSection Section { get; } = section;

        // Every non-empty value of the key (a list: the line repeated).
        public List<(int Line, string Value)> All(string key)
        {
            var output = new List<(int, string)>();
            for (var i = 0; i < Section.Entries.Count; i++)
            {
                var (n, k, v) = Section.Entries[i];
                if (k == key)
                {
                    _used[i] = true;
                    if (v.Length > 0)
                    {
                        output.Add((n, v));
                    }
                }
            }
            return output;
        }

        // The value, if set (empty counts as not set). Set twice is an error.
        public (int Line, string Value)? One(string key)
        {
            var values = All(key);
            if (values.Count > 1)
            {
                throw new IniException($"line {values[1].Line}: {key} is set twice in [{Section.Name}] (also on line {values[0].Line}); keep one");
            }
            return values.Count == 0 ? null : values[0];
        }

        public string? String(string key) => One(key)?.Value;

        public bool? Switch(string key)
        {
            if (One(key) is not var (n, v))
            {
                return null;
            }
            return Switches.Parse(v) ?? throw new IniException($"line {n}: {key} should be yes or no, not '{v}'");
        }

        // A number; a trailing x, % or px (as people write "2x", "90%", "1080px") is allowed.
        public int? Int(string key, string what)
        {
            if (One(key) is not var (n, v))
            {
                return null;
            }
            return Numbers.Int(v) ?? throw new IniException($"line {n}: {key} should be {what}, not '{v}'");
        }

        public decimal? Decimal(string key, string what)
        {
            if (One(key) is not var (n, v))
            {
                return null;
            }
            return Numbers.Decimal(v) ?? throw new IniException($"line {n}: {key} should be {what}, not '{v}'");
        }

        // A value from a fixed list (any case), else an error listing them.
        public string? Choice(string key, IReadOnlyList<string> choices, Func<string, string?>? parse = null)
        {
            if (One(key) is not var (n, v))
            {
                return null;
            }
            var parsed = parse != null ? parse(v) : choices.FirstOrDefault(c => c.Equals(v, StringComparison.OrdinalIgnoreCase));
            return parsed ?? throw new IniException($"line {n}: {key} should be one of {string.Join(", ", choices)}, not '{v}'");
        }

        public void Finish(IReadOnlyList<string> known)
        {
            for (var i = 0; i < Section.Entries.Count; i++)
            {
                if (!_used[i])
                {
                    var (n, k, _) = Section.Entries[i];
                    throw new IniException($"line {n}: unknown setting '{k}' in [{Section.Name}]{Ini.Suggest(k, known)}");
                }
            }
        }
    }

    public static class Switches
    {
        public static bool? Parse(string v) => v.Trim().ToLowerInvariant() switch
        {
            "yes" or "y" or "true" or "on" or "1" => true,
            "no" or "n" or "false" or "off" or "0" => false,
            _ => null,
        };
    }

    public static class Numbers
    {
        private static string Strip(string v)
        {
            var t = v.Trim();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                t = t[..^2];
            }
            return t.TrimEnd('x', 'X', '%', 'p', 'P').Trim();
        }

        public static int? Int(string v) =>
            int.TryParse(Strip(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        public static decimal? Decimal(string v) =>
            decimal.TryParse(Strip(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static class PathText
    {
        // A path from a settings file: relative to the file's folder.
        public static string Relative(string baseDir, string p) =>
            Path.IsPathRooted(p) || baseDir.Length == 0 ? p : Path.Join(baseDir, p);
    }
}
