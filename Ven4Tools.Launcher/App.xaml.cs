using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher;

public partial class App : Application
{
    private static Mutex? _mutex;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        DispatcherUnhandledException += OnDispatcherException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Тёмный системный заголовок на всех окнах — иначе native title bar
        // остаётся светлым поверх тёмной темы приложения даже при тёмной теме Windows.
        try { WindowChromeHelper.RegisterGlobalDarkTitleBar(); } catch { }

        if (Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST") == "1")
        {
            base.OnStartup(e);
            return;
        }

        string? installFromPath = null;
        bool silentInstall = false;
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--install-from=", StringComparison.OrdinalIgnoreCase))
                installFromPath = arg["--install-from=".Length..].Trim('"');
            else if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase))
                silentInstall = true;
        }

        if (installFromPath != null)
        {
            _mutex = new Mutex(true, "Ven4Tools.Launcher.SingleInstance", out bool createdNewCli);
            if (!createdNewCli)
            {
                Console.Error.WriteLine("Ven4Tools Launcher уже запущен.");
                _mutex.Dispose();
                _mutex = null;
                Shutdown(3);
                return;
            }

            // Явный режим завершения: при нуле показанных окон поведение WPF-режима
            // по умолчанию (OnLastWindowClose) для этого пути не проверялось —
            // завершаем процесс сами, точным кодом возврата, без зависимости от
            // подсчёта окон.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();

            // КРИТИЧНО: не блокировать здесь синхронно (.GetAwaiter().GetResult()) —
            // Dispatcher.Run() запускается только ПОСЛЕ возврата из OnStartup, а
            // InstallFromLocalArchiveAsync использует Dispatcher.Invoke, которому
            // для работы нужен уже запущенный цикл диспетчера. Синхронная блокировка
            // здесь = гарантированный deadlock (воспроизведено эмпирически при
            // исполнении этой задачи). BeginInvoke ставит колбэк в очередь и
            // возвращается немедленно — OnStartup завершается, Application.Run()
            // запускает Dispatcher.Run(), и только тогда колбэк реально выполняется.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                int exitCode = await CliInstallRunner.RunAsync(window, installFromPath, silentInstall);
                ReleaseSingleInstanceMutex();
                Shutdown(exitCode);
            }));
            return;
        }

        _mutex = new Mutex(true, "Ven4Tools.Launcher.SingleInstance", out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Ven4Tools Launcher уже запущен.",
                "Уже запущен",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _mutex.Dispose();
            // Обнуляем поле: иначе OnExit вызовет ReleaseMutex() на уже освобождённом
            // мьютексе → ObjectDisposedException.
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    /// <summary>
    /// Освобождает мьютекс единственного экземпляра до завершения процесса.
    /// Нужно вызывать перед запуском повышенной копии лаунчера (RestartAsAdmin) —
    /// иначе новый процесс может увидеть мьютекс ещё занятым и выйти как "уже запущен".
    /// Идемпотентно: повторный вызов (например, из OnExit) безопасен.
    /// </summary>
    public static void ReleaseSingleInstanceMutex()
    {
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }

    /// <summary>
    /// Восстанавливает мьютекс единственного экземпляра после неудачной попытки
    /// перезапуска с правами администратора (пользователь отклонил UAC, exeName
    /// не найден и т.п.) — вызывается, когда ReleaseSingleInstanceMutex() уже
    /// сработал, но повышенная копия так и не стартовала, и текущий процесс
    /// продолжает работать.
    ///
    /// Признак владения (createdNew) обязателен ровно так же, как в OnStartup:
    /// пока висит запрос UAC, мьютекс отпущен, и за эти секунды другой запуск
    /// лаунчера успевает его занять. Тогда конструктор владения НЕ даёт, а поле
    /// всё равно заполнялось — и ReleaseSingleInstanceMutex вызывал ReleaseMutex
    /// на невладеемом мьютексе, то есть ApplicationException при завершении.
    /// Инвариант поля: непустое ⇒ мьютекс принадлежит этому процессу.
    /// </summary>
    public static void ReacquireSingleInstanceMutex()
    {
        if (_mutex != null) return;

        var mutex = new Mutex(true, "Ven4Tools.Launcher.SingleInstance", out bool createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            return;
        }

        // Мьютекс уже занят другим экземпляром — владения нет, держать нечего.
        mutex.Dispose();
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown");
        WriteLauncherCrash(ex);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteLauncherCrash(e.Exception);
        // Завершаем работу после записи лога — продолжать с повреждённым состоянием UI небезопасно
        Application.Current?.Shutdown(1);
    }

    /// <summary>Сколько файлов launcher_crash_*.txt хранить на диске.</summary>
    private const int MaxLauncherCrashFiles = 20;

    private static void WriteLauncherCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "logs");
            Directory.CreateDirectory(dir);
            // Миллисекунды + GUID исключают коллизию имён при двух крашах в одну секунду
            var file = Path.Combine(dir, $"launcher_crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.txt");
            string text = $"[{DateTime.UtcNow:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            // Текст очищается от путей профиля, имени пользователя и имени машины
            // ровно так же, как клиент чистит свой crash_last.json в момент записи:
            // файл лежит в папке, которую пользователь открывает кнопкой «Открыть
            // папку логов» и прикладывает к обращениям, поэтому имена не должны
            // попадать туда изначально. Отдельный try: мы уже в обработчике
            // необработанного исключения, и сбой самой очистки не должен стоить
            // всего отчёта — тогда пишем исходный текст.
            try { text = Services.GitHubService.SanitizePersonalData(text); } catch { }
            // Через FileHelper, а не голым File.WriteAllText: он проверяет каталог и
            // целевой файл на подмену reparse point'ом. Единственное место в лаунчере,
            // писавшее в %LocalAppData%\Ven4Tools в обход этого guard'а, — хотя дерево
            // то же самое, доступное на запись обычному процессу пользователя, а лаунчер
            // штатно оказывается elevated при запуске «от имени администратора»
            // (обоснование целиком — в Helpers/FileHelper.cs).
            Helpers.FileHelper.WriteAllTextAtomic(file, text);
            TrimOldLauncherCrashFiles(dir);
        }
        catch { }
    }

    /// <summary>
    /// Оставляет не более <see cref="MaxLauncherCrashFiles"/> последних файлов
    /// launcher_crash_*.txt. Ни один код их не читает и не удаляет — без уборки
    /// каждый вылет добавлял файл навсегда. Сортировка по имени: оно начинается с
    /// отсортированной метки времени, поэтому не зависит от времени файловой системы
    /// (его меняет копирование папки). Тот же приём, что у журналов установки клиента.
    /// </summary>
    private static void TrimOldLauncherCrashFiles(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "launcher_crash_*.txt");
            if (files.Length <= MaxLauncherCrashFiles) return;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length - MaxLauncherCrashFiles; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch { }
    }
}
