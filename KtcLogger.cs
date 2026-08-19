using System;
using System.IO;
using TaleWorlds.Library;

namespace LT_KickTheChicken
{
    internal static class KtcLogger
    {
        public const string ModuleId = "LT_KickTheChicken";
        private static readonly string LogPath = @"..\..\Modules\" + ModuleId + @"\logs\";
        private static readonly string ErrorFile = LogPath + "error.log";
        private static readonly string DebugFile = LogPath + "debug.log";

        static KtcLogger()
        {
            try
            {
                if (!Directory.Exists(LogPath))
                {
                    Directory.CreateDirectory(LogPath);
                }

                if (!File.Exists(ErrorFile))
                {
                    File.Create(ErrorFile).Dispose();
                }

                if (!File.Exists(DebugFile))
                {
                    File.Create(DebugFile).Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Debug(string log)
        {
            try
            {
                using StreamWriter streamWriter = new StreamWriter(DebugFile, true);
                streamWriter.WriteLine(log);
            }
            catch (Exception)
            {
            }
        }

        public static void LogError(string log)
        {
            try
            {
                using StreamWriter streamWriter = new StreamWriter(ErrorFile, true);
                streamWriter.WriteLine(log);
            }
            catch (Exception)
            {
            }
        }

        public static void LogError(Exception exception)
        {
            LogError("Message " + exception.Message);
            LogError("Error at " + exception.Source + " in function " + exception.Message);
            LogError("With stacktrace :\n" + exception.StackTrace);
            LogError("----------------------------------------------------");
            InformationManager.DisplayMessage(new InformationMessage(exception.Message));
        }
    }
}
