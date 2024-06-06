using VideoJaNai.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using System.Reactive;
using Newtonsoft.Json;
using System.IO;

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
    }
}