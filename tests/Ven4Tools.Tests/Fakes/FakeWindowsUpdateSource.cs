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
}
