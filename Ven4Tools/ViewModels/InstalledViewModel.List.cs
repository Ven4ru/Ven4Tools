using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Загрузка ────────────────────────────────────────────────────────────

        public async Task LoadAppsAsync()
        {
            ShowState("loading");

            // Под try — и сам запуск winget, а не только разбор его вывода: при
            // отсутствующем winget RunAsync бросает InvalidOperationException, и раньше
            // это исключение вылетало мимо обработчика. Вызов из Loaded его никому не
            // показывал, и вкладка навсегда оставалась на «Получение списка...».
            try
            {
                string rawOutput;
                Task? preload;
                lock (_preloadLock) { preload = _preloadTask; }
                if (preload != null)
                {
                    LoadingMessage = preload.IsCompleted
                        ? "⏳ Загрузка списка приложений..."
                        : "⏳ Почти готово, дожидаемся предзагрузки...";
                    // Сбой предзагрузки уже записан в журнал внутри самой задачи — здесь только
                    // не даём ему всплыть повторно, кэш в этом случае просто пуст.
                    try { await preload; } catch { }
                    // Чтение и обнуление кэша — атомарно под блокировкой
                    lock (_preloadLock)
                    {
                        rawOutput = _cachedRawOutput ?? string.Empty;
                        _preloadTask = null;
                        _cachedRawOutput = null;
                    }
                }
                else
                {
                    LoadingMessage = "⏳ Получение списка установленных приложений...";
                    var (_, output) = await WingetRunner.RunAsync(
                        $"list {WingetArgs.NonInteractiveLine}");
                    rawOutput = output;
                }

                _allApps = ParseWingetList(rawOutput);
                ApplyFilter();
                // Сообщение пустого состояния возвращаем к обычному: предыдущая попытка
                // могла оставить в нём текст ошибки.
                EmptyMessage = EmptyMessageDefault;
                ShowState(_allApps.Count == 0 ? "empty" : "list");
                RecomputeStats();
            }
            catch (Exception ex)
            {
                // Причина нужна в журнале: на экране остаётся одна строка сообщения,
                // а у сбоя winget бывает содержательный внутренний текст.
                AppLogger.Write(ex, "InstalledViewModel.LoadAppsAsync");
                // Именно «пусто», а не «загрузка»: состояние загрузки рисует бесконечную
                // полосу прогресса, и сообщение об ошибке под крутящейся полосой читалось
                // так, будто работа всё ещё идёт.
                EmptyMessage = $"❌ Ошибка: {ex.Message}";
                ShowState("empty");
            }
        }

        private static List<InstalledApp> ParseWingetList(string raw)
        {
            var result = new List<InstalledApp>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            // Убрать ANSI, нормализовать переводы строк
            var lines = WingetRunner.StripAnsi(raw).Replace("\r", "").Split('\n');

            // Ищем строку-заголовок: поддерживаем английский и русский вывод winget
            int headerIdx = Array.FindIndex(lines, l =>
                (l.Contains("Name") && l.Contains("Id") && l.Contains("Version")) ||
                (l.Contains("Имя")  && l.Contains("ИД") && l.Contains("Версия")));
            if (headerIdx < 0) return result;

            string header = lines[headerIdx];
            bool isRu = !header.Contains("Name");

            string nameCol      = isRu ? "Имя"      : "Name";
            string idCol        = isRu ? "ИД"        : "Id";
            string versionCol   = isRu ? "Версия"    : "Version";
            string availableCol = isRu ? "Доступна"  : "Available";
            string sourceCol    = isRu ? "Источник"  : "Source";

            // Убрать мусор до начала заголовка "Name"/"Имя" (ANSI-артефакты, отступы)
            int namePos = header.IndexOf(nameCol, StringComparison.Ordinal);
            if (namePos < 0) return result;
            int offset = namePos;

            // Позиции колонок относительно начала первой колонки
            int colName      = 0;
            int colId        = header.IndexOf(idCol,        namePos, StringComparison.Ordinal) - offset;
            int colVersion   = header.IndexOf(versionCol,   namePos, StringComparison.Ordinal) - offset;
            int colAvailable = header.IndexOf(availableCol, namePos, StringComparison.Ordinal) - offset;
            int colSource    = header.IndexOf(sourceCol,    namePos, StringComparison.Ordinal) - offset;
            if (colId <= 0 || colVersion <= 0) return result;
            if (colAvailable < 0) colAvailable = -1;
            if (colSource    < 0) colSource    = -1;

            bool started = false;
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    if (started) break; // пустая строка = начало футера
                    continue;
                }

                // Пропускаем строку-разделитель из дефисов — общим критерием
                // WingetRunner.IsTableSeparator, а не собственной копией условия
                if (WingetRunner.IsTableSeparator(rawLine)) continue;

                // Выровнять строку по offset заголовка
                string line = rawLine.Length > offset ? rawLine.Substring(offset) : rawLine;

                string name      = Extract(line, colName,    colId);
                string id        = Extract(line, colId,      colVersion);
                string version   = Extract(line, colVersion, colAvailable >= 0 ? colAvailable : line.Length);
                string available = colAvailable >= 0 ? Extract(line, colAvailable, colSource >= 0 ? colSource : line.Length) : "";
                string source    = colSource    >= 0 ? Extract(line, colSource,    line.Length) : "";

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) continue;

                started = true;
                result.Add(new InstalledApp
                {
                    Name      = name.Trim(),
                    WingetId  = id.Trim(),
                    Version   = version.Trim(),
                    Available = available.Trim(),
                    Source    = source.Trim()
                });
            }

            return result;
        }

        private static string Extract(string line, int from, int to)
        {
            if (from >= line.Length) return "";
            int end = Math.Min(to, line.Length);
            return line.Substring(from, end - from);
        }
    }
}
