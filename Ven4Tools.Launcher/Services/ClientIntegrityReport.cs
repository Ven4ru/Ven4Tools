using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Исход проверки целостности установленного клиента. Разделение «проблема клиента»
/// и «нам не с чем сравнить» здесь принципиально: отсутствие эталона нельзя показывать
/// пользователю как поломку его установки.
/// </summary>
internal enum ClientIntegrityStatus
{
    /// <summary>Клиента нет на диске — проверять нечего.</summary>
    NotInstalled,

    /// <summary>
    /// Сверять не с чем: файловый манифест не опубликован для этой версии, не
    /// скачался, не прошёл проверку подписи или описывает другую версию. Это НЕ
    /// найденная проблема клиента, а отсутствие эталона — формулировки для
    /// пользователя обязаны это различать.
    /// </summary>
    ManifestUnavailable,

    /// <summary>
    /// Не удалось посчитать реальный состав папки клиента (ошибка чтения диска).
    /// Тоже не вердикт о клиенте: проверка просто не состоялась.
    /// </summary>
    CheckFailed,

    /// <summary>Все файлы совпали с подписанным манифестом — расхождений нет.</summary>
    Healthy,

    /// <summary>Расхождения найдены и починимы пофайлово.</summary>
    RepairAvailable,

    /// <summary>
    /// Расхождений столько, что пофайловая починка бессмысленна (см. порог
    /// <see cref="ClientDeltaPlanner.MinimumUnchangedShare"/>) — нужна обычная полная
    /// переустановка. Кнопка «Исправить» в этом случае не предлагается: качать почти
    /// всю публикацию пофайлово медленнее и хрупче, чем один архив штатным путём.
    /// </summary>
    FullReinstallRecommended,

    /// <summary>
    /// Исполняемый файл клиента физически есть, но у него не читаются сведения о
    /// версии (повреждён PE — антивирус выкусил кусок, обрыв записи и т.п.). Это
    /// гарантированно локальная поломка, а не «эталон недоступен»: у настоящего
    /// собранного проектом exe версия есть всегда. Не должно превращаться в
    /// ManifestUnavailable — та формулировка звучит как проблема сервера/сети,
    /// хотя проблема здесь целиком на диске пользователя.
    /// </summary>
    ExecutableCorrupted,
}

/// <summary>
/// Адреса, по которым проверка берёт эталон и, при починке, отдельные файлы
/// публикации. Ровно те же поля <see cref="ClientVersionInfo"/>, что использует
/// блочное (дельта-) обновление — передаются параметром, а не берутся из
/// константы: они приходят из подписанного version.json и различаются от версии
/// к версии, а у релизов без файлового манифеста отсутствуют вовсе.
/// </summary>
internal sealed class ClientIntegritySources
{
    public string? ManifestUrl { get; init; }
    public string? ManifestSignatureUrl { get; init; }
    public string? FilesBaseUrl { get; init; }
    public string? FilesBaseMirrorHostingUrl { get; init; }

    /// <summary>Манифест и его подпись известны — проверку есть с чем сверять.</summary>
    public bool CanVerify =>
        !string.IsNullOrWhiteSpace(ManifestUrl) &&
        !string.IsNullOrWhiteSpace(ManifestSignatureUrl);

    /// <summary>
    /// Известно ещё и где лежат отдельные файлы — только тогда возможна починка.
    /// Проверка без этого поля работает (сказать «файл повреждён» можно и не умея
    /// его скачать), а починка — нет.
    /// </summary>
    public bool CanRepair => CanVerify && !string.IsNullOrWhiteSpace(FilesBaseUrl);
}

/// <summary>
/// Результат одного запуска проверки «Проверить и восстановить клиент».
///
/// Отчёт самодостаточен: он несёт и вердикт для пользователя, и всё, что нужно
/// для последующей починки (сам подписанный манифест и адреса файлов). Иначе
/// между проверкой и нажатием «Исправить» пришлось бы качать манифест повторно —
/// и чинить по эталону, которого проверка не видела.
/// </summary>
internal sealed class ClientIntegrityReport
{
    private ClientIntegrityReport(
        ClientIntegrityStatus status,
        bool isClientInstalled,
        bool manifestAvailable,
        ClientDeltaPlan? plan,
        bool aclCompromised,
        string summary,
        ClientFileManifest? remoteManifest,
        ClientIntegritySources? sources)
    {
        Status = status;
        IsClientInstalled = isClientInstalled;
        ManifestAvailable = manifestAvailable;
        Plan = plan;
        AclCompromised = aclCompromised;
        Summary = summary;
        RemoteManifest = remoteManifest;
        Sources = sources;
    }

    public ClientIntegrityStatus Status { get; }

    /// <summary>Исполняемый файл клиента найден в папке установки.</summary>
    public bool IsClientInstalled { get; }

    /// <summary>Подписанный манифест этой версии получен и проверен.</summary>
    public bool ManifestAvailable { get; }

    /// <summary>
    /// План расхождений (что скачать, что удалить, что совпало) — null, если
    /// сравнение не состоялось. UI показывает по нему списки путей.
    /// </summary>
    public ClientDeltaPlan? Plan { get; }

    /// <summary>
    /// ACL папки клиента ослаблена. Считается независимо от манифеста и заполняется
    /// даже тогда, когда сравнение не состоялось: это отдельная проблема, и молчать
    /// о ней из-за недоступного CDN нельзя.
    /// </summary>
    public bool AclCompromised { get; }

    /// <summary>Одна строка для журнала лаунчера.</summary>
    public string Summary { get; }

    /// <summary>Эталон, по которому строился план — нужен починке.</summary>
    internal ClientFileManifest? RemoteManifest { get; }

    /// <summary>Адреса, с которыми проверка запускалась — нужны починке.</summary>
    internal ClientIntegritySources? Sources { get; }

    /// <summary>
    /// Почему починка не состоялась (или чем закончилась). Заполняется
    /// <see cref="ClientIntegrityChecker.RepairAsync"/>: у него нет иного способа
    /// объяснить false, кроме как через отчёт.
    /// </summary>
    public string? RepairMessage { get; private set; }

    internal void SetRepairMessage(string message) => RepairMessage = message;

    /// <summary>
    /// Есть ли что чинить пофайлово. Отдельное свойство, а не проверка Status в UI:
    /// условие «план есть, он не «качать всё», и в нём непусто» повторялось бы
    /// в каждой ветке отображения и в самой починке.
    /// </summary>
    public bool HasRepairableFindings =>
        Plan is { FullDownloadRecommended: false } plan &&
        (plan.ToDownload.Count > 0 || plan.ToDelete.Count > 0);

    internal static ClientIntegrityReport NotInstalled() =>
        new(ClientIntegrityStatus.NotInstalled, false, false, null, false,
            "клиент не установлен — проверять нечего", null, null);

    internal static ClientIntegrityReport CheckFailed(string reason, bool aclCompromised) =>
        new(ClientIntegrityStatus.CheckFailed, true, false, null, aclCompromised,
            $"проверка не состоялась: {reason}", null, null);

    internal static ClientIntegrityReport ManifestUnavailable(string reason, bool aclCompromised) =>
        new(ClientIntegrityStatus.ManifestUnavailable, true, false, null, aclCompromised,
            $"не с чем сверять: {reason}", null, null);

    internal static ClientIntegrityReport ExecutableCorrupted(bool aclCompromised) =>
        new(ClientIntegrityStatus.ExecutableCorrupted, true, false, null, aclCompromised,
            "исполняемый файл клиента повреждён — версия не читается, переустановите клиент полностью",
            null, null);

    internal static ClientIntegrityReport FromPlan(
        ClientDeltaPlan plan,
        bool aclCompromised,
        ClientFileManifest remoteManifest,
        ClientIntegritySources sources)
    {
        if (plan.FullDownloadRecommended)
        {
            return new(ClientIntegrityStatus.FullReinstallRecommended, true, true, plan, aclCompromised,
                $"расхождений слишком много ({plan.Reason}) — нужна полная переустановка",
                remoteManifest, sources);
        }

        bool clean = plan.ToDownload.Count == 0 && plan.ToDelete.Count == 0;
        var status = clean ? ClientIntegrityStatus.Healthy : ClientIntegrityStatus.RepairAvailable;
        string summary = clean
            ? $"целостность подтверждена, проверено файлов: {plan.Unchanged.Count}"
            : $"найдено расхождений: {plan.ToDownload.Count} повреждённых/отсутствующих, " +
              $"{plan.ToDelete.Count} лишних (совпало {plan.Unchanged.Count})";

        return new(status, true, true, plan, aclCompromised, summary, remoteManifest, sources);
    }
}
