using System.IO;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Источник ответил и файл скачался полностью, но его SHA256 не совпал с
/// ожидаемым из подписанного манифеста. Отдельный тип (а не голый
/// <see cref="IOException"/>) нужен, чтобы отличать это от «источник
/// недоступен»: для пользователя и для журнала это разные события —
/// недоступность лечится ожиданием и сменой источника, а несовпадение
/// контрольной суммы означает, что по этой ссылке лежит не тот файл
/// (устаревший/пересобранный ассет либо подмена).
///
/// Наследуется от <see cref="IOException"/> — прежние обработчики загрузки,
/// ловящие IOException, продолжают работать без изменений.
/// </summary>
internal sealed class IntegrityCheckFailedException : IOException
{
    public IntegrityCheckFailedException(string message) : base(message) { }
}
