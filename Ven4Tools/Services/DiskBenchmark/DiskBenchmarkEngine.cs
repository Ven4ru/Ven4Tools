using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

namespace Ven4Tools.Services.DiskBenchmark
{
    /// <summary>
    /// Измеряет скорость накопителя через временный файл на выбранном томе.
    /// Прямой работы с устройством или секторами нет и быть не должно — только файл.
    ///
    /// Кэш файловой системы обходится флагом FILE_FLAG_NO_BUFFERING: без него замер чтения
    /// на повторных проходах измеряет оперативную память, а не накопитель. Нативный код для
    /// этого не нужен — рантайм разрешает соответствующее значение FileOptions, а требование
    /// выравнивания буфера закрывается закреплением обычного массива.
    /// </summary>
    public static class DiskBenchmarkEngine
    {
        /// <summary>FILE_FLAG_NO_BUFFERING — полный обход кэша файловой системы.</summary>
        private const FileOptions NoBuffering = (FileOptions)0x20000000;

        /// <summary>Кратно и 512-байтному, и 4096-байтному физическому сектору.</summary>
        private const int Alignment = 4096;

        /// <summary>Длительность одного прохода одного замера.</summary>
        private static readonly TimeSpan PassDuration = TimeSpan.FromSeconds(5);

        /// <summary>Размер блока при подготовке тестового файла.</summary>
        private const int PrepareBlockSize = 1024 * 1024;

        /// <summary>Сколько операций записи держится незавершёнными при подготовке файла.</summary>
        private const int PrepareStreams = 8;

        /// <summary>Доля шкалы прогресса, отведённая под подготовку файла.</summary>
        private const double PrepareProgressShare = 0.10;

        public const string TempFileName = "Ven4Tools_benchmark.tmp";

        /// <summary>Запас свободного места сверх размера тестового файла.</summary>
        public const long FreeSpaceReserveBytes = 1024L * 1024 * 1024;

        /// <summary>Доступные размеры тестового файла.</summary>
        public static readonly long[] FileSizes =
        {
            1024L * 1024 * 1024,
            2048L * 1024 * 1024,
            4096L * 1024 * 1024,
            8192L * 1024 * 1024
        };

        /// <summary>Паттерны нагрузки в порядке вывода в таблице результатов.</summary>
        public static readonly BenchmarkPattern[] Patterns =
        {
            new BenchmarkPattern { Name = "SEQ1M Q8T1",   BlockSize = 1024 * 1024, QueueDepth = 8,  ThreadCount = 1,  Sequential = true },
            new BenchmarkPattern { Name = "SEQ1M Q1T1",   BlockSize = 1024 * 1024, QueueDepth = 1,  ThreadCount = 1,  Sequential = true },
            new BenchmarkPattern { Name = "RND4K Q32T16", BlockSize = 4096,        QueueDepth = 32, ThreadCount = 16, Sequential = false },
            new BenchmarkPattern { Name = "RND4K Q1T1",   BlockSize = 4096,        QueueDepth = 1,  ThreadCount = 1,  Sequential = false }
        };

        public static int PassesForProfile(BenchmarkProfile profile) => profile switch
        {
            BenchmarkProfile.Fast => 1,
            BenchmarkProfile.Precise => 5,
            _ => 3
        };

        public static string DescribeProfile(BenchmarkProfile profile) => profile switch
        {
            BenchmarkProfile.Fast => "Быстрый",
            BenchmarkProfile.Precise => "Точный",
            _ => "Обычный"
        };

        /// <summary>Полный прогон. Отмена — штатный путь: возвращается частичный результат.</summary>
        public static async Task<BenchmarkRunResult> RunAsync(
            PhysicalDiskInfo disk,
            BenchmarkVolumeInfo volume,
            BenchmarkProfile profile,
            long fileSizeBytes,
            IProgress<BenchmarkProgress>? progress,
            CancellationToken ct)
        {
            int passes = PassesForProfile(profile);
            var result = new BenchmarkRunResult
            {
                Disk = disk,
                VolumeLetter = volume.Letter,
                Profile = profile,
                Passes = passes,
                FileSizeBytes = fileSizeBytes,
                StartedAt = DateTime.Now
            };

            string path = Path.Combine(volume.Letter + Path.DirectorySeparatorChar, TempFileName);
            var stopwatch = Stopwatch.StartNew();

            // Лучшее значение по проходам для каждой пары «паттерн + направление».
            var best = new Dictionary<string, BenchmarkMeasurement>();
            int totalMeasurements = passes * Patterns.Length * 2;
            int doneMeasurements = 0;

            try
            {
                await PrepareFileAsync(path, fileSizeBytes, progress, ct).ConfigureAwait(false);

                for (int pass = 1; pass <= passes; pass++)
                {
                    foreach (var pattern in Patterns)
                    {
                        foreach (var operation in new[] { BenchmarkOperation.Read, BenchmarkOperation.Write })
                        {
                            ct.ThrowIfCancellationRequested();

                            string direction = operation == BenchmarkOperation.Read ? "чтение" : "запись";
                            string stage = passes > 1
                                ? $"{pattern.Name} — {direction}, проход {pass} из {passes}"
                                : $"{pattern.Name} — {direction}";

                            progress?.Report(new BenchmarkProgress
                            {
                                Stage = stage,
                                Fraction = PrepareProgressShare +
                                           (1 - PrepareProgressShare) * doneMeasurements / totalMeasurements
                            });

                            var measurement = await MeasureAsync(path, pattern, operation, fileSizeBytes, ct)
                                .ConfigureAwait(false);

                            string key = pattern.Name + "/" + operation;
                            if (!best.TryGetValue(key, out var previous) ||
                                measurement.MegabytesPerSecond > previous.MegabytesPerSecond)
                            {
                                best[key] = measurement;
                            }

                            doneMeasurements++;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
            }
            finally
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                TryDeleteFile(path);
            }

            // Порядок в отчёте не зависит от порядка завершения замеров.
            foreach (var pattern in Patterns)
            {
                foreach (var operation in new[] { BenchmarkOperation.Read, BenchmarkOperation.Write })
                {
                    if (best.TryGetValue(pattern.Name + "/" + operation, out var measurement))
                        result.Measurements.Add(measurement);
                }
            }

            progress?.Report(new BenchmarkProgress
            {
                Stage = result.Cancelled ? "Тест остановлен" : "Готово",
                Fraction = 1
            });

            return result;
        }

        /// <summary>
        /// Создаёт тестовый файл и записывает его целиком псевдослучайными данными.
        /// Целиком — обязательно: чтение неинициализированных областей разреженного файла
        /// возвращается мгновенно и обесценивает замер. Псевдослучайные — тоже обязательно:
        /// накопители с аппаратным сжатием на однородных данных завышают результат.
        /// </summary>
        private static async Task PrepareFileAsync(
            string path, long fileSizeBytes, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new BenchmarkProgress { Stage = "Подготовка тестового файла", Fraction = 0 });

            using var buffer = new AlignedBuffer(PrepareStreams, PrepareBlockSize);
            buffer.FillWithPseudoRandomData();

            long totalBlocks = fileSizeBytes / PrepareBlockSize;

            using var handle = File.OpenHandle(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                NoBuffering | FileOptions.WriteThrough | FileOptions.Asynchronous, fileSizeBytes);

            RandomAccess.SetLength(handle, fileSizeBytes);

            var pending = new List<Task>(PrepareStreams);
            int lastReportedPercent = -1;

            for (long blockIndex = 0; blockIndex < totalBlocks; blockIndex += PrepareStreams)
            {
                ct.ThrowIfCancellationRequested();
                pending.Clear();

                for (int slot = 0; slot < PrepareStreams && blockIndex + slot < totalBlocks; slot++)
                {
                    long offset = (blockIndex + slot) * PrepareBlockSize;
                    pending.Add(RandomAccess.WriteAsync(handle, buffer.Slice(slot), offset, ct).AsTask());
                }

                await Task.WhenAll(pending).ConfigureAwait(false);

                // Отчёт не чаще, чем раз в процент: на файле в 8 ГиБ иначе набегает
                // тысяча уведомлений, и интерфейс тратит время на их обработку.
                int percent = (int)((blockIndex + PrepareStreams) * 100 / totalBlocks);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(new BenchmarkProgress
                    {
                        Stage = $"Подготовка тестового файла — {Math.Min(100, percent)}%",
                        Fraction = PrepareProgressShare * percent / 100
                    });
                }
            }
        }

        /// <summary>
        /// При глубине очереди 1 нагрузку создаёт обычный синхронный ввод-вывод.
        ///
        /// Это не упрощение, а требование точности. Асинхронный путь .NET добавляет к каждой
        /// операции фиксированные накладные расходы на доставку завершения через пул потоков.
        /// На глубине очереди 1 скорость равна единице, делённой на задержку, поэтому эти
        /// накладные расходы попадают в результат целиком: измерения показали 41 мкс на
        /// асинхронной записи против 21 мкс на синхронной — вдвое, то есть половина «замера
        /// диска» была замером самого приложения. На глубинах больше единицы операции идут
        /// внахлёст, накладные расходы прячутся за ожиданием устройства, и там асинхронный
        /// путь необходим — иначе очередь не набрать.
        /// </summary>
        public static bool UsesSynchronousIo(BenchmarkPattern pattern) => pattern.QueueDepth == 1;

        /// <summary>
        /// Один замер длительностью PassDuration.
        ///
        /// Нагрузку с глубиной очереди больше единицы создают Q*T независимых асинхронных
        /// цепочек: столько же незавершённых операций видит устройство, а именно это и
        /// описывает подпись паттерна. Вариант «T настоящих потоков операционной системы по Q
        /// операций в каждом» был реализован и измерен — в процессе с обычной (не серверной)
        /// сборкой мусора, а именно такая у клиента, он даёт впятеро худший результат: 357
        /// против 1819 МБ/с на RND4K Q32T16. При серверной сборке мусора обе модели дают
        /// одинаковый порядок величины (1576 против 1720), то есть на само устройство модель
        /// не влияет — влияет только на накладные расходы приложения. Поэтому здесь оставлена
        /// та, что не мешает измерять диск.
        /// </summary>
        private static async Task<BenchmarkMeasurement> MeasureAsync(
            string path, BenchmarkPattern pattern, BenchmarkOperation operation,
            long fileSizeBytes, CancellationToken ct)
        {
            int threadCount = Math.Max(1, pattern.ThreadCount);
            int queueDepth = Math.Max(1, pattern.QueueDepth);
            int streams = threadCount * queueDepth;
            int block = pattern.BlockSize;
            long totalBlocks = fileSizeBytes / block;
            bool synchronous = UsesSynchronousIo(pattern);
            bool reading = operation == BenchmarkOperation.Read;

            using var buffer = new AlignedBuffer(streams, block);
            if (!reading) buffer.FillWithPseudoRandomData();

            // Асинхронный дескриптор нужен только там, где действительно набирается очередь.
            // WriteThrough при замере не ставится: измерения показали, что на скорость он
            // почти не влияет, зато условия замера расходятся с CrystalDiskMark. Целостность
            // данных здесь обеспечивать не нужно — файл временный и удаляется следом.
            var options = NoBuffering;
            if (!synchronous) options |= FileOptions.Asynchronous;

            using var handle = File.OpenHandle(
                path, FileMode.Open, reading ? FileAccess.Read : FileAccess.Write,
                FileShare.None, options);

            var counters = new long[streams];
            long blocksPerStream = Math.Max(1, totalBlocks / streams);

            var stopwatch = Stopwatch.StartNew();
            long deadline = Stopwatch.GetTimestamp() + (long)(PassDuration.TotalSeconds * Stopwatch.Frequency);

            if (synchronous)
            {
                // Отдельный поток: синхронный ввод-вывод блокирует, и на вызывающем потоке
                // это заморозило бы интерфейс.
                await Task.Run(
                    () => Parallel.For(0, streams, RunSynchronousStream),
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var tasks = new Task[streams];
                for (int index = 0; index < streams; index++) tasks[index] = RunAsynchronousStreamAsync(index);
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            stopwatch.Stop();
            ct.ThrowIfCancellationRequested();

            long totalOperations = 0;
            foreach (long count in counters) totalOperations += count;

            double seconds = stopwatch.Elapsed.TotalSeconds;
            double operationsPerSecond = seconds > 0 && totalOperations > 0 ? totalOperations / seconds : 0;

            return new BenchmarkMeasurement
            {
                PatternName = pattern.Name,
                Operation = operation,
                // Мегабайты десятичные (1 000 000 байт) — так же считает CrystalDiskMark.
                MegabytesPerSecond = operationsPerSecond * block / 1_000_000d,
                OperationsPerSecond = operationsPerSecond,
                // Закон Литтла: средняя задержка равна суммарной глубине очереди, делённой
                // на пропускную способность в операциях в секунду.
                AverageLatencyMicroseconds = operationsPerSecond > 0
                    ? streams / operationsPerSecond * 1_000_000d
                    : 0
            };

            // Смещения: последовательные паттерны делят файл на равные участки по числу
            // потоков запросов, случайные выбирают выровненный блок генератором.
            long NextOffset(Random random, ref long cursor, long start)
            {
                long blockIndex;
                if (pattern.Sequential)
                {
                    blockIndex = cursor;
                    cursor++;
                    if (cursor >= start + blocksPerStream || cursor >= totalBlocks) cursor = start;
                }
                else
                {
                    blockIndex = random.NextInt64(totalBlocks);
                }
                return blockIndex * block;
            }

            void RunSynchronousStream(int index)
            {
                var memory = buffer.Slice(index);
                var random = new Random(unchecked(index * 397 + 1013));
                long start = pattern.Sequential ? index * blocksPerStream % totalBlocks : 0;
                long cursor = start;
                long count = 0;

                while (Stopwatch.GetTimestamp() < deadline && !ct.IsCancellationRequested)
                {
                    long offset = NextOffset(random, ref cursor, start);
                    if (reading)
                    {
                        if (RandomAccess.Read(handle, memory.Span, offset) <= 0) break;
                    }
                    else
                    {
                        RandomAccess.Write(handle, memory.Span, offset);
                    }
                    count++;
                }

                counters[index] = count;
            }

            async Task RunAsynchronousStreamAsync(int index)
            {
                var memory = buffer.Slice(index);
                var random = new Random(unchecked(index * 397 + 1013));
                long start = pattern.Sequential ? index * blocksPerStream % totalBlocks : 0;
                long cursor = start;
                long count = 0;

                while (Stopwatch.GetTimestamp() < deadline)
                {
                    ct.ThrowIfCancellationRequested();

                    long offset = NextOffset(random, ref cursor, start);
                    if (reading)
                    {
                        int read = await RandomAccess.ReadAsync(handle, memory, offset, ct).ConfigureAwait(false);
                        if (read <= 0) break;
                    }
                    else
                    {
                        await RandomAccess.WriteAsync(handle, memory, offset, ct).ConfigureAwait(false);
                    }

                    count++;
                }

                counters[index] = count;
            }
        }

        /// <summary>
        /// Удаляет тестовые файлы, оставшиеся от аварийно завершённых прогонов,
        /// со всех готовых томов. Вызывается при открытии вкладки.
        /// </summary>
        public static void CleanupOrphanedFiles()
        {
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskBenchmarkEngine.CleanupOrphanedFiles");
                return;
            }

            foreach (var drive in drives)
            {
                try
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                    TryDeleteFile(Path.Combine(drive.RootDirectory.FullName, TempFileName));
                }
                catch (Exception ex)
                {
                    AppLogger.Write(ex, "DiskBenchmarkEngine.CleanupOrphanedFiles");
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiskBenchmarkEngine.TryDeleteFile");
            }
        }

        /// <summary>
        /// Буфер, выровненный по границе сектора: без выравнивания небуферизованный
        /// ввод-вывод возвращает ошибку. Обычный массив закрепляется, адрес округляется
        /// вверх; пока дескриптор закреплён, сборщик мусора массив не двигает.
        /// </summary>
        private sealed class AlignedBuffer : IDisposable
        {
            private readonly byte[] _raw;
            private readonly GCHandle _pin;
            private readonly int _offset;
            private readonly int _blockSize;
            private bool _disposed;

            public AlignedBuffer(int streams, int blockSize)
            {
                _blockSize = blockSize;
                _raw = new byte[(long)streams * blockSize + Alignment];
                _pin = GCHandle.Alloc(_raw, GCHandleType.Pinned);
                long address = _pin.AddrOfPinnedObject().ToInt64();
                _offset = (int)((Alignment - address % Alignment) % Alignment);
            }

            public Memory<byte> Slice(int index) =>
                _raw.AsMemory(_offset + index * _blockSize, _blockSize);

            public void FillWithPseudoRandomData()
            {
                var random = new Random(20260725);
                random.NextBytes(_raw);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_pin.IsAllocated) _pin.Free();
            }
        }
    }
}
