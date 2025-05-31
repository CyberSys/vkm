namespace vkm
{
using System.Diagnostics.CodeAnalysis;
using CommandLine;
using static System.String;

// Класс для хранения и разбора параметров командной строки
internal class Options
{
    /// <summary>
    /// Название трека для фильтрации. Если не указано — скачиваются все треки.
    /// </summary>
    [Option('t', "title", Required = false,
        HelpText = "The name of your track. If the parameter is missing, all your music will be loaded.")]
    public string? Title { set; get; }
    
    /// <summary>
    /// Логин (email или телефон) для входа в VK. Если не указан — используется предыдущий логин из кэша.
    /// </summary>
    [Option('l', "login", Required = false,
        HelpText =
            "Your phone number or email to enter VK. If the parameter is missing, the previous login will be used.")]
    public string? Login { set; get; }
    
    /// <summary>
    /// Пароль от VK. Не передаётся никуда, кроме локального компьютера. Если не указан — используется из кэша.
    /// </summary>
    [Option('p', "password", Required = false,
        HelpText =
            "The VK password is not transferred anywhere from your computer. If the parameter is missing, the previous password will be used.")]
    public string? Password { set; get; }
    
    /// <summary>
    /// Директория для скачивания музыки. Если не существует — будет создана. По умолчанию ./vk_music
    /// </summary>
    [Option('d', "directory", Required = false,
        HelpText =
            "Music download directory. If the directory does not exist, it will be created. The default is ./vk_music")]
    public string? Directory { set; get; }
}
}