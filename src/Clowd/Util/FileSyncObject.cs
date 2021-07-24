using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json;
using PropertyChanged;

namespace Clowd.Util
{
    [DoNotNotify]
    public class FileSyncObject : INotifyPropertyChanged, IDisposable
    {
        public DateTime LastModifiedUtc { get; private set; }

        [JsonIgnore]
        public string FilePath { get; }

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

            // need to sync this to dispatcher thread as events can be raised / handled by WPF
            var fileChanged = SynchronizationContextEventHandler.CreateDelegate<FileSystemEventArgs>((s, e) => { Read(); });

            _fsw.Changed += (s, e) =>
            {
                if (e.FullPath == FilePath)
                {
                    Thread.Sleep(10);
                    fileChanged(s, e);
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
                var json = JsonConvert.SerializeObject(this);
                File.WriteAllText(FilePath, json);
            });
        }

        private void Read()
        {
            DoRetryDiskAction(() =>
            {
                var json = File.ReadAllText(FilePath);
                JsonConvert.PopulateObject(json, this);
            });
        }

        private void DoRetryDiskAction(Action fn)
        {
            lock (_lock)
            {
                if (_busy) return;
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

                if (_store.TryGetValue(propertyName, out var stor) && Equals(stor, value))
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

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void OnPropertiesChanged(params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                OnPropertyChanged(propertyName);
            }
        }

        private void ThrowIfDisposed()
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
                _fsw.Dispose();
            }
        }
    }
}
