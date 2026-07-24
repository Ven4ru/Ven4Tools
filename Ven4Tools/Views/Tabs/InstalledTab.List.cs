using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class InstalledTab : UserControl
    {
        // Фоновая предзагрузка — запускается из MainWindow.Loaded, до открытия вкладки.
        // Первое открытие вкладки просто awaits уже идущую задачу вместо нового winget list.
        public static void StartPreload()
        {
            lock (_preloadLock)
            {
                if (_preloadTask != null) return;
                _preloadTask = Task.Run(async () =>
                {
                    try
                    {
                        var (_, output) = await WingetRunner.RunAsync(
                            "list --accept-source-agreements --disable-interactivity");
                        _cachedRawOutput = output;
                    }
                    catch { _cachedRawOutput = string.Empty; }
                });
            }
        }

        // ── Загрузка ────────────────────────────────────────────────────────────

        private async Task LoadAppsAsync()
        {
            ShowState("loading");

            string rawOutput;
            Task? preload;
            lock (_preloadLock) { preload = _preloadTask; }
            if (preload != null)
            {
                txtLoadingMsg.Text = preload.IsCompleted
                    ? "⏳ Загрузка списка приложений..."
                    : "⏳ Почти готово, дожидаемся предзагрузки...";
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
                txtLoadingMsg.Text = "⏳ Получение списка установленных приложений...";
                var (_, output) = await WingetRunner.RunAsync(
                    "list --accept-source-agreements --disable-interactivity");
                rawOutput = output;
            }

            try
            {
                _allApps = ParseWingetList(rawOutput);
                ApplyFilter();
                ShowState(_allApps.Count == 0 ? "empty" : "list");
                UpdateStats();
            }
            catch (Exception ex)
            {
                ShowState("loading");
                txtLoadingMsg.Text = $"❌ Ошибка: {ex.Message}";
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

                // Пропускаем строку-разделитель из дефисов
                string t = rawLine.Trim();
                if (t.Length >= 5 && t.All(c => c == '-' || c == ' ')) continue;

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
