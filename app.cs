namespace vkm
{
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using VkNet.Model.Attachments;
using VkNet.Model.RequestParams;
using static System.IO.Directory;
using static System.IO.File;
using static Vk;
using CommandLine;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using System.Net;

// Главный класс программы: точка входа, обработка аргументов, авторизация, скачивание аудио
internal class Program
{
    /// <summary>
    /// Точка входа в программу. Обрабатывает аргументы командной строки, авторизует пользователя, скачивает аудио.
    /// Весь цикл защищён от ошибок VK API и сети, чтобы программа не падала.
    /// </summary>
    public static async Task Main(string[] args)
    {
        // Основной цикл для повторной авторизации при ошибке scopes
        while (true)
        {
            try
            {
                // Парсинг аргументов командной строки и запуск основной логики
                await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(async options =>
                {
                    // Авторизация в VK (через кэш или логин/пароль)
                    using var api = options.Login switch
                    {
                        null => LoginToVkApiWithCache(),
                        _ => LoginToVkApi(
                            options.Login,
                            options.Password ?? throw new ArgumentException("Password wasn't set. Use --p to set password")
                        )
                    };

                    // Определение директории для скачивания
                    var directory = options.Directory switch
                    {
                        null => api.GetUserLink(),
                        _ => options.Directory
                    };

                    if (!Directory.Exists(directory)) CreateDirectory(directory);

                    // Получение списка аудио из VK
                    var ownerId = api.UserId ?? throw new Exception("UserId is null");
                    var audioList = AudioUtils.GetAudioListFromVk(api, ownerId);

                    // Фильтрация по названию трека, если указано
                    Func<AudioInfo, bool> filter = options.Title switch
                    {
                        null => _ => true,
                        _ => x => (x.Title ?? string.Empty).ToUpper().Contains(options.Title.ToUpper())
                    };

                    // Формирование списка для скачивания (только новые треки)
                    var audios = audioList
                        .Where(filter)
                        .Select(x => (
                            Filename: $"{directory}/{x.Artist} - {x.Title}.mp3",
                            Url: x.Url
                        ))
                        .Where(x => !File.Exists(x.Filename) && !string.IsNullOrEmpty(x.Url))
                        .ToList();

                    // HttpClient с нужным User-Agent для обхода антибота VK
                    var handler = new HttpClientHandler();
                    var http = new HttpClient(handler);
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("com.vk.windows_app/20302");

                    int total = audios.Count;
                    int current = 0;

                    // Отрисовка прогресс-бара в консоли
                    void DrawProgressBar(int progress, int total)
                    {
                        const int barWidth = 40;
                        double percent = total == 0 ? 1 : (double)progress / total;
                        int filled = (int)(barWidth * percent);
                        string bar = new string('#', filled) + new string('-', barWidth - filled);
                        Console.Write($"\r[{bar}] {progress}/{total} ({percent:P0})");
                        if (progress == total) Console.WriteLine();
                    }

                    DrawProgressBar(0, total);
                    // Основной цикл скачивания аудио
                    foreach (var (filename, url) in audios)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(url) && url.Contains(".mp3"))
                            {
                                $"Downloading {filename}...".Log();
                                int maxAttempts = 3;
                                int attempt = 0;
                                bool success = false;
                                while (attempt < maxAttempts && !success)
                                {
                                    try
                                    {
                                        WriteAllBytes(filename, await http.GetByteArrayAsync(url));
                                        success = true;
                                    }
                                    catch (HttpRequestException hre)
                                    {
                                        $"HTTP error while downloading {filename} (attempt {attempt + 1}/{maxAttempts}): {hre.Message}".LogError();
                                        if (hre.StatusCode != null)
                                            $"HTTP status: {hre.StatusCode}".LogError();
                                        if (++attempt < maxAttempts) await Task.Delay(2000);
                                    }
                                    catch (IOException ioe)
                                    {
                                        $"IO error while saving {filename} (attempt {attempt + 1}/{maxAttempts}): {ioe.Message}".LogError();
                                        try
                                        {
                                            var drive = new DriveInfo(Path.GetPathRoot(filename)!);
                                            $"Free space: {drive.AvailableFreeSpace / (1024 * 1024)} MB".LogWarn();
                                        }
                                        catch { /* ignore */ }
                                        if (++attempt < maxAttempts) await Task.Delay(2000);
                                    }
                                }
                                if (!success)
                                {
                                    $"Failed to download {filename} after {maxAttempts} attempts.".LogError();
                                }
                            }
                            else if (!string.IsNullOrEmpty(url) && url.Contains("m3u8"))
                            {
                                $"HLS/m3u8 detected for {filename}. Will use ffmpeg to download and convert to mp3...".LogWarn();
                                string? tempM3u8 = null, tempMp3 = null;
                                try
                                {
                                    tempM3u8 = Path.GetTempFileName() + ".m3u8";
                                    tempMp3 = Path.GetTempFileName() + ".mp3";
                                    using (var web = new WebClient())
                                    {
                                        web.Headers.Add("User-Agent", "com.vk.windows_app/20302");
                                        await web.DownloadFileTaskAsync(url, tempM3u8);
                                    }

                                    // Устанавливаем путь к ffmpeg, если он есть рядом с exe
                                    var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                                    var ffmpegPath = Path.Combine(exeDir, "ffmpeg.exe");
                                    if (File.Exists(ffmpegPath))
                                    {
                                        FFmpeg.SetExecutablesPath(exeDir);
                                    }
                                    else
                                    {
                                        FFmpeg.SetExecutablesPath(AppDomain.CurrentDomain.BaseDirectory);
                                    }
                                    var conversion = await FFmpeg.Conversions.FromSnippet.Convert(tempM3u8, tempMp3);
                                    conversion.OnProgress += (s, e) =>
                                    {
                                        $"ffmpeg progress: {e.Percent:F1}% ({e.Duration})".Log();
                                    };
                                    conversion.OnDataReceived += (s, e) =>
                                    {
                                        if (!string.IsNullOrWhiteSpace(e.Data))
                                            $"ffmpeg: {e.Data}".Log();
                                    };
                                    await conversion.Start();

                                    File.Move(tempMp3, filename, true);
                                    File.Delete(tempM3u8);
                                    $"HLS/m3u8 for {filename} successfully converted to mp3 via ffmpeg.".Log();
                                }
                                catch (Xabe.FFmpeg.Exceptions.FFmpegNotFoundException ffnotfound)
                                {
                                    $"FFmpeg not found: {ffnotfound.Message}".LogError();
                                }
                                catch (Exception ffex)
                                {
                                    $"FFmpeg error for {filename}: {ffex.Message}".LogError();
                                    if (tempMp3 != null && File.Exists(tempMp3)) File.Delete(tempMp3);
                                    if (tempM3u8 != null && File.Exists(tempM3u8)) File.Delete(tempM3u8);
                                }
                            }
                            else
                            {
                                $"Unsupported audio URL for {filename}: {url}".LogWarn();
                            }
                        }
                        catch (Exception e)
                        {
                            $"Unexpected error for {filename}: {e.Message}\n{e.StackTrace}".LogError();
                        }
                        finally
                        {
                            current++;
                            DrawProgressBar(current, total);
                        }
                    }
                });
                // Если всё прошло успешно — выходим из цикла
                break;
            }
            catch (VkNet.Exception.VkApiException ex) when (ex.Message.Contains("Access denied: no access to call this method. It cannot be called with current scopes."))
            {
                // Обработка ошибки scopes на любом этапе
                Console.WriteLine("VK API error: Access denied. Ваш токен не даёт доступ к нужным методам (scopes).");
                Console.WriteLine("Возможно, вы не предоставили все разрешения при авторизации. Попробовать авторизоваться заново? (Y/N)");
                var answer = Console.ReadLine();
                if (!string.IsNullOrEmpty(answer) && answer.Trim().ToUpper() == "Y")
                {
                    // Удаляем кэш авторизации, чтобы пользователь мог ввести логин/пароль заново
                    if (File.Exists(".authorization"))
                    {
                        try { File.Delete(".authorization"); } catch { }
                    }
                    continue; // Повторяем цикл
                }
                else
                {
                    Console.WriteLine("Спасибо за использование программы! Всего хорошего.");
                    Environment.Exit(0);
                }
            }
            // Обработка других частых ошибок VK API и сети
            catch (VkNet.Exception.CaptchaNeededException captchaEx)
            {
                // Капча: выводим ссылку и просим пользователя ввести ответ
                Console.WriteLine($"VK API error: Требуется капча. Перейдите по ссылке: {captchaEx.Img}");
                Console.Write("Введите капчу: ");
                var captcha = Console.ReadLine();
                // ToDo: можно реализовать повтор запроса с капчей, если потребуется
                Console.WriteLine("Попробуйте перезапустить программу и ввести капчу при авторизации.");
                Environment.Exit(1);
            }
            catch (VkNet.Exception.RateLimitReachedException)
            {
                // Превышен лимит запросов
                Console.WriteLine("VK API error: Превышен лимит запросов к API. Подождите и попробуйте снова.");
                Environment.Exit(1);
            }
            catch (System.Net.Http.HttpRequestException netEx)
            {
                // Ошибки сети
                Console.WriteLine($"Ошибка сети: {netEx.Message}");
                Console.WriteLine("Проверьте подключение к интернету и попробуйте снова.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                // Финальный catch: любые другие ошибки
                Console.WriteLine($"Произошла непредвиденная ошибка: {ex.Message}\n{ex.StackTrace}");
                Console.WriteLine("Программа завершена. Спасибо за использование!");
                Environment.Exit(1);
            }
        }
    }
}

// Класс для хранения информации об аудиозаписи VK (аналог TVKAudio из Delphi)
internal class AudioInfo
{
    /// <summary>
    /// Идентификатор аудиозаписи
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// Идентификатор владельца (пользователь или группа)
    /// </summary>
    public int OwnerId { get; set; }
    /// <summary>
    /// Исполнитель
    /// </summary>
    public string? Artist { get; set; }
    /// <summary>
    /// Название трека
    /// </summary>
    public string? Title { get; set; }
    /// <summary>
    /// Прямая ссылка на аудиофайл
    /// </summary>
    public string? Url { get; set; }
    /// <summary>
    /// Длительность в секундах
    /// </summary>
    public int Duration { get; set; }
    /// <summary>
    /// AccessKey для приватных треков
    /// </summary>
    public string? AccessKey { get; set; }
    /// <summary>
    /// Название альбома
    /// </summary>
    public string? Album { get; set; }
    /// <summary>
    /// Ссылка на обложку альбома
    /// </summary>
    public string? AlbumArtUrl { get; set; }
    /// <summary>
    /// Жанр (id)
    /// </summary>
    public int GenreId { get; set; }
    /// <summary>
    /// Id текста песни (если есть)
    /// </summary>
    public int LyricsId { get; set; }
    /// <summary>
    /// Дата добавления (Unix time)
    /// </summary>
    public long Date { get; set; }
}

// Класс-утилита для получения списка аудио из VK
internal static class AudioUtils
{
    /// <summary>
    /// Получить список аудиозаписей пользователя или группы из VK
    /// </summary>
    /// <param name="api">Авторизованный VkApi</param>
    /// <param name="ownerId">Id владельца (пользователь или группа)</param>
    /// <param name="offset">Смещение для постраничной загрузки</param>
    /// <param name="count">Максимальное количество треков</param>
    /// <returns>Список AudioInfo</returns>
    public static List<AudioInfo> GetAudioListFromVk(VkNet.VkApi api, long ownerId, int offset = 0, int count = 6000)
    {
        var result = new List<AudioInfo>();
        // Получаем список аудио через VK API
        var audioList = api.Audio.Get(new VkNet.Model.RequestParams.AudioGetParams
        {
            OwnerId = ownerId,
            Offset = offset,
            Count = count
        });
        // Преобразуем результат VK API в список AudioInfo
        foreach (var audio in audioList)
        {
            result.Add(new AudioInfo
            {
                Id = audio.Id != null ? (int)audio.Id : 0,
                OwnerId = audio.OwnerId != null ? (int)audio.OwnerId : 0,
                Artist = audio.Artist,
                Title = audio.Title,
                Url = audio.Url?.ToString(),
                Duration = audio.Duration,
                AccessKey = audio.AccessKey,
                Album = audio.Album?.Title,
                AlbumArtUrl = audio.Album?.Thumb?.Photo600 ?? audio.Album?.Thumb?.Photo300,
                GenreId = audio.Genre != null ? (int)audio.Genre : 0,
                LyricsId = audio.LyricsId != null ? (int)audio.LyricsId : 0,
                Date = audio.Date == default(DateTime) ? 0 : (long)((DateTimeOffset)audio.Date).ToUnixTimeSeconds()
            });
        }
        return result;
    }
}
}