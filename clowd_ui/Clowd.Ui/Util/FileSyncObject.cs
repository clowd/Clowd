using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Avalonia.Threading;

namespace Clowd.Util
{
    public abstract class FileSyncObject : INotifyPropertyChanged, IDisposable
    {
        public DateTime LastModifiedUtc { get; set; }

        [JsonIgnore] public string FilePath { get; }

        protected abstract JsonTypeInfo GetJsonTypeInfo();

        public event PropertyChangedEventHandler PropertyChanged;

        // static cache
        private readonly static Dictionary<string, FileSyncObject> _alive = new Dictionary<string, FileSyncObject>(StringComparer.OrdinalIgnoreCase);
        private readonly FileSystemWatcher _fsw;
        private readonly object _lock = new object();
        private readonly Dictionary<string, object> _store = new Dictionary<string, object>();
        private readonly List<string> _events = new List<string>();

        // state
        private bool _disposed;
        private bool _busy;
        private bool _initialized;

        public static bool CheckPathInUse(string path)
        {
            return _alive.ContainsKey(path);
        }

        protected FileSyncObject(string file)
        {
            if (String.IsNullOrWhiteSpace(file))
                throw new ArgumentNullException(nameof(file));

            if (!Directory.Exists(Path.GetDirectoryName(file)))
                throw new InvalidOperationException("Directory for containing FileSyncObject must exist");

            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File must end with '.json' as this is the only supported format");

            FilePath = Path.GetFullPath(file);

            lock (_alive)
            {
                if (_alive.ContainsKey(FilePath))
                    throw new InvalidOperationException("Only one FileSyncObject can be tracking a given file at any one time.");
                _alive[FilePath] = this;
            }

            // create a save file with default values
            if (!File.Exists(FilePath))
                Save();

            _fsw = new FileSystemWatcher(Path.GetDirectoryName(FilePath));
            _fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            _fsw.EnableRaisingEvents = true;

            // need to sync this to the UI thread as events can be raised / handled by the UI
            _fsw.Changed += (s, e) =>
            {
                if (e.FullPath == FilePath)
                {
                    Thread.Sleep(10);
                    Dispatcher.UIThread.Post(Read);
                }
            };

            _fsw.Deleted += (s, e) =>
            {
                if (e.FullPath == FilePath)
                    Save();
            };

            Read();

            _initialized = true;
        }

        ~FileSyncObject()
        {
            Dispose();
        }

        private void Save()
        {
            DoRetryDiskAction(() =>
            {
                var json = JsonSerializer.Serialize(this, GetJsonTypeInfo());
                File.WriteAllText(FilePath, json);
            });
        }

        private void Read()
        {
            DoRetryDiskAction(() =>
            {
                var json = File.ReadAllText(FilePath);

                // if the file on disk carries the same LastModifiedUtc that we have in memory, this
                // change event is just an echo of our own write — repopulating would cause an event
                // loop (especially via the less precise FSW on macOS), so skip it.
                if (_initialized)
                {
                    var node = JsonNode.Parse(json);
                    var modifiedNode = node?["LastModifiedUtc"];
                    if (modifiedNode != null)
                    {
                        var fileModified = modifiedNode.GetValue<DateTime>().ToUniversalTime();
                        if (fileModified == LastModifiedUtc)
                            return;
                    }
                }

                PopulateFromJson(json);
            });
        }

        private void PopulateFromJson(string json)
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return;

            var typeInfo = GetJsonTypeInfo();
            foreach (var prop in typeInfo.Properties)
            {
                if (prop.Set == null) continue;
                if (!obj.ContainsKey(prop.Name)) continue;

                var valueNode = obj[prop.Name];
                object value = valueNode?.Deserialize(prop.PropertyType, typeInfo.Options);
                prop.Set(this, value);
            }
        }

        private void DoRetryDiskAction(Action fn)
        {
            lock (_lock)
            {
                // a Read posted to the UI thread (or an FSW event in flight) can arrive after this
                // object was disposed and its backing file deleted — e.g. a blank editor session
                // being discarded on close. There is nothing left to sync with; don't throw.
                if (_busy || _disposed) return;
                _busy = true;

                try
                {
                    int retry = 10;
                    while (true)
                    {
                        try
                        {
                            fn();
                            break;
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            // the backing file/directory has been deleted out from under us
                            // (session discarded or removed externally) — retrying cannot help,
                            // and the deletion path is already tearing this object down.
                            break;
                        }
                        catch
                        {
                            if (--retry > 0)
                            {
                                Thread.Sleep(100);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }
                finally
                {
                    _busy = false;
                    ProcessPendingEvents();
                }
            }
        }

        private void ProcessPendingEvents()
        {
            var events = _events.ToArray();
            _events.Clear();
            OnPropertiesChanged(events);
        }

        protected bool Set<T>(T value, [CallerMemberName] string propertyName = null)
        {
            lock (_lock)
            {
                ThrowIfDisposed();

                if (_store.TryGetValue(propertyName, out var stor))
                    if (Equals(stor, value))
                        return false;

                _store[propertyName] = value;

                if (_initialized)
                    LastModifiedUtc = DateTime.UtcNow;

                Save();

                if (_busy)
                {
                    _events.Add(propertyName);
                }
                else
                {
                    OnPropertyChanged(propertyName);
                }

                return true;
            }
        }

        protected T Get<T>([CallerMemberName] string propertyName = null)
        {
            lock (_lock)
            {
                ThrowIfDisposed();

                if (_store.TryGetValue(propertyName, out var stor))
                {
                    if (stor == null)
                        return default;
                    if (stor.GetType().IsAssignableFrom(typeof(T)))
                        return (T)stor;
                }

                return default;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected void OnPropertiesChanged(params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                OnPropertyChanged(propertyName);
            }
        }

        protected void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(this.GetType().FullName);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                GC.SuppressFinalize(this);
                lock (_alive)
                    _alive.Remove(FilePath);
                _fsw?.Dispose();
            }
        }
    }
}
