using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class InstallOutcomeEvaluatorTests
{
    private static InstallOutcomeContext Ctx(
        bool verificationSupported = true,
        bool exitCodeSuccess = true,
        bool wasInstalledBefore = false,
        string? versionBefore = null,
        bool foundAfter = false,
        string? versionAfter = null) => new()
    {
        VerificationSupported = verificationSupported,
        ExitCodeSuccess = exitCodeSuccess,
        WasInstalledBefore = wasInstalledBefore,
        VersionBefore = versionBefore,
        FoundAfter = foundAfter,
        VersionAfter = versionAfter
    };

    [Fact]
    public void NotInstalledBefore_SuccessExitCode_FoundAfter_IsConfirmedSuccess()
    {
        // Обычный случай: не стояло, код успеха, после установки нашли в системе.
        var ctx = Ctx(wasInstalledBefore: false, exitCodeSuccess: true, foundAfter: true, versionAfter: "1.0.0");

        Assert.Equal(InstallOutcome.ConfirmedSuccess, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void NotInstalledBefore_SuccessExitCode_NotFoundAfter_IsUnconfirmed()
    {
        // Инсталлятор соврал (или тихо не сработал): код успеха, но по факту
        // приложения нет даже после ретраев — не пишем «Установлено».
        var ctx = Ctx(wasInstalledBefore: false, exitCodeSuccess: true, foundAfter: false);

        Assert.Equal(InstallOutcome.Unconfirmed, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void NotInstalledBefore_ErrorExitCode_NotFoundAfter_IsConfirmedFailure()
    {
        // Согласованный обычный случай неудачи: код ошибки, по факту не появилось.
        var ctx = Ctx(wasInstalledBefore: false, exitCodeSuccess: false, foundAfter: false);

        Assert.Equal(InstallOutcome.ConfirmedFailure, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void NotInstalledBefore_ErrorExitCode_ButFoundAfter_IsConfirmedSuccess()
    {
        // Честная коррекция: код выхода — ошибка, но установщик на самом деле
        // справился (нашли по факту) — не доверяем слепо плохому коду выхода.
        var ctx = Ctx(wasInstalledBefore: false, exitCodeSuccess: false, foundAfter: true, versionAfter: "2.3.1");

        Assert.Equal(InstallOutcome.ConfirmedSuccess, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void AlreadyInstalled_SuccessExitCode_SameVersionAfter_IsAlreadyUpToDate()
    {
        // Уже стояло, код успеха, версия после установки не изменилась —
        // типичный no-op тихого инсталлятора, не «только что поставили».
        var ctx = Ctx(wasInstalledBefore: true, versionBefore: "1.5.0",
            exitCodeSuccess: true, foundAfter: true, versionAfter: "1.5.0");

        Assert.Equal(InstallOutcome.AlreadyUpToDate, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void AlreadyInstalled_OlderVersion_SuccessExitCode_VersionIncreased_IsConfirmedSuccess()
    {
        // Уже стояла старая версия, код успеха, версия после — другая:
        // обновление подтверждено по факту.
        var ctx = Ctx(wasInstalledBefore: true, versionBefore: "1.0.0",
            exitCodeSuccess: true, foundAfter: true, versionAfter: "2.0.0");

        Assert.Equal(InstallOutcome.ConfirmedSuccess, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void AlreadyInstalled_ErrorExitCode_SameVersionAfter_IsAlreadyUpToDate()
    {
        // Уже стояло, инсталлятор вернул ошибку (например отказался переустанавливать
        // ту же версию), по факту версия та же — цель фактически достигнута
        // (приложение стоит), это не провал установки.
        var ctx = Ctx(wasInstalledBefore: true, versionBefore: "3.2.0",
            exitCodeSuccess: false, foundAfter: true, versionAfter: "3.2.0");

        Assert.Equal(InstallOutcome.AlreadyUpToDate, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void RebootRequired_FoundAfterDespitePendingReboot_IsConfirmedSuccess()
    {
        // 3010 (нужна перезагрузка) — но по факту уже нашли в системе:
        // сильное подтверждение, несмотря на отложенную перезагрузку.
        var ctx = Ctx(wasInstalledBefore: false, exitCodeSuccess: true, foundAfter: true, versionAfter: "4.4.4");

        Assert.Equal(InstallOutcome.ConfirmedSuccess, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void RebootRequired_NotFoundAfter_IsUnconfirmed()
    {
        // 3010, но по факту ещё не появилось (часть инсталляторов дописывают
        // реестр только после перезагрузки) — честно «не подтверждено», не провал.
        var ctx = Ctx(wasInstalledBefore: false, exitCodeSuccess: true, foundAfter: false);

        Assert.Equal(InstallOutcome.Unconfirmed, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void VerificationNotSupported_SuccessExitCode_IsNotYetDetermined()
    {
        // Нет надёжного ID для сверки (напр. пользовательское приложение только
        // с ChocoId) — не изобретаем фиктивную проверку, честно «не проверено».
        var ctx = Ctx(verificationSupported: false, exitCodeSuccess: true);

        Assert.Equal(InstallOutcome.NotYetDetermined, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Fact]
    public void VerificationNotSupported_ErrorExitCode_IsNotYetDetermined()
    {
        var ctx = Ctx(verificationSupported: false, exitCodeSuccess: false);

        Assert.Equal(InstallOutcome.NotYetDetermined, InstallOutcomeEvaluator.Evaluate(ctx));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData(" 1.2.3 ", "1.2.3", true)]
    [InlineData("1.2.3-BETA", "1.2.3-beta", true)]
    [InlineData(null, null, true)]
    [InlineData(null, "1.0.0", false)]
    public void VersionComparison_IsCaseInsensitiveAndTrimmed_ViaOutcomeDifference(
        string? before, string? after, bool expectAlreadyUpToDate)
    {
        // Косвенная проверка сравнения версий через видимое поведение Evaluate,
        // а не через приватный метод напрямую — VersionsEqual приватен намеренно
        // (деталь реализации), наблюдаемый эффект — выбор между AlreadyUpToDate
        // и ConfirmedSuccess при wasInstalledBefore=true.
        var ctx = Ctx(wasInstalledBefore: true, versionBefore: before,
            exitCodeSuccess: true, foundAfter: true, versionAfter: after);

        var expected = expectAlreadyUpToDate ? InstallOutcome.AlreadyUpToDate : InstallOutcome.ConfirmedSuccess;
        Assert.Equal(expected, InstallOutcomeEvaluator.Evaluate(ctx));
    }
}
