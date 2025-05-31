namespace vkm
{
using System;
using static System.Console;
using static System.ConsoleColor;
using static System.String;

// Класс для логирования сообщений в консоль с цветовой индикацией
internal static class Logger
{
    /// <summary>
    /// Логирование сообщения с цветом и тегом
    /// </summary>
    internal static void Log<T>(this T obj, string withTag = "", ConsoleColor withColor = DarkBlue)
    {
        var previousColor = ForegroundColor;
        ForegroundColor = withColor;
        if (withTag is not "") Write($"{withTag} -- ");
        WriteLine(obj);
        ForegroundColor = previousColor;
    }

    /// <summary>
    /// Логирование ошибки (красный цвет)
    /// </summary>
    internal static void LogError<T>(this T obj) => obj.Log(withColor: DarkRed);
    /// <summary>
    /// Логирование предупреждения (жёлтый цвет)
    /// </summary>
    internal static void LogWarn<T>(this T obj) => obj.Log(withColor: DarkYellow);
}
}