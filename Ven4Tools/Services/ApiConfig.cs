namespace Ven4Tools.Services
{
    /// <summary>
    /// Единая конфигурация адресов серверного API.
    /// Раньше разные сервисы использовали несовместимые базовые URL
    /// (www.ven4tools.ru и ven4tools.ru), что приводило к разным cookie/сессиям.
    /// </summary>
    internal static class ApiConfig
    {
        // Базовый адрес сайта без завершающего слэша
        internal const string BaseUrl = "https://ven4tools.ru";

        // Точка входа PHP-API
        internal const string DbApi = BaseUrl + "/api/db.php";
    }
}
