using System;
using System.Collections.Generic;

namespace Ven4Tools.Models
{
    /// <summary>
    /// Локальный снапшот конфигурации приложения («было — стало» на этой машине).
    /// Собственный автономный формат: не связан с Preset/ProfileService и не предназначен
    /// для переноса на другой ПК — только для отката состояния на текущей машине.
    /// </summary>
    public class ConfigSnapshot
    {
        /// <summary>Версия формата — для безопасной эволюции схемы.</summary>
        public int FormatVersion { get; set; } = 1;

        /// <summary>Имя снапшота, заданное пользователем.</summary>
        public string Name { get; set; } = "";

        /// <summary>Момент создания снапшота (локальное время).</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Идентификаторы отмеченных твиков Debloater (apps/privacy/services).</summary>
        public List<string> DebloatTweakIds { get; set; } = new();

        /// <summary>Копия локальных пресетов каталога на момент снапшота.</summary>
        public List<ConfigSnapshotPreset> Presets { get; set; } = new();
    }

    /// <summary>
    /// Копия пресета внутри снапшота. Дублирует поля Preset сознательно:
    /// формат снапшота должен оставаться независимым от изменений модели Preset.
    /// </summary>
    public class ConfigSnapshotPreset
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Apps { get; set; } = new();
    }

    /// <summary>Краткая информация о файле снапшота для списка в UI.</summary>
    public class ConfigSnapshotInfo
    {
        public string FilePath { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int TweakCount { get; set; }
        public int PresetCount { get; set; }

        /// <summary>Строка для отображения в списке снапшотов.</summary>
        public string DisplayLabel =>
            $"{Name} — {CreatedAt:dd.MM.yyyy HH:mm} · твиков: {TweakCount}, пресетов: {PresetCount}";
    }
}
