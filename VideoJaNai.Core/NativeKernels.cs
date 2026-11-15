using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoJaNai.Core
{
    // One model step's native-kernel outcome, as aji_encode logs it in its numbered chain steps on
    // stderr (TensorRT backends with native on, libaji's default):
    //   "3. Native kernels: <model> (<what>)"
    //   "3. Native kernels not used for <model>: <why>; using TensorRT"   (or TensorRT-RTX)
    // RIFE and TemporalFix log the same way ("Native kernels: rife_v4.14 (...)"). With native=off,
    // and on DirectML, there are no such lines.
    public sealed record NativeKernelStep(string Model, bool Used, string Detail, string Message);

    public static class NativeKernels
    {
        private const string UsedPrefix = "Native kernels: ";
        private const string NotUsedPrefix = "Native kernels not used for ";

        // The step a line reports, else null. Message is the line without aji_encode's step number;
        // Detail is <what> for a native model and <why> (without "; using ...") for one that isn't.
        public static NativeKernelStep? FromAjiLog(string line)
        {
            var text = WithoutStepNumber(line.Trim());
            if (text.StartsWith(UsedPrefix, StringComparison.Ordinal))
            {
                var rest = text[UsedPrefix.Length..];
                var open = rest.IndexOf(" (", StringComparison.Ordinal);
                if (open <= 0 || !rest.EndsWith(')'))
                {
                    return null;
                }
                return new NativeKernelStep(rest[..open], true, rest[(open + 2)..^1], text);
            }
            if (text.StartsWith(NotUsedPrefix, StringComparison.Ordinal))
            {
                var rest = text[NotUsedPrefix.Length..];
                var colon = rest.IndexOf(": ", StringComparison.Ordinal);
                if (colon <= 0)
                {
                    return null;
                }
                var why = rest[(colon + 2)..];
                var tail = why.LastIndexOf("; using ", StringComparison.Ordinal);
                if (tail >= 0)
                {
                    why = why[..tail];
                }
                return new NativeKernelStep(rest[..colon], false, why, text);
            }
            return null;
        }

        // "12. text" -> "text"; anything else as is.
        private static string WithoutStepNumber(string line)
        {
            var i = 0;
            while (i < line.Length && char.IsAsciiDigit(line[i]))
            {
                i++;
            }
            return i > 0 && i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' ' ? line[(i + 2)..] : line;
        }
    }

    // The native-kernel outcome of a run (one video, or a batch: a model counts as native only if it
    // was on every video it ran on; the first reason it wasn't is kept). Models in chain order, once each.
    public sealed class NativeKernelSummary
    {
        private readonly List<NativeKernelStep> _models = [];

        public IReadOnlyList<NativeKernelStep> Models => _models;
        public int Total => _models.Count;
        public int UsedCount => _models.Count(m => m.Used);
        public bool AllUsed => Total > 0 && UsedCount == Total;
        public NativeKernelStep? FirstNotUsed => _models.FirstOrDefault(m => !m.Used);

        public void Clear() => _models.Clear();

        // Adds the line's step, if it reports one. Returns the step when it changed the summary (a
        // model seen for the first time, or one that was native until now and isn't on this video),
        // else null.
        public NativeKernelStep? Add(string line)
        {
            if (NativeKernels.FromAjiLog(line) is not NativeKernelStep step)
            {
                return null;
            }
            var at = _models.FindIndex(m => m.Model == step.Model);
            if (at < 0)
            {
                _models.Add(step);
                return step;
            }
            if (_models[at].Used && !step.Used)
            {
                _models[at] = step;
                return step;
            }
            return null;
        }
    }
}
