using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Avalonia.Media;
using Microsoft.Extensions.Configuration;

namespace Clowd.Config
{
    /// <summary>
    /// Loads and saves <see cref="SettingsRoot"/> as JSON using the standard .NET configuration
    /// stack. <see cref="Load()"/> is a pure parse with no side effects: it returns a fully
    /// populated defaults instance when the file (or any section/value) is missing. Persistence
    /// is explicit — nothing in the settings object graph saves itself; UI code calls
    /// <see cref="Save(SettingsRoot)"/> after a user-visible mutation.
    /// </summary>
    public static class SettingsService
    {
        /// <summary>Both paths (Microsoft.Extensions.Configuration in, System.Text.Json out) share
        /// one representation: enums by name, Color as "#AARRGGBB", SimpleKeyGesture as
        /// "Modifier+Modifier+Key" (see <see cref="SimpleKeyGesture.ToSerializedString"/>).</summary>
        static SettingsService()
        {
            // the M.E.C binder converts string values via TypeDescriptor — register a converter
            // for the external Avalonia Color struct (SimpleKeyGesture carries its own
            // [TypeConverter] attribute).
            TypeDescriptor.AddAttributes(typeof(Color), new TypeConverterAttribute(typeof(ColorTypeConverter)));
        }

        public static string FilePath =>
#if DEBUG
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd", "Clowd.DEBUG.Settings.json");
#else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clowd", "Clowd.Settings.json");
#endif

        /// <summary>
        /// Parses the settings file into a new <see cref="SettingsRoot"/>. Missing file, sections
        /// or values fall back to the compiled-in defaults. Throws only when the file exists but
        /// cannot be parsed (malformed JSON / unconvertible value) — callers may then offer a reset.
        /// </summary>
        public static SettingsRoot Load() => Load(FilePath);

        public static SettingsRoot Load(string path)
        {
            return WithSharingVioRetry(() =>
            {
                var config = new ConfigurationBuilder()
                             .AddJsonFile(path, optional: true)
                             .Build();

                var settings = config.Get<SettingsRoot>() ?? new SettingsRoot();
                Debug.WriteLine("Settings were loaded from " + path);
                return settings;
            });
        }

        /// <summary>Writes the settings as indented JSON, atomically (temp file + move-overwrite).</summary>
        public static void Save(SettingsRoot settings) => Save(settings, FilePath);

        public static void Save(SettingsRoot settings, string path)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var json = JsonSerializer.Serialize(settings, CreateJsonOptions());

            // unique temp name: a fixed name races when two instances save concurrently
            var tempPath = path + "." + Path.GetRandomFileName() + ".~tmp";

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            WithSharingVioRetry<object>(() =>
            {
                File.WriteAllText(tempPath, json);
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // the atomic replace is denied while another process (editor tab, file
                    // watcher, AV scan) holds the settings file open without delete sharing.
                    // Fall back to writing in place — open handles normally share read/write.
                    File.WriteAllText(path, json);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch {; }
                }

                return null;
            });
        }

        public static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // local settings file read back by this app — keep '+' etc. human-readable
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new ColorJsonConverter());
            options.Converters.Add(new SimpleKeyGestureJsonConverter());
            return options;
        }

        /// <summary>Simple retry loop for sharing violations (another process holding the settings
        /// file).</summary>
        private static T WithSharingVioRetry<T>(Func<T> func)
        {
            const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
            const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);

            var sw = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return func();
                }
                catch (IOException ex) when ((ex.HResult == ERROR_SHARING_VIOLATION || ex.HResult == ERROR_LOCK_VIOLATION)
                                             && sw.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>Color ↔ "#AARRGGBB" for the M.E.C binder (load path).</summary>
        private sealed class ColorTypeConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) =>
                sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) =>
                value is string s ? Color.Parse(s) : base.ConvertFrom(context, culture, value);
        }

        /// <summary>SimpleKeyGesture ↔ "Control+Shift+S" for System.Text.Json (save path; the load
        /// path uses the [TypeConverter] on SimpleKeyGesture). A cleared gesture is written as
        /// "None" rather than JSON null: the configuration binder never assigns null converted
        /// values, so a null here would silently resurrect the compiled-in default gesture.
        /// SettingsHotkey normalizes the resulting Key.None gesture back to null.</summary>
        private sealed class SimpleKeyGestureJsonConverter : JsonConverter<SimpleKeyGesture>
        {
            public override bool HandleNull => true;

            public override SimpleKeyGesture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                SimpleKeyGesture.Parse(reader.GetString());

            public override void Write(Utf8JsonWriter writer, SimpleKeyGesture value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value?.ToSerializedString() ?? "None");
        }
    }
}
