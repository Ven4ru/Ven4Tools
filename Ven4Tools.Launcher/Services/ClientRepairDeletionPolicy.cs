using System;
using System.Collections.Generic;
using System.IO;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Какие «лишние» файлы (есть в папке клиента, нет в подписанном манифесте) починка
/// вправе удалить. Раньше удалялось всё, чего нет в манифесте, — а папку клиента
/// можно привязать вручную («Найти клиент на диске»), например D:\Tools, где рядом
/// лежат другие программы и документы, и «Исправить» стёр бы их.
///
/// Удаляется только то, что похоже на файл публикации клиента:
/// • расширение из состава публикации (<see cref="PublicationExtensions"/> — сверено с
///   client-manifest и установленной папкой 7.x: dll, exe, json, файлы оформления
///   Personalization — wav/cur/ani/jpg/scr);
/// • файл в корне или в подпапке, которую <see cref="ClientFolderInspector"/> считает
///   своей (языковые папки .NET, Data, lang, runtimes, Personalization…). Всё в
///   чужих подпапках остаётся целиком;
/// • исполняемые файлы (.exe, .scr) в корне — только свои по
///   <see cref="ClientFolderInspector.IsOwnFileName"/>. Чужой exe рядом с клиентом —
///   главный признак папки с другими программами (portable-программа в D:\Tools), а
///   вреда клиенту от лишнего exe нет: его никто не запускает. Лишняя dll в корне,
///   напротив, удаляется: её подгрузил бы сам Ven4Tools.exe (подмена библиотеки).
///
/// Остальное остаётся на месте и попадает в список «не трогаю» для журнала.
/// Чистая функция над путями — покрыта unit-тестами.
/// </summary>
internal static class ClientRepairDeletionPolicy
{
    internal static readonly IReadOnlySet<string> PublicationExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".json", ".pdb", ".xml", ".pri", ".winmd", ".config", ".sig",
            ".ico", ".png", ".jpg", ".jpeg", ".bmp", ".gif",
            ".cur", ".ani", ".wav", ".scr", ".ttf", ".otf",
        };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr",
    };

    /// <summary>
    /// Делит лишние файлы (относительные пути через «/» из манифеста) на удаляемые и
    /// оставляемые. <paramref name="inspector"/> — проверка своих файлов корня по
    /// deps.json папки клиента.
    /// </summary>
    internal static (IReadOnlyList<string> Delete, IReadOnlyList<string> Keep) Split(
        IEnumerable<string> extras, ClientFolderInspector inspector)
    {
        var delete = new List<string>();
        var keep = new List<string>();
        foreach (string path in extras)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            (IsDeletable(path, inspector) ? delete : keep).Add(path);
        }
        return (delete, keep);
    }

    internal static bool IsDeletable(string relativePath, ClientFolderInspector inspector)
    {
        string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        string name = parts[^1];
        string extension = Path.GetExtension(name);
        if (!PublicationExtensions.Contains(extension)) return false;

        if (parts.Length > 1)
            return ClientFolderInspector.IsOwnDirectoryName(parts[0]);

        return !ExecutableExtensions.Contains(extension) || inspector.IsOwnFileName(name);
    }
}
