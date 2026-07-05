using Microsoft.VisualStudio.TestTools.UnitTesting;

// Тесты этой сборки запускают реальный Ven4Tools.exe и делят один и тот же
// %LOCALAPPDATA%\Ven4Tools (profile.json, source_order.json, дисковый кэш
// каталога) между классами — параллельный запуск классов вызывает гонки
// (один тест перезаписывает настройки другого прямо во время его выполнения).
[assembly: DoNotParallelize]
