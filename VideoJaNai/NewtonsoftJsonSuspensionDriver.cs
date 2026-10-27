using VideoJaNai.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ReactiveUI;
using System;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Signals;
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

        // The default state shipped next to the exe (the template a first launch copies), or null.
        public static object? LoadDefaultState()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, Program.AppStateFilename);
                return JsonConvert.DeserializeObject<object>(File.ReadAllText(path), Settings);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Set once the main window is up: a crash after that isn't caused by the saved state.
        public static bool StartupComplete { get; set; }

        // ReactiveUI invalidates the state after any unhandled exception, which would otherwise
        // delete the user's saved workflows. A crash during startup may come from the state itself,
        // so move it aside (the next launch starts fresh, the old state stays as <file>.bak); any
        // later crash keeps the state and only backs it up.
        public IObservable<RxVoid> InvalidateState()
        {
            if (File.Exists(_file))
            {
                if (StartupComplete)
                    File.Copy(_file, _file + ".bak", overwrite: true);
                else
                    File.Move(_file, _file + ".bak", overwrite: true);
            }
            return Signal.Return(RxVoid.Default);
        }

        public IObservable<object> LoadState()
        {
            var lines = File.ReadAllText(_file);
            try
            {
                var state = JsonConvert.DeserializeObject<object>(lines, Settings);
                return Signal.Return(state);
            }
            catch (Exception)
            {
                // ReactiveUI falls back to fresh defaults, which the next save writes over this file;
                // keep the unreadable state so a format change never silently drops saved workflows.
                File.Copy(_file, _file + ".bak", overwrite: true);
                throw;
            }
        }

        public IObservable<RxVoid> SaveState(object state)
        {
            var lines = JsonConvert.SerializeObject(state, Settings);
            File.WriteAllText(_file, lines);
            return Signal.Return(RxVoid.Default);
        }

        // ReactiveUI 23 added AOT/source-gen-friendly generic overloads to ISuspensionDriver (and 26
        // moved it off System.Reactive: RxVoid/Signal instead of Unit/Observable).
        // We keep using Newtonsoft (with TypeNameHandling.All) for polymorphic state, so the
        // JsonTypeInfo<T> metadata argument is ignored.
        public IObservable<RxVoid> SaveState<T>(T state)
        {
            var lines = JsonConvert.SerializeObject(state, Settings);
            File.WriteAllText(_file, lines);
            return Signal.Return(RxVoid.Default);
        }

        public IObservable<RxVoid> SaveState<T>(T state, JsonTypeInfo<T> typeInfo) => SaveState(state);

        public IObservable<T?> LoadState<T>(JsonTypeInfo<T> typeInfo)
        {
            var lines = File.ReadAllText(_file);
            var state = JsonConvert.DeserializeObject<T>(lines, Settings);
            return Signal.Return(state);
        }
    }
}