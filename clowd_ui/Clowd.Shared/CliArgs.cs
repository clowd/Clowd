using System;

namespace Clowd
{
    /// <summary>
    /// The app's command-line surface. Explorer integrations (the Win11 shell extension and
    /// the legacy registry verb) launch <c>Clowd.Ui.exe upload "path" ...</c>; bare paths —
    /// files dragged onto the exe, shortcuts from before the command existed — remain accepted
    /// as an implicit upload. Deliberately tiny: grow a real parser when a second command or
    /// the first <c>--option</c> shows up, not before.
    /// </summary>
    public static class CliArgs
    {
        public const string UploadCommand = "upload";

        /// <summary>The file paths an argument list asks to upload: the arguments after a
        /// leading "upload" command, or the whole list when it is legacy bare paths. Never
        /// null; an empty result means the arguments carried nothing to upload.</summary>
        public static string[] ExtractUploadPaths(string[] args)
        {
            if (args == null || args.Length == 0)
                return Array.Empty<string>();

            // a real path is always absolute here (Explorer hands verbs fully-qualified
            // paths), so a bare "upload" token can only be the command word
            if (String.Equals(args[0], UploadCommand, StringComparison.OrdinalIgnoreCase))
                return args[1..];

            return args;
        }
    }
}
