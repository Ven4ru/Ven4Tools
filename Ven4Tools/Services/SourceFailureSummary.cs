using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Причина неудачи цепочки источников для журнала неудач, блока «Не установлено»
    /// и отчёта лаунчера: что ответил каждый источник, а не только последний.
    /// <para>Раньше в запись попадала одна «последняя причина». Отчёт о Lunacy выглядел
    /// как «choco завершился с кодом 404», хотя до Chocolatey не справился winget, а
    /// прямая ссылка была пропущена — ни того, ни другого в отчёте не было.</para>
    /// <para>Строка всегда одного вида, с тремя источниками в одном и том же порядке:
    /// у неё один шаблон перевода, а по отчёту видно и то, что до источника дело
    /// не дошло вовсе.</para>
    /// </summary>
    public sealed class SourceFailureSummary
    {
        /// <summary>Источника нет в порядке установки — до него дело не дошло.</summary>
        public const string NotTried = "не пробовался";

        /// <summary>Источник пробовался, но причины для показа нет — она записана в лог.</summary>
        public const string SeeLog = "не сработал, подробности — в логе установки";

        private string _winget = NotTried;
        private string _choco = NotTried;
        private string _direct = NotTried;

        /// <summary>
        /// Запоминает итог источника <paramref name="sourceId"/>
        /// (<see cref="SourceOrderSettings"/>). Пустая причина означает, что источник
        /// пробовался, но расшифровки у него нет. Неизвестный источник пропускается.
        /// </summary>
        public void Record(string? sourceId, string? failureDetail)
        {
            string detail = string.IsNullOrWhiteSpace(failureDetail) ? SeeLog : failureDetail.Trim();
            switch (sourceId)
            {
                case SourceOrderSettings.Winget: _winget = detail; break;
                case SourceOrderSettings.Choco:  _choco  = detail; break;
                case SourceOrderSettings.Direct: _direct = detail; break;
            }
        }

        public string Build() =>
            $"все источники исчерпаны — Winget: {_winget} · Chocolatey: {_choco} · Прямая ссылка: {_direct}";
    }
}
