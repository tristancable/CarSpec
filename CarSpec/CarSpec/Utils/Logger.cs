using System.Diagnostics;

namespace CarSpec.Utils
{
    /// <summary>
    /// Simple console logger utility.
    /// </summary>
    public class Logger
    {
        private readonly string _tag;

        public Logger(string tag) => _tag = tag;

        public void Info(string msg) => Write("INFO", msg);
        public void LogDebug(string msg) => Write("DEBUG", msg);
        public void Warn(string msg) => Write("WARN", msg);
        public void Error(string msg) => Write("ERROR", msg);

        private void Write(string level, string msg)
        {
            var formatted = $"[{DateTime.Now:HH:mm:ss}] [{_tag}] {level}: {msg}";
            Debug.WriteLine(formatted);

            // Add color formatting for console (optional)
            if (level == "ERROR")
                Console.ForegroundColor = ConsoleColor.Red;
            else if (level == "WARN")
                Console.ForegroundColor = ConsoleColor.Yellow;
            else if (level == "INFO")
                Console.ForegroundColor = ConsoleColor.Cyan;
            else
                Console.ResetColor();

            Console.WriteLine(formatted);
            Console.ResetColor();
        }
    }
}