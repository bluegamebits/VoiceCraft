using System;

namespace VoiceCraft.Web;

internal static class Log
{
    public static void Info(string message) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    public static void Error(string message) => Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {message}");
}
