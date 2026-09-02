using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Всё, что проверке целостности нужно от внешнего мира: диск, сеть и ACL.
/// Вынесено в интерфейс не ради абстракции как таковой, а потому что решающая
/// часть <see cref="ClientIntegrityChecker"/> — это выбор вердикта по нескольким
/// входам, и покрыть её тестами можно только отвязав от реального CDN и реальной
/// папки клиента (тот же приём, что у TransactionalDirectoryInstaller).
/// </summary>
internal interface IClientIntegrityEnvironment
{
    bool ClientExecutableExists(string clientPath);

    Task<ClientFileManifest> BuildLocalManifestAsync(
        string clientPath, string versionLabel, CancellationToken cancellationToken);

    Task<ClientFileManifest?> FetchRemoteManifestAsync(
        string manifestUrl, string signatureUrl, CancellationToken cancellationToken);

    bool IsAclCompromised(string clientPath);
}

/// <summary>
/// Штатная реализация: те же сервисы, что использует блочное (дельта-) обновление.
/// </summary>
internal sealed class ClientIntegrityEnvironment : IClientIntegrityEnvironment
{
    private readonly HttpClient _httpClient;

    public ClientIntegrityEnvironment(HttpClient httpClient) => _httpClient = httpClient;

    public bool ClientExecutableExists(string clientPath) =>
        File.Exists(Path.Combine(clientPath, LauncherPaths.ClientExeName));

    public Task<ClientFileManifest> BuildLocalManifestAsync(
        string clientPath, string versionLabel, CancellationToken cancellationToken) =>
        ClientManifestBuilder.BuildFromDirectoryAsync(clientPath, versionLabel, cancellationToken);

    public async Task<ClientFileManifest?> FetchRemoteManifestAsync(
        string manifestUrl, string signatureUrl, CancellationToken cancellationToken)
    {
        var fetched = await ClientManifestFetcher
            .FetchAsync(_httpClient, manifestUrl, signatureUrl, cancellationToken)
            .ConfigureAwait(false);
        return fetched?.Manifest;
    }

    public bool IsAclCompromised(string clientPath)
    {
        // Результат ACL-проверки кэшируется на весь сеанс — это оправданно для
        // резолвинга winget/choco (горячий путь), но не для диагностики, которую
        // пользователь запускает РУЧНУЮ и, скорее всего, повторно после того, как
        // права поправил. Сбрасываем запись, чтобы кнопка показывала состояние
        // диска сейчас, а не на момент первого обращения.
        TrustedExecutablePaths.InvalidateAclCache(clientPath);
        return TrustedExecutablePaths.IsDirectoryAclCompromised(clientPath);
    }
}

/// <summary>
/// Применение найденной починки к папке клиента. Отделено от
/// <see cref="ClientIntegrityChecker"/>, потому что это единственная его часть,
/// которая реально пишет в папку клиента: сама починка обязана пройти те же
/// предустановочные проверки и ту же транзакцию, что и обычное обновление, а всё
/// это живёт в MainWindow рядом с окном (диалоги «закройте клиент», прогресс,
/// журнал). Так решающая логика остаётся тестируемой, а рискованная — ровно одна
/// и переиспользованная, а не написанная второй раз.
/// </summary>
internal interface IClientRepairExecutor
{
    Task<bool> ApplyAsync(
        ClientFileManifest remoteManifest,
        ClientDeltaPlan plan,
        ClientIntegritySources sources,
        string clientPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// «Проверить и восстановить клиент»: сверяет РЕАЛЬНО лежащие на диске файлы
/// публикации с подписанным файловым манифестом версии и, если расхождения
/// починимы пофайлово, докачивает недостающее.
///
/// Своего алгоритма сравнения здесь нет — используется тот же
/// <see cref="ClientDeltaPlanner"/>, что и у блочного обновления. Разница ровно в
/// одном входе: обновление сравнивает манифест НОВОЙ версии с кэшем «что мы
/// установили» (<see cref="InstalledManifestStore"/>), а проверка — манифест
/// УСТАНОВЛЕННОЙ версии с пересчитанными хешами файлов на диске. Поэтому она
/// находит именно повреждение файлов (антивирус выкусил dll, оборвалась запись,
/// кто-то подменил exe), а не отставание от релиза.
///
/// Класс ничего не чинит «на всякий случай»: ослабленная ACL только сообщается,
/// а слишком большие расхождения отправляют пользователя на обычную полную
/// переустановку вместо самодеятельности (см. ClientIntegrityStatus).
/// </summary>
internal sealed class ClientIntegrityChecker
{
    private readonly IClientIntegrityEnvironment _environment;
    private readonly IClientRepairExecutor? _repairExecutor;

    public ClientIntegrityChecker(HttpClient httpClient, IClientRepairExecutor repairExecutor)
        : this(new ClientIntegrityEnvironment(httpClient), repairExecutor)
    {
    }

    internal ClientIntegrityChecker(
        IClientIntegrityEnvironment environment, IClientRepairExecutor? repairExecutor)
    {
        _environment = environment;
        _repairExecutor = repairExecutor;
    }

    /// <summary>
    /// Проверка. Исключений наружу не бросает (кроме отмены пользователем): любая
    /// неудача — это вердикт отчёта, а не падение диагностического экрана.
    /// </summary>
    public async Task<ClientIntegrityReport> CheckAsync(
        string clientPath,
        string installedVersionLabel,
        ClientIntegritySources sources,
        CancellationToken cancellationToken)
    {
        // 1. Клиента нет — ни сети, ни хеширования, ни разговоров про ACL:
        //    у ненайденной установки нет ни целостности, ни прав доступа.
        if (!_environment.ClientExecutableExists(clientPath))
        {
            return ClientIntegrityReport.NotInstalled();
        }

        // 2. ACL — независимая проверка, и она обязана отработать даже если ниже
        //    всё сорвётся: недоступный CDN не повод молчать про ослабленные права.
        bool aclCompromised = _environment.IsAclCompromised(clientPath);

        // 3. Реальный состав папки клиента: хеш каждого файла, посчитанный сейчас.
        //    Это и есть то, чего не делает обычное обновление — оно верит кэшу.
        ClientFileManifest local;
        try
        {
            local = await _environment
                .BuildLocalManifestAsync(clientPath, installedVersionLabel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClientIntegrityReport.CheckFailed(
                $"не удалось прочитать файлы клиента ({ex.Message})", aclCompromised);
        }

        // 4. Эталон. У релизов без опубликованного файлового манифеста полей нет —
        //    это нормальный случай, а не ошибка.
        if (!sources.CanVerify)
        {
            return ClientIntegrityReport.ManifestUnavailable(
                $"для установленной версии {installedVersionLabel} не опубликован файловый манифест",
                aclCompromised);
        }

        ClientFileManifest? remote;
        try
        {
            remote = await _environment
                .FetchRemoteManifestAsync(sources.ManifestUrl!, sources.ManifestSignatureUrl!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClientIntegrityReport.ManifestUnavailable(
                $"манифест не получен ({ex.Message})", aclCompromised);
        }

        if (remote == null)
        {
            // FetchAsync fail-closed: null — это и «сеть недоступна», и «подпись не
            // сошлась». Различать их в тексте для пользователя нельзя, не солгав:
            // на этом уровне разницы уже не видно.
            return ClientIntegrityReport.ManifestUnavailable(
                "манифест не скачался или его подпись не подтверждена", aclCompromised);
        }

        // 5. Манифест обязан описывать именно установленную версию. Иначе сравнение
        //    покажет отличия между двумя разными релизами и объявит целый клиент
        //    «повреждённым» — худший из возможных ложных диагнозов.
        //
        //    Сравнение через VersionComparer, а не строкой (как в TryDeltaUpdateAsync):
        //    там обе версии приходят из одного version.json, а здесь установленная
        //    берётся из метаданных exe и имеет вид «5.0.0.0» против «5.0.0» в манифесте.
        if (VersionComparer.Compare(remote.Version, installedVersionLabel) != 0)
        {
            return ClientIntegrityReport.ManifestUnavailable(
                $"на сервере манифест версии {remote.Version}, а установлена {installedVersionLabel}",
                aclCompromised);
        }

        // 6. Тот же планировщик, что и у блочного обновления — второй реализации
        //    сравнения в проекте быть не должно.
        var plan = ClientDeltaPlanner.Plan(remote, local);
        return ClientIntegrityReport.FromPlan(plan, aclCompromised, remote, sources);
    }

    /// <summary>
    /// Применяет найденную починку. Возвращает false, если чинить нечего или нельзя;
    /// причина в любом случае записывается в <see cref="ClientIntegrityReport.RepairMessage"/>.
    /// </summary>
    public async Task<bool> RepairAsync(
        ClientIntegrityReport report, string clientPath, CancellationToken cancellationToken)
    {
        if (_repairExecutor == null)
        {
            report.SetRepairMessage("починка недоступна в этом режиме");
            return false;
        }

        if (report.Plan == null || report.RemoteManifest == null || report.Sources == null)
        {
            report.SetRepairMessage("проверка не состоялась — чинить не по чему");
            return false;
        }

        if (report.Plan.FullDownloadRecommended)
        {
            // Сознательно НЕ запускаем полную переустановку отсюда: у пользователя
            // для этого есть обычный путь обновления, а вторая точка входа в
            // установку клиента — это вторая точка, где она может пойти не так.
            report.SetRepairMessage(
                "расхождений слишком много для пофайловой починки — переустановите клиент обычным обновлением");
            return false;
        }

        if (!report.HasRepairableFindings)
        {
            report.SetRepairMessage("расхождений не найдено — чинить нечего");
            return false;
        }

        if (!report.Sources.CanRepair)
        {
            // Проверке хватало манифеста, починке нужен ещё и адрес отдельных файлов.
            report.SetRepairMessage(
                "неизвестен адрес отдельных файлов этой версии — починка невозможна");
            return false;
        }

        bool applied = await _repairExecutor
            .ApplyAsync(report.RemoteManifest, report.Plan, report.Sources, clientPath, cancellationToken)
            .ConfigureAwait(false);

        if (!applied && report.RepairMessage == null)
        {
            report.SetRepairMessage("починка не удалась — подробности в журнале лаунчера");
        }

        return applied;
    }
}
