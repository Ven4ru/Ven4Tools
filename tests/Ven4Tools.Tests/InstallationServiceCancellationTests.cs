using System.Diagnostics;

using Ven4Tools.Services;

namespace Ven4Tools.Tests;

// Отмена установки обязана ЗАВЕРШАТЬ запущенный установщик, а не только вернуть
// управление. Раньше цикл ожидания в RunElevatedInstallerAsync передавал токен в
// Task.Delay, и отмена, пришедшая во время паузы, выбрасывала исключение прямо из
// Delay — в обход ветки Kill. А внутри паузы приходит практически любая отмена:
// проверка вверху цикла занимает микросекунды против 100 мс ожидания, то есть
// Kill был фактически недостижим. Итог: интерфейс показывал «⏹️ Отменено», пока
// msiexec продолжал молча устанавливать приложение — худший вид расхождения между
// тем, что видит пользователь, и тем, что происходит с его системой.
//
// Тест работает на настоящем процессе (обычном, без повышения прав — метод
// принимает готовый ProcessStartInfo и про Verb=runas ничего не знает), потому что
// проверяемое поведение — это именно взаимодействие отмены с живым процессом.
public sealed class InstallationServiceCancellationTests
{
    // Достаточно долгий безобидный процесс: если Kill не сработает, он переживёт
    // тест и будет найден живым.
    private static ProcessStartInfo LongRunningProcess() =>
        new("cmd.exe", "/c ping -n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

    [Fact]
    public async Task ОтменаЗавершаетПроцессУстановщика()
    {
        using var cts = new CancellationTokenSource();
        int pid = 0;

        // onStarted вызывается синхронно, до первого await — pid известен сразу.
        var run = InstallationService.RunElevatedInstallerAsync(
            LongRunningProcess(), cts.Token, id => pid = id);

        Assert.NotEqual(0, pid);
        Assert.False(HasExited(pid), "процесс должен быть жив до отмены");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run!);

        // Главное утверждение: установщик именно УБИТ. До фикса он оставался жив и
        // продолжал работу ещё десятки секунд после «отмены».
        Assert.True(
            await WaitForExitAsync(pid, TimeSpan.FromSeconds(10)),
            "после отмены процесс установщика обязан быть завершён");
    }

    [Fact]
    public async Task БезОтменыВозвращаетКодВыхода()
    {
        // Контрольный случай: обычное завершение по-прежнему отдаёт код выхода,
        // а непрерываемая пауза в цикле ожидания не мешает дождаться процесса.
        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var result = await InstallationService.RunElevatedInstallerAsync(psi, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Value.Ok);
        Assert.False(result.Value.Reboot);
        Assert.Equal(0, result.Value.ExitCode);
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            // Процесса с таким идентификатором уже нет — значит завершился.
            return true;
        }
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (HasExited(pid)) return true;
            await Task.Delay(50);
        }
        return false;
    }
}
