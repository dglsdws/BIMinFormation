using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BIMinFormation.Revit
{
    /// <summary>
    /// Diagnostic log in <c>%AppData%\BIMinFormation\logs\yyyy-MM-dd.log</c>. Never throws: a logging failure must not
    /// bring Revit down.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMinFormation", "logs");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception? exception = null) =>
            Write("ERRO", exception == null ? message : message + Environment.NewLine + exception);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    string file = Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                    string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " " +
                                  message + Environment.NewLine;
                    File.AppendAllText(file, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // No log, but the command keeps working.
            }
        }
    }
}
