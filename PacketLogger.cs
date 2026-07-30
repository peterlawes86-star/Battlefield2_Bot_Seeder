using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BF2BotManager
{
    public class PacketLogger
    {
        private static readonly object _fileLock = new object();
        public static string LogFilePath { get; private set; }

        static PacketLogger()
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            LogFilePath = Path.Combine(logDir, $"packet_log_{timeStamp}.txt");
            
            LogSystemEvent($"--- External Packet Log Session Started: {DateTime.Now} ---");
        }

        public static void LogPacket(string botName, string direction, string protocol, string remoteEndPoint, byte[] data, int length)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{botName}] {direction} ({protocol}) Endpoint: {remoteEndPoint} Length: {length} bytes");
            
            // Generate ASCII preview
            string ascii = Encoding.ASCII.GetString(data, 0, length);
            sb.AppendLine($"  ASCII: {SanitizeAscii(ascii)}");

            // Generate Hex Dump
            sb.AppendLine("  HEX:");
            sb.Append(FormatHexDump(data, length));
            sb.AppendLine(new string('-', 80));

            WriteToFile(sb.ToString());
        }

        public static void LogSystemEvent(string message)
        {
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] {message}{Environment.NewLine}";
            WriteToFile(entry);
        }

        public static void LogBotEvent(string botName, string message)
        {
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{botName}] [EVENT] {message}{Environment.NewLine}";
            WriteToFile(entry);
        }

        public static void LogException(string botName, string context, Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{botName}] [ERROR] Context: {context}");
            sb.AppendLine($"  Exception Type: {ex.GetType().FullName}");
            sb.AppendLine($"  Message: {ex.Message}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"  Inner Exception: {ex.InnerException.Message}");
            }
            sb.AppendLine(new string('-', 80));
            WriteToFile(sb.ToString());
        }

        private static string MaskPasswords(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Mask password key-value patterns in protocol strings, e.g. \password\secret123\ or \pass\secret123\ or password="secret"
            text = Regex.Replace(text, @"(\\(?:password|pass|passwd)\\)[^\\]+", "$1****", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"(password\s*=\s*[""'])[^""']+([""'])", "$1****$2", RegexOptions.IgnoreCase);
            
            return text;
        }

        private static string FormatHexDump(byte[] data, int length)
        {
            StringBuilder hexSb = new StringBuilder();
            for (int i = 0; i < length; i += 16)
            {
                hexSb.Append($"  {i:X4}: ");
                
                // Bytes in Hex
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < length)
                        hexSb.Append($"{data[i + j]:X2} ");
                    else
                        hexSb.Append("   ");
                }

                hexSb.Append(" | ");

                // Bytes in Printable ASCII
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < length)
                    {
                        byte b = data[i + j];
                        hexSb.Append(b >= 32 && b <= 126 ? (char)b : '.');
                    }
                }

                hexSb.AppendLine();
            }
            return hexSb.ToString();
        }

        private static string SanitizeAscii(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in text)
            {
                sb.Append(c >= 32 && c <= 126 ? c : '·');
            }
            return sb.ToString();
        }

        private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB Limit

        private static void WriteToFile(string content)
        {
            lock (_fileLock)
            {
                try
                {
                    // Mask sensitive password fields before writing
                    content = MaskPasswords(content);

                    FileInfo fi = new FileInfo(LogFilePath);
                    if (fi.Exists && fi.Length >= MaxLogSizeBytes)
                    {
                        File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] --- Log file reached 5MB limit. Cleared and restarted log ---{Environment.NewLine}");
                    }

                    File.AppendAllText(LogFilePath, content);
                }
                catch
                {
                    // Ignore transient file I/O exceptions
                }
            }
        }
    }
}