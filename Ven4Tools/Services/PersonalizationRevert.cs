using System;
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Единый откат всей «Персонализации» — до состояния, каким оно было до Ven4Tools:
    /// «Свой Пуск», тема панели задач и ушки Венчика, настройки панели задач и Пуска,
    /// курсоры, обои, экран блокировки, звуки, заставка (и все автозапуски этих программ).
    ///
    /// Нужен там, где кнопок вкладки уже нет: деинсталлятор перед удалением данных зовёт
    /// лаунчер, а тот — клиент с ключом <see cref="Switch"/>; файлы «как было» лежат в
    /// данных, и после их удаления вернуть Windows было бы уже не по чему.
    /// Сбой одного шага не останавливает остальные: что не вернулось, попадает в отчёт
    /// и в журнал, код возврата — 1.
    /// </summary>
    public sealed class PersonalizationRevert
    {
        /// <summary>Ключ командной строки клиента и лаунчера: откатить всё и выйти без окон.</summary>
        public const string Switch = "--revert-personalization";

        /// <param name="Name">Название для журнала.</param>
        /// <param name="Revert">Откатить; true — после отката от раздела ничего не осталось.</param>
        public sealed record Step(string Name, Func<bool> Revert);

        public sealed class Result
        {
            public List<string> Reverted { get; } = new();
            public List<string> Failed { get; } = new();
            /// <summary>0 — всё возвращено, 1 — что-то не вернулось (подробности в журнале).</summary>
            public int ExitCode => Failed.Count == 0 ? 0 : 1;
        }

        private readonly IReadOnlyList<Step> _steps;

        public PersonalizationRevert(IEnumerable<Step> steps) => _steps = steps.ToList();

        public static PersonalizationRevert Default => new(DefaultSteps());

        /// <summary>
        /// Порядок важен: сначала программы, которые работают сами и держат автозапуск
        /// («Свой Пуск»; будущая своя панель Ven4Bar встаёт сюда же — сначала вернуть родную
        /// панель), затем тема с ушками (её «как было» снято раньше настроек панели), затем
        /// настройки панели задач и Пуска, затем остальные пункты «Персонализации».
        /// </summary>
        public static IReadOnlyList<Step> DefaultSteps()
        {
            var start = StartMenuService.Default;
            var theme = TaskbarThemeService.Default;
            var taskbar = TaskbarStartService.Default;
            var parts = PersonalizationService.Default;
            var steps = new List<Step>
            {
                new("Свой Пуск", () => { start.Revert(out _); return !start.HasUndo; }),
                new("Тема панели задач и ушки Венчика", () => { theme.RevertAll(out _); return !theme.HasUndo; }),
                new("Панель задач и Пуск", () => { var r = taskbar.RevertAll(); return r.Blocked.Count == 0 && !taskbar.HasUndo; }),
            };
            foreach (var part in Enum.GetValues<PersonalizationPart>())
                steps.Add(new("Персонализация: " + part, () => { parts.Revert(part); return !parts.IsApplied(part); }));
            return steps;
        }

        public Result RevertAll()
        {
            var result = new Result();
            AppLogger.Write("↩ Персонализация: откат всех изменений");
            foreach (var step in _steps)
            {
                try
                {
                    if (step.Revert()) result.Reverted.Add(step.Name);
                    else
                    {
                        result.Failed.Add(step.Name);
                        AppLogger.Write($"⚠️ Откат персонализации: «{Tr(step.Name)}» вернулся не полностью");
                    }
                }
                catch (Exception ex)
                {
                    result.Failed.Add(step.Name);
                    AppLogger.Write($"⚠️ Откат персонализации: «{Tr(step.Name)}» — {ex.Message}");
                }
            }
            AppLogger.Write(result.Failed.Count == 0
                ? "✓ Персонализация: всё возвращено, как было до Ven4Tools"
                : $"⚠️ Персонализация: не вернулось — {string.Join(", ", result.Failed.Select(Tr))}");
            return result;
        }
    }
}
