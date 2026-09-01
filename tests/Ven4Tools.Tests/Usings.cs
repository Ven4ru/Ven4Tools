global using Xunit;

// Тесты правят статику уровня процесса: ProfileService.Current, семафоры
// InstallationService/WindowsUpdateService, AppSettings. Разбиение на коллекции
// ("ProfileService", "InstallSemaphore") сериализует только классы ВНУТРИ одной
// коллекции, а сами коллекции xUnit по умолчанию выполняет параллельно — и
// CheckOnceAsync_ModeNotifyAndDownload_DownloadsFoundUpdates примерно раз на шесть
// прогонов падал, видя WindowsUpdateService.IsWindowsUpdateBusy = true из соседней
// коллекции, где скачивание нарочно удерживается открытым. Полный прогон занимает
// доли секунды, поэтому параллелизм коллекций выключен ради воспроизводимости.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
