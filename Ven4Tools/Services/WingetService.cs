using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    public static class WingetService
    {
        // Верхняя граница ожидания интерактивных winget-вызовов (search/show) из диалогов.
        // Хватает для медленного источника, но не даёт зависшему winget (сетевой столл,
        // запрос соглашения источника) держать UI в состоянии «поиск…» и оставлять
        // дочерний winget.exe живым до закрытия всего клиента.
        private static readonly TimeSpan UiCallTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Search winget for packages matching <paramref name="query"/>.
        /// Returns up to 15 deduplicated results (IDs must contain a dot).
        /// Бросает <see cref="TimeoutException"/>, если winget не ответил за <see cref="UiCallTimeout"/>.
        /// </summary>
        public static async Task<List<WingetPackage>> SearchAsync(
            string query, CancellationToken token = default)
        {
            var results = new List<WingetPackage>();
            query = CommandLineGuard.SanitizeQuery(query);
            if (string.IsNullOrEmpty(query)) return results;

            // Внутренний таймаут поверх внешнего токена: даже без токена (диалоги
            // передают default) зависший winget будет принудительно завершён.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(UiCallTimeout);
            try
            {
                // ProcessStartInfo собираем общей фабрикой WingetRunner: аргументы идут
                // отдельными токенами через ArgumentList (.NET экранирует каждый), поэтому
                // пользовательский ввод не может «вырваться» из кавычек в посторонние
                // winget-флаги — устойчиво даже при ослаблении набора символов в
                // CommandLineGuard, в отличие от прямой строковой интерполяции.
                var psi = WingetRunner.CreateStartInfo(new[]
                {
                    "search", "--name", query, "--source", "winget", "--accept-source-agreements"
                });
                if (psi == null) return results;

                using var process = Process.Start(psi);
                if (process == null) return results;

                // Kill(entireProcessTree) — по таймауту/отмене убиваем winget и всех его
                // потомков, иначе пайп не закрывается и ReadToEndAsync зависает.
                using var reg = timeoutCts.Token.Register(() =>
                    { try { process.Kill(entireProcessTree: true); } catch { } });

                var stderrTask = process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(timeoutCts.Token);
                await stderrTask;

                bool headerPassed = false;
                foreach (var line in output.Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!headerPassed)
                    {
                        if (WingetRunner.IsTableSeparator(line)) headerPassed = true;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length < 2) continue;

                    string id = parts[1].Trim();
                    if (!id.Contains('.')) continue; // настоящие winget ID всегда содержат точку

                    results.Add(new WingetPackage
                    {
                        Name    = parts[0].Trim(),
                        Id      = id,
                        Version = parts.Length > 2 ? parts[2].Trim() : "",
                        Source  = parts.Length > 3 ? parts[3].Trim() : "winget"
                    });
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // Именно таймаут (а не отмена пользователем через внешний токен) —
                // сообщаем вызывающему явным исключением, чтобы UI показал ошибку
                // вместо «вечного поиска».
                throw new TimeoutException("Превышено время ожидания ответа winget при поиске.");
            }
            catch (OperationCanceledException) { }
            catch { }

            return results
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .Take(15)
                .ToList();
        }

        /// <summary>
        /// Validate a winget package by exact ID. Returns (Name, Version) or (null, null).
        /// Бросает <see cref="TimeoutException"/>, если winget не ответил за <see cref="UiCallTimeout"/>.
        /// </summary>
        public static async Task<(string? Name, string? Version)> ValidateIdAsync(
            string id, CancellationToken token = default)
        {
            if (!CommandLineGuard.ValidateId(id)) return (null, null);

            // Внутренний таймаут: раньше show/WaitForExitAsync был без ограничения —
            // зависший winget держал диалог в «проверяем…» бесконечно.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(UiCallTimeout);
            try
            {
                // ProcessStartInfo собираем общей фабрикой WingetRunner: аргументы идут
                // отдельными токенами через ArgumentList (.NET экранирует каждый), поэтому
                // пользовательский ввод не может «вырваться» из кавычек в посторонние
                // winget-флаги — устойчиво даже при ослаблении набора символов в
                // CommandLineGuard, в отличие от прямой строковой интерполяции.
                var psi = WingetRunner.CreateStartInfo(new[]
                {
                    "show", "--id", id, "-e", "--source", "winget", "--accept-source-agreements"
                });
                if (psi == null) return (null, null);

                using var process = Process.Start(psi);
                if (process == null) return (null, null);

                // Kill(entireProcessTree) — по таймауту/отмене убиваем всё дерево winget.
                using var reg = timeoutCts.Token.Register(() =>
                    { try { process.Kill(entireProcessTree: true); } catch { } });

                var stderrTask = process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(timeoutCts.Token);
                await stderrTask;
                if (process.ExitCode != 0) return (null, null);

                string? name = null, version = null;
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (name == null)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(t,
                            @"(?:Found|Найдено)\s+(.+?)\s+\[");
                        if (m.Success) { name = m.Groups[1].Value.Trim(); continue; }
                    }
                    if (version == null &&
                        (t.StartsWith("Version:") || t.StartsWith("Версия:")))
                    {
                        version = t.Split(':', 2).Last().Trim();
                    }
                }
                return (name, version);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // Таймаут (не отмена пользователем) — отличаем от «не найдено» (null,null),
                // чтобы диалог показал понятное сообщение.
                throw new TimeoutException("Превышено время ожидания ответа winget при проверке ID.");
            }
            catch { return (null, null); }
        }
    }
}
