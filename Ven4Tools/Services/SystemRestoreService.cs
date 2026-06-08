using System.Management;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Создание точек восстановления Windows через WMI (класс SystemRestore).
    /// Используется перед массовыми операциями (установка/обновление нескольких
    /// приложений), чтобы можно было откатить систему при сбое.
    /// </summary>
    public static class SystemRestoreService
    {
        // Тип точки восстановления: установка приложения
        private const int APPLICATION_INSTALL = 12;
        // Тип события: начало системного изменения
        private const int BEGIN_SYSTEM_CHANGE = 100;

        /// <summary>
        /// Создаёт точку восстановления Windows.
        /// Возвращает true при успехе. Если WMI недоступен, нет прав администратора
        /// или восстановление системы отключено — возвращает false и не выбрасывает
        /// исключение (вызывающий код решает, как реагировать).
        /// </summary>
        public static Task<bool> CreateRestorePointAsync(string description)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var mc = new ManagementClass(
                        @"\\localhost\root\default", "SystemRestore", new ObjectGetOptions());

                    var inParams = mc.GetMethodParameters("CreateRestorePoint");
                    inParams["Description"]      = description;
                    inParams["RestorePointType"] = APPLICATION_INSTALL;
                    inParams["EventType"]        = BEGIN_SYSTEM_CHANGE;

                    var outParams = mc.InvokeMethod("CreateRestorePoint", inParams, null);

                    // ReturnValue == 0 означает успех
                    if (outParams?["ReturnValue"] is uint code)
                        return code == 0;

                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }
    }
}
