using System.ComponentModel;
using Ven4Tools.Models;

namespace Ven4Tools.Tests;

public sealed class AppInstallProgressPhaseTests
{
    [Fact]
    public void Phase_DefaultsToDownload()
    {
        // Значение по умолчанию важно: до первого Report() объект уже должен
        // иметь осмысленную фазу для биндинга (InstallPhaseToBrushConverter).
        var progress = new AppInstallProgress();

        Assert.Equal(InstallPhase.Download, progress.Phase);
    }

    [Fact]
    public void Phase_ChangeRaisesPropertyChanged()
    {
        // ProgressBar.Foreground в CatalogTab.xaml биндится напрямую на Phase —
        // без PropertyChanged смена фазы не долетит до уже отрисованного элемента
        // списка (тот же класс бага, что описан в комментарии над AppInstallProgress
        // про Status/Percentage).
        var progress = new AppInstallProgress();
        string? raisedProperty = null;
        ((INotifyPropertyChanged)progress).PropertyChanged += (_, e) => raisedProperty = e.PropertyName;

        progress.Phase = InstallPhase.Installing;

        Assert.Equal(nameof(AppInstallProgress.Phase), raisedProperty);
        Assert.Equal(InstallPhase.Installing, progress.Phase);
    }

    [Theory]
    [InlineData(InstallPhase.Download, 0, false, 0.0)]
    [InlineData(InstallPhase.Download, 40, false, 20.0)]
    [InlineData(InstallPhase.Download, 100, false, 50.0)]
    [InlineData(InstallPhase.Download, 0, true, 25.0)]
    [InlineData(InstallPhase.Installing, 0, false, 50.0)]
    [InlineData(InstallPhase.Installing, 100, false, 100.0)]
    [InlineData(InstallPhase.Installing, 0, true, 75.0)]
    [InlineData(InstallPhase.Done, 0, false, 100.0)]
    [InlineData(InstallPhase.Error, 0, false, 100.0)]
    public void EffectiveProgress_MapsPhaseAndPercentageToMonotonicRange(
        InstallPhase phase, int percentage, bool isIndeterminate, double expected)
    {
        // Агрегированная шкала по всей очереди (CatalogViewModel.OverallProgressPercentage)
        // усредняет EffectiveProgress, а не сырой Percentage — без этой развязки смена
        // фазы Download -> Installing (Percentage сбрасывается на 0) заставила бы общий
        // прогресс визуально "прыгать назад".
        var progress = new AppInstallProgress
        {
            Phase = phase,
            Percentage = percentage,
            IsIndeterminate = isIndeterminate
        };

        Assert.Equal(expected, progress.EffectiveProgress);
    }

    [Fact]
    public void EffectiveProgress_NeverDecreases_AcrossDownloadToInstallingTransition()
    {
        // Явная проверка инварианта "полоска не прыгает назад": конец фазы Download
        // (100%) не должен давать более высокую агрегированную оценку, чем начало
        // фазы Installing сразу после переключения.
        var endOfDownload = new AppInstallProgress { Phase = InstallPhase.Download, Percentage = 100 };
        var startOfInstalling = new AppInstallProgress { Phase = InstallPhase.Installing, Percentage = 0, IsIndeterminate = true };

        Assert.True(startOfInstalling.EffectiveProgress >= endOfDownload.EffectiveProgress);
    }

    [Fact]
    public void Outcome_DefaultsToNotYetDetermined()
    {
        // До финального отчёта об установке (ReportInstallOutcomeAsync) все
        // промежуточные статусы («Скачивание...», «Установка...») не должны
        // выглядеть как уже подтверждённый или проваленный результат.
        var progress = new AppInstallProgress();

        Assert.Equal(InstallOutcome.NotYetDetermined, progress.Outcome);
    }

    [Fact]
    public void Outcome_ChangeRaisesPropertyChanged()
    {
        // InstallOutcomeToBrushConverter в CatalogTab.xaml биндится напрямую на
        // Outcome — без PropertyChanged смена итога не долетит до уже
        // отрисованного элемента списка (тот же класс бага, что у Phase/Status).
        var progress = new AppInstallProgress();
        string? raisedProperty = null;
        ((INotifyPropertyChanged)progress).PropertyChanged += (_, e) => raisedProperty = e.PropertyName;

        progress.Outcome = InstallOutcome.ConfirmedSuccess;

        Assert.Equal(nameof(AppInstallProgress.Outcome), raisedProperty);
        Assert.Equal(InstallOutcome.ConfirmedSuccess, progress.Outcome);
    }
}
