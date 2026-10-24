using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeJaNaiConverterGui.Services
{
    // Reads an ONNX model's graph input shape without an onnx dependency: a
    // minimal protobuf walk (ModelProto.graph -> GraphProto.input /
    // initializer -> ValueInfoProto.type -> tensor_type.shape.dim), mirroring
    // the engine's onnx_shape.cpp. Used to tell temporal (multi-frame) models
    // apart, which take T frames as [N, T*3, H, W] or [N, T, 3, H, W].
    public static class OnnxInputShape
    {
        private static readonly Dictionary<string, (DateTime, long, int)> _cache = [];

        // Frames per inference of the model (1 for single-frame models or an
        // unreadable file), cached while the file is unchanged.
        public static int TemporalFrames(string onnxPath)
        {
            try
            {
                var info = new FileInfo(onnxPath);
                if (!info.Exists)
                    return 1;
                if (_cache.TryGetValue(onnxPath, out var c) && c.Item1 == info.LastWriteTimeUtc && c.Item2 == info.Length)
                    return c.Item3;
                var dims = InputDims(File.ReadAllBytes(onnxPath));
                var frames = 1;
                if (dims is { Count: 5 } && dims[1] > 1 && dims[2] == 3)
                    frames = (int)dims[1];
                else if (dims is { Count: 4 } && dims[1] > 3 && dims[1] % 3 == 0)
                    frames = (int)(dims[1] / 3);
                _cache[onnxPath] = (info.LastWriteTimeUtc, info.Length, frames);
                return frames;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        // Dims of the first graph input that is not an initializer (-1 for
        // symbolic dims), or null.
        public static List<long>? InputDims(byte[] data)
        {
            var m = new Reader(data, 0, data.Length);
            while (m.More)
            {
                var f = m.Tag(out var wire);
                if (f != 7 || wire != 2)
                {
                    m.Skip(wire);
                    continue;
                }
                var g = m.Sub();
                var inits = new HashSet<string>();
                var inputs = new List<(string, List<long>)>();
                while (g.More)
                {
                    var gf = g.Tag(out var gw);
                    if (gf == 5 && gw == 2)
                        inits.Add(TensorName(g.Sub()));
                    else if (gf == 11 && gw == 2)
                    {
                        var vi = ValueInfo(g.Sub());
                        if (vi is { } v)
                            inputs.Add(v);
                    }
                    else
                        g.Skip(gw);
                }
                foreach (var (name, dims) in inputs)
                {
                    if (!inits.Contains(name))
                        return dims;
                }
                return null;
            }
            return null;
        }

        private static string TensorName(Reader r)
        {
            while (r.More)
            {
                var f = r.Tag(out var wire);
                if (f == 8 && wire == 2)
                    return r.String();
                r.Skip(wire);
            }
            return string.Empty;
        }

        private static (string, List<long>)? ValueInfo(Reader r)
        {
            var name = string.Empty;
            List<long>? dims = null;
            while (r.More)
            {
                var f = r.Tag(out var wire);
                if (f == 1 && wire == 2)
                    name = r.String();
                else if (f == 2 && wire == 2)
                {
                    var tp = r.Sub();
                    while (tp.More)
                    {
                        var tf = tp.Tag(out var tw);
                        if (tf != 1 || tw != 2)
                        {
                            tp.Skip(tw);
                            continue;
                        }
                        var tt = tp.Sub();
                        while (tt.More)
                        {
                            var ff = tt.Tag(out var w);
                            if (ff == 2 && w == 2)
                                dims = Shape(tt.Sub());
                            else
                                tt.Skip(w);
                        }
                    }
                }
                else
                    r.Skip(wire);
            }
            return dims == null ? null : (name, dims);
        }

        private static List<long> Shape(Reader r)
        {
            var dims = new List<long>();
            while (r.More)
            {
                var f = r.Tag(out var wire);
                if (f == 1 && wire == 2)
                {
                    var d = r.Sub();
                    long v = -1;
                    while (d.More)
                    {
                        var df = d.Tag(out var dw);
                        if (df == 1 && dw == 0)
                            v = (long)d.Varint();
                        else
                            d.Skip(dw);
                    }
                    dims.Add(v);
                }
                else
                    r.Skip(wire);
            }
            return dims;
        }

        private sealed class Reader(byte[] data, int pos, int end)
        {
            private int _pos = pos;

            public bool More => _pos < end;

            public ulong Varint()
            {
                ulong v = 0;
                for (var shift = 0; shift < 64; shift += 7)
                {
                    if (_pos >= end)
                        throw new InvalidDataException("truncated varint");
                    var b = data[_pos++];
                    v |= (ulong)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0)
                        return v;
                }
                throw new InvalidDataException("varint too long");
            }

            public uint Tag(out int wire)
            {
                var t = Varint();
                wire = (int)(t & 7);
                return (uint)(t >> 3);
            }

            public Reader Sub()
            {
                var n = Varint();
                if (n > (ulong)(end - _pos))
                    throw new InvalidDataException("truncated field");
                var r = new Reader(data, _pos, _pos + (int)n);
                _pos += (int)n;
                return r;
            }

            public string String()
            {
                var s = Sub();
                return System.Text.Encoding.UTF8.GetString(data, s._pos, s.Length);
            }

            private int Length => end - _pos;

            public void Skip(int wire)
            {
                switch (wire)
                {
                    case 0: Varint(); break;
                    case 1: _pos += 8; break;
                    case 2: Sub(); break;
                    case 5: _pos += 4; break;
                    default: throw new InvalidDataException($"wire type {wire}");
                }
                if (_pos > end)
                    throw new InvalidDataException("truncated field");
            }
        }
    }
}
