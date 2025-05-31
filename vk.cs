namespace vkm
{
using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VkNet;
using VkNet.Abstractions.Core;
using VkNet.AudioBypassService.Extensions;
using VkNet.Model;
using VkNet.Utils.AntiCaptcha;
using static System.Console;
using static System.IO.File;
using static VkNet.Enums.Filters.Settings;

// Класс для работы с VK API: авторизация, обработка капчи, ручная и автоматическая валидация, кэширование токена
internal static class Vk
{
    private const string Cache = ".authorization";

    /// <summary>
    /// Получить ссылку на профиль пользователя VK (используется для имени папки по умолчанию)
    /// </summary>
    internal static string GetUserLink(this VkApi api) =>
        $"vk.com/{api.Account.GetProfileInfo().ScreenName ?? $"id{api.UserId}"}";

    /// <summary>
    /// Авторизация в VK по логину и паролю, с поддержкой 2FA, автоматической и ручной валидации, кэшированием токена
    /// </summary>
    internal static VkApi LoginToVkApi(string withLogin, string andPassword)
    {
        // Включаем доступ к своим сообщениям, комментариям и музыке
        var hooks = new ServiceCollection()
            .AddAudioBypass()
            .AddSingleton<PrimitiveCaptchaSolver>();
        var api = new VkApi(hooks);
        try
        {
            api.Authorize(new ApiAuthParams
            {
                // Используем идентификатор официального VK iOS-приложения (как в aimp_VK)
                ApplicationId = 5776857, // VK iOS AppID, позволяет обойти лишнюю валидацию
                Login = withLogin,
                Password = andPassword,
                Settings = All,
                TwoFactorAuthorization = () =>
                {
                    "Enter 2FA code:".Log();
                    return Console.ReadLine()!;
                }
            });
        }
        catch (VkNet.Exception.NeedValidationException ex)
        {
            int maxValidationAttempts = 3;
            int validationAttempt = 0;
            Exception? lastEx = ex;
            while (validationAttempt < maxValidationAttempts)
            {
                $"VK требует валидацию: {lastEx.Message}".LogWarn();
                var redirectUri = (lastEx as VkNet.Exception.NeedValidationException)?.RedirectUri;
                if (redirectUri != null)
                {
                    $"Откройте ссылку для подтверждения: {redirectUri}".LogWarn();
                    Console.WriteLine("После прохождения валидации нажмите Enter...");
                    Console.ReadLine();
                    try
                    {
                        api.Authorize(new ApiAuthParams
                        {
                            ApplicationId = 5776857, // VK iOS AppID
                            Login = withLogin,
                            Password = andPassword,
                            Settings = All,
                            TwoFactorAuthorization = () =>
                            {
                                "Enter 2FA code:".Log();
                                return Console.ReadLine()!;
                            }
                        });
                        break; // успех
                    }
                    catch (VkNet.Exception.NeedValidationException retryEx)
                    {
                        lastEx = retryEx;
                        validationAttempt++;
                        continue;
                    }
                    catch (Exception retryOther)
                    {
                        $"Ошибка при повторной авторизации: {retryOther.Message}\n{retryOther.StackTrace}".LogError();
                        throw;
                    }
                }
                else
                {
                    $"VK требует валидацию, но ссылка не предоставлена. {lastEx.Message}".LogError();
                    throw lastEx;
                }
            }
            if (validationAttempt == maxValidationAttempts)
            {
                int manualAttempts = 0;
                const int maxManualAttempts = 3;
                bool repeatManualAuth = true;
                while (manualAttempts < maxManualAttempts && repeatManualAuth)
                {
                    Console.WriteLine("\n--- ВНИМАНИЕ ---");
                    Console.WriteLine("VK требует ручную валидацию. Откройте в браузере следующую ссылку:");
                    var manualAuthUrl = $"https://oauth.vk.com/authorize?client_id=5776857&scope=all&response_type=token&v=5.131&display=page";
                    Console.WriteLine(manualAuthUrl);
                    Console.WriteLine("\nАвторизуйтесь, скопируйте access_token из адресной строки браузера (после #access_token=...), затем вставьте его сюда:");
                    Console.Write("access_token: ");
                    var tokenInput = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(tokenInput))
                    {
                        "Токен не был введён. Попробуйте ещё раз.".LogError();
                        manualAttempts++;
                        continue;
                    }
                    // Универсальная обработка access_token:
                    // 1. Если есть access_token=, взять значение между access_token= и первым &
                    // 2. Если строка начинается с vk1.a., взять до первого & (или всю строку, если & нет)
                    // 3. Иначе — взять строку как есть
                    string token;
                    if (tokenInput.Contains("access_token="))
                    {
                        var tokenMatch = System.Text.RegularExpressions.Regex.Match(tokenInput, @"access_token=([^&]+)");
                        token = tokenMatch.Success ? tokenMatch.Groups[1].Value : tokenInput.Trim();
                    }
                    else if (tokenInput.StartsWith("vk1.a."))
                    {
                        var ampIndex = tokenInput.IndexOf('&');
                        token = ampIndex > 0 ? tokenInput.Substring(0, ampIndex) : tokenInput.Trim();
                    }
                    else
                    {
                        token = tokenInput.Trim();
                    }

                    // Универсальная обработка user_id:
                    // 1. Всегда ищем user_id=... в исходной строке (tokenInput), независимо от формата
                    // 2. Если найден — используем, если нет — спрашиваем вручную
                    string userIdStr = "";
                    var userIdMatch = System.Text.RegularExpressions.Regex.Match(tokenInput, @"[&#]user_id=(\\d+)");
                    if (userIdMatch.Success)
                        userIdStr = userIdMatch.Groups[1].Value;
                    // Если user_id не найден автоматически, спрашиваем вручную
                    if (string.IsNullOrEmpty(userIdStr))
                    {
                        Console.Write("user_id (из того же адреса): ");
                        var input = Console.ReadLine();
                        userIdStr = input ?? "";
                    }
                    if (!long.TryParse(userIdStr, out var userId))
                    {
                        "user_id не был введён или некорректен. Попробуйте ещё раз.".LogError();
                        manualAttempts++;
                        continue;
                    }
                    try
                    {
                        $"DEBUG: access_token = '{token}', user_id = '{userIdStr}'".LogWarn();
                        WriteAllLines(Cache, new[] {withLogin, andPassword, token, userIdStr});
                        var manualApi = new VkApi();
                        manualApi.Authorize(new ApiAuthParams
                        {
                            AccessToken = token,
                            UserId = userId
                        });
                        $"Login as vk.com/id{userId} (manual token)".Log();
                        return manualApi;
                    }
                    catch (VkNet.Exception.VkApiException manualTokenError)
                    {
                        // Проверяем, что ошибка связана с правами/scopes
                        if (manualTokenError.Message.Contains("Access denied: no access to call this method. It cannot be called with current scopes."))
                        {
                            "VK API error: Access denied. Ваш токен не даёт доступ к нужным методам (scopes).".LogError();
                            Console.WriteLine("Возможно, вы не предоставили все разрешения при авторизации. Попробовать авторизоваться заново? (Y/N)");
                            var answer = Console.ReadLine();
                            if (!string.IsNullOrEmpty(answer) && answer.Trim().ToUpper() == "Y")
                            {
                                // Повторяем процесс авторизации
                                manualAttempts = 0; // Можно сбросить счётчик, чтобы дать ещё 3 попытки
                                continue;
                            }
                            else
                            {
                                Console.WriteLine("Спасибо за использование программы! Всего хорошего.");
                                Environment.Exit(0);
                            }
                        }
                        else
                        {
                            $"Ошибка авторизации с введённым токеном: {manualTokenError.Message}".LogError();
                            manualAttempts++;
                            continue;
                        }
                    }
                }
                throw new Exception($"Не удалось авторизоваться вручную после {maxManualAttempts} попыток.");
            }
        }
        $"Login as {api.GetUserLink()}".Log();
        WriteAllLines(Cache, new[] {withLogin, andPassword});
        return api;
    }

    /// <summary>
    /// Авторизация в VK с использованием кэша (файл .authorization)
    /// </summary>
    internal static VkApi LoginToVkApiWithCache()
    {
        "Reading login and password from cache...".Log();
        if (!Exists(Cache))
            throw new FileNotFoundException(
                $"Authorization cache wasn't found. Please restart application with login and password"
            );
        var lines = ReadAllLines(Cache);
        if (lines.Length == 2)
            return LoginToVkApi(withLogin: lines[0], andPassword: lines[1]);
        if (lines.Length >= 4)
        {
            var token = lines[2];
            var userIdStr = lines[3];
            if (!long.TryParse(userIdStr, out var userId))
                throw new IOException("Invalid user_id in authorization cache.");
            var api = new VkApi();
            api.Authorize(new ApiAuthParams
            {
                AccessToken = token,
                UserId = userId
            });
            $"Login as vk.com/id{userId} (manual token from cache)".Log();
            return api;
        }
        throw new IOException(
            $"Invalid authorization cache. Please restart application with login and password"
        );
    }

    /// <summary>
    /// Примитивный решатель капчи: выводит ссылку на капчу и ждёт ввода пользователя
    /// </summary>
    private class PrimitiveCaptchaSolver : ICaptchaSolver
    {
        public string Solve(string url)
        {
            $"Please enter captcha: {url}".LogWarn();
            return ReadLine()!;
        }

        public void CaptchaIsFalse()
        {
            "Invalid captcha!".LogError();
        }
    }
}
}
