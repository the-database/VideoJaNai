using VideoJaNai.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using System.Reactive;
using Newtonsoft.Json;
using System.IO;
using System.Text.Json.Serialization.Metadata;

namespace VideoJaNai
{
    public class NewtonsoftJsonSuspensionDriver : ISuspensionDriver
    {
        private readonly string _file;
        public static readonly JsonSerializerSettings Settings = new()
        {
            TypeNameHandling = TypeNameHandling.All,
            Formatting = Formatting.Indented,
        };

        public NewtonsoftJsonSuspensionDriver(string file) => _file = file;

        public IObservable<Unit> InvalidateState()
        {
            if (File.Exists(_file))
                File.Delete(_file);
            return Observable.Return(Unit.Default);
        }

        public IObservable<object> LoadState()
        {
            var lines = File.ReadAllText(_file);
            var state = JsonConvert.DeserializeObject<object>(lines, Settings);
            return Observable.Return(state);
        }

        public IObservable<Unit> SaveState(object state)
        {
            var lines = JsonConvert.SerializeObject(state, Settings);
            File.WriteAllText(_file, lines);
            return Observable.Return(Unit.Default);
        }

        // ReactiveUI 23 added AOT/source-gen-friendly generic overloads to ISuspensionDriver.
        // We keep using Newtonsoft (with TypeNameHandling.All) for polymorphic state, so the
        // JsonTypeInfo<T> metadata argument is ignored.
        public IObservable<Unit> SaveState<T>(T state)
        {
            var lines = JsonConvert.SerializeObject(state, Settings);
            File.WriteAllText(_file, lines);
            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> SaveState<T>(T state, JsonTypeInfo<T> typeInfo) => SaveState(state);

        public IObservable<T?> LoadState<T>(JsonTypeInfo<T> typeInfo)
        {
            var lines = File.ReadAllText(_file);
            var state = JsonConvert.DeserializeObject<T>(lines, Settings);
            return Observable.Return(state);
        }
    }
}