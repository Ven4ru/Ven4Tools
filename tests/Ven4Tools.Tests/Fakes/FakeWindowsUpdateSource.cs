using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests.Fakes;

public sealed class FakeWindowsUpdateSource : IWindowsUpdateSource
{
    public List<WindowsUpdateItem> Items { get; } = new();
    public bool ServiceRunning { get; set; } = true;
    public bool RebootPending { get; set; }
    public bool SearchShouldFail { get; set; }
    public string SearchFailureMessage { get; set; } = "";
    public List<string> InstallCallsReceived { get; } = new();
    public HashSet<string> ItemIdsThatFailInstall { get; } = new();
    public int SearchCallCount { get; private set; }

    // ── Фоновое скачивание (DownloadOnlyAsync) ────────────────────────────────
    // Отдельные счётчики от установочных: главная проверка фонового режима —
    // что скачивание случилось, а установка при этом НЕ случилась.
    public List<string> DownloadCallsReceived { get; } = new();
    public int DownloadCallCount { get; private set; }
    public HashSet<string> ItemIdsThatFailDownload { get; } = new();
    public bool DownloadShouldFailOutright { get; set; }
    public string DownloadFailureMessage { get; set; } = "тестовый отказ скачивания";

    // Задвижка для тестов состояния семафоров: DownloadStarted сигналит, что вызов
    // уже внутри метода, DownloadRelease держит его там, пока тест не отпустит.
    // Без этого проверить «что видно снаружи ВО ВРЕМЯ скачивания» невозможно —
    // фейк отрабатывает мгновенно.
    public TaskCompletionSource<bool>? DownloadStarted { get; set; }
    public Task? DownloadRelease { get; set; }

    public bool IsServiceRunning() => ServiceRunning;
    public bool TryStartService() { ServiceRunning = true; return true; }
    public bool IsRebootPending() => RebootPending;

    public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct)
    {
        SearchCallCount++;
        if (SearchShouldFail)
            return Task.FromResult(WindowsUpdateSearchResult.Failed(SearchFailureMessage));
        return Task.FromResult(WindowsUpdateSearchResult.Ok(Items));
    }

    public Task<WindowsUpdateInstallOutcome> InstallAsync(
        IReadOnlyList<string> updateIds,
        IProgress<WindowsUpdateProgress> progress,
        CancellationToken ct)
    {
        InstallCallsReceived.AddRange(updateIds);
        var outcomes = updateIds.Select(id =>
        {
            var item = Items.FirstOrDefault(i => i.UpdateId == id);
            bool fails = ItemIdsThatFailInstall.Contains(id);
            progress.Report(new WindowsUpdateProgress
            {
                CurrentTitle = item?.Title ?? id,
                Phase = "Установка",
                CompletedCount = 1,
                TotalCount = updateIds.Count,
                PercentComplete = 100
            });
            return new WindowsUpdateItemOutcome
            {
                UpdateId = id,
                Title = item?.Title ?? id,
                Success = !fails,
                ErrorMessage = fails ? "тестовая ошибка" : ""
            };
        }).ToList();

        return Task.FromResult(new WindowsUpdateInstallOutcome
        {
            Success = outcomes.All(o => o.Success),
            Items = outcomes,
            RebootRequired = RebootPending
        });
    }

    public async Task<WindowsUpdateDownloadOutcome> DownloadOnlyAsync(
        IReadOnlyList<string> updateIds,
        IProgress<WindowsUpdateProgress> progress,
        CancellationToken ct)
    {
        DownloadCallCount++;
        DownloadCallsReceived.AddRange(updateIds);

        DownloadStarted?.TrySetResult(true);
        if (DownloadRelease != null) await DownloadRelease;

        if (DownloadShouldFailOutright)
            return new WindowsUpdateDownloadOutcome
            {
                Success = false,
                ErrorMessage = DownloadFailureMessage
            };

        var outcomes = updateIds.Select(id =>
        {
            var item = Items.FirstOrDefault(i => i.UpdateId == id);
            bool fails = ItemIdsThatFailDownload.Contains(id);
            progress.Report(new WindowsUpdateProgress
            {
                CurrentTitle = item?.Title ?? id,
                Phase = "Скачивание",
                CompletedCount = 1,
                TotalCount = updateIds.Count,
                PercentComplete = 100
            });
            return new WindowsUpdateItemOutcome
            {
                UpdateId = id,
                Title = item?.Title ?? id,
                Success = !fails,
                ErrorMessage = fails ? "тестовая ошибка скачивания" : ""
            };
        }).ToList();

        return new WindowsUpdateDownloadOutcome
        {
            Success = outcomes.All(o => o.Success),
            Items = outcomes
        };
    }
}
