namespace Ven4Tools.Models
{
    /// <summary>
    /// Размер-заглушка для случаев, когда реальный размер установщика получить не
    /// удалось (winget не сообщил размер, у HEAD/GET нет Content-Length, либо строка
    /// размера не распарсилась). Значение чисто индикативное для UI — точным быть не
    /// обязано. Раньше было продублировано как литерал 100 в четырёх независимых
    /// местах (AvailabilityChecker/CatalogViewModel.Catalog.cs/CatalogViewModel.Disks.cs).
    /// </summary>
    public static class InstallSizeDefaults
    {
        public const long UnknownSizeMB = 100;
    }
}
