using System;
using System.IO;

namespace Ven4Tools.Updater
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                File.WriteAllText(@"C:\Users\Ven4\updater_test.txt", 
                    $"Updater started at {DateTime.Now}\nArgs: {string.Join(", ", args)}");
                
                // Если есть аргумент с установщиком, запускаем его
                if (args.Length > 0 && File.Exists(args[0]))
                {
                    File.AppendAllText(@"C:\Users\Ven4\updater_test.txt", 
                        $"\nInstaller found: {args[0]}");
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(@"C:\Users\Ven4\updater_error.txt", ex.ToString());
            }
        }
    }
}