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

            try
            {
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

                // our own atomic saves land as a rename (.tmp → file); another process using this
                // class produces Renamed rather than Changed, so both must trigger a re-read.
                _fsw.Renamed += (s, e) =>
                {
                    if (e.FullPath == FilePath)
                        Dispatcher.UIThread.Post(Read);
                };

                // an external atomic replacement can also surface as Created — the macOS watcher
                // maps a rename-into-place onto Created when the destination already existed.
                _fsw.Created += (s, e) =>
                {
                    if (e.FullPath == FilePath)
                        Dispatcher.UIThread.Post(Read);
                };

                _fsw.Deleted += (s, e) =>
                {
                    // some platforms report a rename-over-existing as a Deleted for the target;
                    // only recreate the file when it is actually gone, or this would loop forever.
                    if (e.FullPath == FilePath && !File.Exists(FilePath))
                        Save();
                };

                Read();

                _initialized = true;
            }
            catch
            {
                // a failed construction must be fully torn down: without unregistration the path
                // is poisoned for the rest of the process (CheckPathInUse stays true and every
                // later attempt to load this file throws "Only one FileSyncObject..."), and a
                // still-pending finalizer would later unregister whatever instance owns the path
                // by then. Dispose covers both — identity-checked removal and SuppressFinalize.
                Dispose();
                throw;
            }
        }

        ~FileSyncObject()
        {
            Dispose();
        }

        private void Save()
        {
            DoRetryDiskAction(WriteToDisk);
        }

        // write-to-temp then rename, so the file on disk is never observable half-written. A crash
        // (or power loss) mid-WriteAllText used to leave a truncated session.json that then failed
        // to parse on every subsequent launch (CLOWD-9).
        private void WriteToDisk()
        {
            var tmp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(this, GetJsonTypeInfo());

            // flush through the OS buffers before the rename: on power loss the rename must never
            // become durable ahead of the data it points at, or the torn file returns.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // the atomic replace is denied while another process (AV scan, indexer) holds the
                // file open without delete sharing — fall back to writing in place, which shares
                // fine with plain readers (the same trade SettingsService.Save makes).
                File.WriteAllText(FilePath, json);
                try { File.Delete(tmp); } catch {; }
            }
        }

        private void Read()
        {
            DoRetryDiskAction(() =>
            {
                var json = File.ReadAllText(FilePath);

                try
                {
                    // a valid-JSON file of the wrong shape ("[]", "null", a bare string) is
                    // corruption too — without this check it would load silently as an
                    // all-defaults ghost.
                    if (JsonNode.Parse(json) is not JsonObject obj)
                        throw new JsonException("Root of the document is not a JSON object.");

                    // if the file on disk carries the same LastModifiedUtc that we have in memory, this
                    // change event is just an echo of our own write — repopulating would cause an event
                    // loop (especially via the less precise FSW on macOS), so skip it.
                    if (_initialized)
                    {
                        var modifiedNode = obj["LastModifiedUtc"];
                        if (modifiedNode != null)
                        {
                            var fileModified = modifiedNode.GetValue<DateTime>().ToUniversalTime();
                            if (fileModified == LastModifiedUtc)
                                return;
                        }
                    }

                    PopulateFromJson(obj);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                {
                    // corruption is deterministic — the retry loop can't help. (FormatException /
                    // InvalidOperationException cover valid JSON carrying garbage values, e.g. a
                    // non-date LastModifiedUtc; setters can't raise them here because _lock is
                    // held for the whole action.) The file was torn by a crash mid-write
                    // (pre-atomic saves) or corrupted externally.
                    if (_initialized)
                    {
                        // mid-run the in-memory state is the authority — quarantine a COPY of the
                        // evidence, then atomically replace the file from memory. Copy, not move:
                        // if WriteToDisk fails transiently the file still exists, so the outer
                        // retry loop re-parses, lands back here and re-drives the recovery
                        // instead of silently abandoning it with no file at all.
                        // NB: not Save(), which would no-op behind the _busy guard.
                        File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
                        WriteToDisk();
                        SentryConfig.CaptureHandled(ex, "filesync.recover");
                    }
                    else
                    {
                        // during construction there is no state worth keeping — move the file
                        // aside (so the next launch skips it rather than re-reporting forever)
                        // and fail the load; the caller skips this object (a corrupt session is
                        // simply not shown in recents). If the move itself fails transiently the
                        // outer retry re-parses and lands back here to try again.
                        _fsw.EnableRaisingEvents = false; // a FSW Deleted echo of the rename must not resurrect the file
                        File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
                        throw new InvalidDataException($"'{Path.GetFileName(FilePath)}' is corrupt and has been renamed to .corrupt", ex);
                    }
                }
            });
        }

        private void PopulateFromJson(JsonObject obj)
        {
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
                        catch (InvalidDataException)
                        {
                            // corrupt file (see Read) — deterministic, retrying cannot help.
                            throw;
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
                {
                    // remove by identity, not key: the finalizer of a failed/stale instance must
                    // not unregister a newer live tracker for the same path. (FilePath is null
                    // when the constructor threw before assigning it — finalizers still run.)
                    if (FilePath != null && _alive.TryGetValue(FilePath, out var cur) && ReferenceEquals(cur, this))
                        _alive.Remove(FilePath);
                }
                _fsw?.Dispose();
            }
        }
    }
}
