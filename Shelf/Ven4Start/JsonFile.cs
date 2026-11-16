using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace System.Runtime.CompilerServices
{
    /// <summary>Нужен компилятору для record и init в .NET Framework 4.8.</summary>
    internal static class IsExternalInit { }
}

namespace Ven4Start
{
    /// <summary>
    /// Чтение и запись маленьких JSON-файлов меню (настройки, закреплённые, кэш программ) средствами
    /// .NET Framework (DataContractJsonSerializer) — без внешних библиотек, программа остаётся одним exe.
    /// </summary>
    internal static class JsonFile
    {
        public static T? Read<T>(string path) where T : class
        {
            using var fs = File.OpenRead(path);
            return new DataContractJsonSerializer(typeof(T)).ReadObject(fs) as T;
        }

        /// <summary>Атомарная запись: во временный файл рядом, затем замена — оборванная запись не портит прежний файл.</summary>
        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), ownsStream: false))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(w, value);
                w.Flush();
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
