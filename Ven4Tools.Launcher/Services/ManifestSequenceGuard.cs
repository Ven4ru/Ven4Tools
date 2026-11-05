using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Защита от повтора старого подписанного version.json. Подпись подтверждает, что
/// манифест выпущен владельцем, но не то, что он свежий: старая копия подписана так
/// же правильно, и её повтор (подменённый CDN, перехват) откатил бы флаги поведения
/// и cdn_ip и снял бы отзыв архивов. Поле manifest_seq растёт с каждой подписью;
/// здесь хранится наибольшее принятое значение, и манифест с меньшим отвергается.
///
/// Совместимость: пока лаунчер не видел ни одного номера, манифест без поля
/// принимается, как раньше. После первого номера манифест без поля — это повтор
/// манифеста, выпущенного до появления номеров, и он отвергается так же.
///
/// Значение хранится в данных лаунчера
/// (%LOCALAPPDATA%\Ven4Tools\Launcher\version-manifest-seq.txt). Отсутствующий или
/// битый файл — «номеров ещё не было» (поведение как до защиты), а не отказ: иначе
/// порча файла оставила бы лаунчер без манифеста CDN навсегда.
/// </summary>
internal sealed class ManifestSequenceGuard
{
    public const string FileName = "version-manifest-seq.txt";

    private static readonly Lazy<ManifestSequenceGuard> _default = new(() => new ManifestSequenceGuard(DefaultPath));

    /// <summary>Штатный экземпляр (один на процесс).</summary>
    public static ManifestSequenceGuard Default => _default.Value;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ven4Tools", "Launcher", FileName);

    private readonly string _path;
    private readonly object _lock = new();
    private bool _loaded;
    private long? _highest;

    internal ManifestSequenceGuard(string path) => _path = path;

    /// <summary>Наибольший принятый номер (null — номеров ещё не было).</summary>
    public long? Highest
    {
        get { lock (_lock) { EnsureLoaded(); return _highest; } }
    }

    /// <summary>
    /// Принять ли манифест с номером <paramref name="seq"/> (подпись уже подтверждена).
    /// true — принять; при большем номере он запоминается. false — манифест старее уже
    /// принятого, <paramref name="reason"/> объясняет почему.
    /// </summary>
    public bool TryAccept(long? seq, out string? reason)
    {
        lock (_lock)
        {
            EnsureLoaded();
            reason = null;

            if (seq == null)
            {
                if (_highest == null) return true;
                reason = $"в манифесте нет номера выпуска (manifest_seq), а уже принят номер {_highest} — это повтор старого манифеста";
                return false;
            }

            if (_highest != null && seq.Value < _highest.Value)
            {
                reason = $"номер выпуска манифеста {seq.Value} меньше уже принятого {_highest.Value} — это повтор старого манифеста";
                return false;
            }

            if (_highest == null || seq.Value > _highest.Value)
            {
                _highest = seq.Value;
                try
                {
                    Helpers.FileHelper.WriteAllTextAtomic(_path, seq.Value.ToString(CultureInfo.InvariantCulture));
                }
                catch
                {
                    // Не записалось — номер всё равно действует до конца сеанса; потеря
                    // файла лишь возвращает поведение «как до защиты».
                }
            }
            return true;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            string text = File.ReadAllText(_path, Encoding.UTF8).Trim();
            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                _highest = value;
        }
        catch
        {
            // Нечитаемый файл — как его отсутствие.
        }
    }
}
