using System;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Итог сопоставления заявленного результата установки (код выхода процесса)
    /// с фактическим состоянием системы (winget list до и после). Раньше клиент
    /// доверял только коду выхода — тихий установщик мог вернуть 0 и ничего не
    /// поставить, либо наоборот, вернуть код ошибки при фактически успешной
    /// установке. Значения ниже — это реально различимые по данным ситуации,
    /// а не искусственное дробление ради количества.
    /// </summary>
    public enum InstallOutcome
    {
        /// <summary>
        /// Проверка по факту не выполнялась или невозможна (нет надёжного winget-ID
        /// для сверки — например пользовательское приложение только с ChocoId,
        /// см. <see cref="CommandLineGuard"/>-подобный признак «User.» в ID). В этом
        /// случае результат определяется по коду выхода, как и раньше — честно
        /// помечено отдельным значением, а не выдаваемая за подтверждённую проверку.
        /// </summary>
        NotYetDetermined,

        /// <summary>Установка подтверждена по факту: приложение найдено в системе
        /// после установки, и (если оно уже стояло раньше) версия действительно
        /// изменилась.</summary>
        ConfirmedSuccess,

        /// <summary>Приложение уже было установлено до начала, и после установки
        /// версия не изменилась — реального изменения не произошло, хотя код
        /// выхода и мог быть успешным (типичный no-op тихого инсталлятора на уже
        /// установленной версии).</summary>
        AlreadyUpToDate,

        /// <summary>Код выхода — успех, но по факту приложение не найдено в системе
        /// даже после повторных проверок. Либо инсталлятор тихо не сработал, либо
        /// (при отложенной перезагрузке) часть инсталляторов дописывают реестр
        /// только после неё — в обоих случаях недостаточно оснований для
        /// «Установлено», но и однозначной ошибки тоже нет.</summary>
        Unconfirmed,

        /// <summary>Код выхода — ошибка, и по факту приложение действительно не
        /// найдено в системе. Согласованный, обычный случай неудачной установки.</summary>
        ConfirmedFailure
    }

    /// <summary>
    /// Входные данные для <see cref="InstallOutcomeEvaluator.Evaluate"/>. Все значения
    /// берутся извне (код выхода процесса, snapshot winget list до/после) — сам метод
    /// не делает никакого I/O, поэтому классификация юнит-тестируется напрямую, без
    /// реального установщика или реестра.
    /// </summary>
    public readonly struct InstallOutcomeContext
    {
        /// <summary>Есть ли вообще надёжный ID для сверки с winget list. False для
        /// пользовательских приложений без winget-каталожной записи (только ChocoId).</summary>
        public bool VerificationSupported { get; init; }

        /// <summary>Код выхода установщика — успех (0 или 3010/ERROR_SUCCESS_REBOOT_REQUIRED).</summary>
        public bool ExitCodeSuccess { get; init; }

        /// <summary>Было ли приложение установлено до начала (baseline, снят перед стартом).</summary>
        public bool WasInstalledBefore { get; init; }

        /// <summary>Версия до начала установки (null/пусто, если не было установлено).</summary>
        public string? VersionBefore { get; init; }

        /// <summary>Найдено ли приложение в системе после установки (с ретраями/backoff).</summary>
        public bool FoundAfter { get; init; }

        /// <summary>Версия после установки (null/пусто, если не найдено).</summary>
        public string? VersionAfter { get; init; }
    }

    /// <summary>
    /// Чистая функция принятия решения: сопоставляет код выхода установщика с
    /// фактическим состоянием системы до/после и классифицирует результат.
    /// Вынесена из <see cref="InstallationService"/> отдельным статическим классом
    /// специально для того, чтобы прогнать всю матрицу реалистичных комбинаций
    /// юнит-тестами без живого процесса/реестра — «симуляция» вместо N живых
    /// установок на каждый сценарий.
    /// </summary>
    public static class InstallOutcomeEvaluator
    {
        public static InstallOutcome Evaluate(InstallOutcomeContext ctx)
        {
            // Честной проверки в принципе нет (нет надёжного ID) — не изобретаем
            // фиктивную, оставляем как раньше: результат по коду выхода, но явно
            // помеченный «не проверено», а не выдаваемый за подтверждённый.
            if (!ctx.VerificationSupported)
                return InstallOutcome.NotYetDetermined;

            bool versionChanged = !VersionsEqual(ctx.VersionBefore, ctx.VersionAfter);

            if (ctx.FoundAfter)
            {
                // Найдено по факту — это подтверждение независимо от кода выхода
                // (в т.ч. честная коррекция, если код выхода был ошибкой, но
                // установщик на самом деле справился). Единственное исключение —
                // приложение уже стояло той же версии до начала: реального
                // изменения не произошло, это не «только что установили».
                return (ctx.WasInstalledBefore && !versionChanged)
                    ? InstallOutcome.AlreadyUpToDate
                    : InstallOutcome.ConfirmedSuccess;
            }

            // Не найдено по факту (после ретраев с backoff в вызывающем коде).
            if (!ctx.ExitCodeSuccess)
                return InstallOutcome.ConfirmedFailure;

            // Код выхода успешный, но по факту не нашли: инсталлятор либо тихо не
            // сработал, либо (для 3010/ERROR_SUCCESS_REBOOT_REQUIRED) часть
            // инсталляторов дописывают реестр только после перезагрузки — в обоих
            // случаях недостаточно оснований для полноценного ConfirmedSuccess.
            return InstallOutcome.Unconfirmed;
        }

        // Версии сравниваются на равенство, а не на «больше/меньше»: строки версий
        // в каталоге и в выводе winget list очень разнородны (не всегда semver,
        // встречаются даты, билд-хэши и т.п.) — надёжно определить возрастание
        // нельзя, а вот «совпадает» или «отличается» — можно и достаточно для
        // различения AlreadyUpToDate от ConfirmedSuccess.
        private static bool VersionsEqual(string? a, string? b)
        {
            string na = (a ?? string.Empty).Trim();
            string nb = (b ?? string.Empty).Trim();
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
    }
}
