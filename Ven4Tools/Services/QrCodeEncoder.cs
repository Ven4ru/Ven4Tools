using System;
using System.Collections.Generic;
using System.Text;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Минимальный кодировщик QR-кода (ISO/IEC 18004): байтовый режим, уровень
    /// коррекции M, версии 1–15. Своя реализация вместо пакета NuGet — нужен один
    /// сценарий (строка подключения к Wi-Fi до ~200 байт), а лишняя зависимость
    /// означала бы ещё одну лицензию и ещё одно обновление на каждый выпуск.
    /// Маска выбирается по штрафам стандарта (правила N1–N4).
    /// </summary>
    internal static class QrCodeEncoder
    {
        public const int MinVersion = 1;
        public const int MaxVersion = 15;

        // Уровень M: число кодовых слов коррекции в каждом блоке и число блоков
        // (индекс — версия, нулевой элемент не используется).
        private static readonly int[] EccPerBlockM = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24 };
        private static readonly int[] BlockCountM  = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10 };

        /// <summary>
        /// Закодировать текст в UTF-8. Возвращает матрицу [строка, столбец], true — тёмный модуль.
        /// Тихая зона (рамка) не входит — её добавляет отрисовка.
        /// </summary>
        /// <exception cref="ArgumentException">Текст не помещается в версию 15.</exception>
        public static bool[,] Encode(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            byte[] payload = Encoding.UTF8.GetBytes(text);

            int version = MinVersion;
            for (; version <= MaxVersion; version++)
            {
                int capacityBits = DataCodewords(version) * 8;
                int needBits = 4 + CountBits(version) + payload.Length * 8;
                if (needBits <= capacityBits) break;
            }
            if (version > MaxVersion)
                throw new ArgumentException("Текст слишком длинный для QR-кода.", nameof(text));

            byte[] data = BuildDataCodewords(payload, version);
            byte[] all = AddEccAndInterleave(data, version);

            int size = version * 4 + 17;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];
            DrawFunctionPatterns(modules, isFunction, version);
            DrawCodewords(modules, isFunction, all);

            // Перебор восьми масок: оставляем ту, у которой штраф меньше.
            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, isFunction, mask);
                DrawFormatBits(modules, isFunction, mask);
                int penalty = Penalty(modules);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestMask = mask;
                }
                ApplyMask(modules, isFunction, mask); // XOR — повторное наложение снимает маску
            }
            ApplyMask(modules, isFunction, bestMask);
            DrawFormatBits(modules, isFunction, bestMask);
            return modules;
        }

        // ── Данные ───────────────────────────────────────────────────────────

        private static int CountBits(int version) => version <= 9 ? 8 : 16;

        /// <summary>Число модулей под данные и коррекцию (без служебных узоров), в битах.</summary>
        internal static int RawDataModules(int version)
        {
            int result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                int numAlign = version / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        internal static int DataCodewords(int version) =>
            RawDataModules(version) / 8 - EccPerBlockM[version] * BlockCountM[version];

        private static byte[] BuildDataCodewords(byte[] payload, int version)
        {
            var bits = new List<bool>();
            AppendBits(bits, 0b0100, 4);                    // байтовый режим
            AppendBits(bits, payload.Length, CountBits(version));
            foreach (byte b in payload) AppendBits(bits, b, 8);

            int capacityBits = DataCodewords(version) * 8;
            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count)); // терминатор
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);               // до целого байта
            for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11)
                AppendBits(bits, pad, 8);

            var result = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return result;
        }

        private static void AppendBits(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        private static byte[] AddEccAndInterleave(byte[] data, int version)
        {
            int numBlocks = BlockCountM[version];
            int eccLen = EccPerBlockM[version];
            int rawCodewords = RawDataModules(version) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            byte[] divisor = ReedSolomonDivisor(eccLen);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - eccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                byte[] ecc = ReedSolomonRemainder(dat, divisor);
                // Короткие блоки дополняются пустым байтом, чтобы чередование шло
                // по одинаковой длине; этот байт при выкладке пропускается.
                var block = new byte[shortBlockLen + 1];
                Array.Copy(dat, 0, block, 0, datLen);
                Array.Copy(ecc, 0, block, shortBlockLen + 1 - eccLen, eccLen);
                blocks.Add(block);
            }

            var result = new byte[rawCodewords];
            int n = 0;
            for (int i = 0; i < blocks[0].Length; i++)
            {
                for (int j = 0; j < blocks.Count; j++)
                {
                    if (i != shortBlockLen - eccLen || j >= numShortBlocks)
                        result[n++] = blocks[j][i];
                }
            }
            return result;
        }

        private static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = GfMultiply(result[j], root);
                    if (j + 1 < result.Length) result[j] ^= result[j + 1];
                }
                root = GfMultiply(root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[^1] = 0;
                for (int i = 0; i < result.Length; i++)
                    result[i] ^= GfMultiply(divisor[i], factor);
            }
            return result;
        }

        /// <summary>Умножение в поле GF(2^8) по модулю x^8+x^4+x^3+x^2+1.</summary>
        private static byte GfMultiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        // ── Служебные узоры ──────────────────────────────────────────────────

        private static void Set(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        private static void DrawFunctionPatterns(bool[,] m, bool[,] f, int version)
        {
            int size = m.GetLength(0);
            for (int i = 0; i < size; i++)
            {
                Set(m, f, 6, i, i % 2 == 0);
                Set(m, f, i, 6, i % 2 == 0);
            }

            DrawFinder(m, f, 3, 3);
            DrawFinder(m, f, size - 4, 3);
            DrawFinder(m, f, 3, size - 4);

            int[] align = AlignmentPositions(version);
            int numAlign = align.Length;
            for (int i = 0; i < numAlign; i++)
            {
                for (int j = 0; j < numAlign; j++)
                {
                    // Три угла заняты поисковыми узорами.
                    if ((i == 0 && j == 0) || (i == 0 && j == numAlign - 1) || (i == numAlign - 1 && j == 0))
                        continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Set(m, f, align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }

            DrawFormatBits(m, f, 0); // резервирует место; настоящие биты — после выбора маски
            DrawVersion(m, f, version);
        }

        private static void DrawFinder(bool[,] m, bool[,] f, int x, int y)
        {
            int size = m.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < size && yy >= 0 && yy < size)
                        Set(m, f, xx, yy, dist != 2 && dist != 4);
                }
            }
        }

        internal static int[] AlignmentPositions(int version)
        {
            if (version == 1) return Array.Empty<int>();
            int numAlign = version / 7 + 2;
            int step = (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            for (int i = numAlign - 1, pos = version * 4 + 17 - 7; i >= 1; i--, pos -= step)
                result[i] = pos;
            return result;
        }

        private static void DrawFormatBits(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            int data = (0 << 3) | mask; // биты уровня M — 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) Set(m, f, 8, i, Bit(bits, i));
            Set(m, f, 8, 7, Bit(bits, 6));
            Set(m, f, 8, 8, Bit(bits, 7));
            Set(m, f, 7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) Set(m, f, 14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) Set(m, f, size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) Set(m, f, 8, size - 15 + i, Bit(bits, i));
            Set(m, f, 8, size - 8, true); // всегда тёмный модуль
        }

        private static void DrawVersion(bool[,] m, bool[,] f, int version)
        {
            if (version < 7) return;
            int size = m.GetLength(0);
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = Bit(bits, i);
                int a = size - 11 + i % 3;
                int b = i / 3;
                Set(m, f, a, b, bit);
                Set(m, f, b, a, bit);
            }
        }

        private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

        // ── Данные в матрицу, маски, штрафы ──────────────────────────────────

        private static void DrawCodewords(bool[,] m, bool[,] f, byte[] data)
        {
            int size = m.GetLength(0);
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5; // вертикальная синхрополоса
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!f[y, x] && i < data.Length * 8)
                        {
                            m[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                        // Оставшиеся «хвостовые» биты остаются светлыми — так и положено.
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert && !f[y, x]) m[y, x] = !m[y, x];
                }
            }
        }

        /// <summary>Штраф по правилам N1–N4 стандарта (меньше — лучше читается сканерами).</summary>
        private static int Penalty(bool[,] m)
        {
            int size = m.GetLength(0);
            int result = 0;

            // N1: серии из 5+ одинаковых модулей в строке/столбце.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < size; a++)
                {
                    int run = 1;
                    for (int b = 1; b < size; b++)
                    {
                        bool cur = pass == 0 ? m[a, b] : m[b, a];
                        bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                        if (cur == prev)
                        {
                            run++;
                            if (run == 5) result += 3;
                            else if (run > 5) result++;
                        }
                        else run = 1;
                    }
                }
            }

            // N2: одноцветные квадраты 2×2.
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = m[y, x];
                    if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1]) result += 3;
                }

            // N3: узор, похожий на поисковый (1:1:3:1:1 со светлой полосой из 4 модулей).
            bool[] p1 = { true, false, true, true, true, false, true, false, false, false, false };
            bool[] p2 = { false, false, false, false, true, false, true, true, true, false, true };
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b + 11 <= size; b++)
                {
                    bool rowMatch1 = true, rowMatch2 = true, colMatch1 = true, colMatch2 = true;
                    for (int k = 0; k < 11; k++)
                    {
                        bool r = m[a, b + k], c = m[b + k, a];
                        if (r != p1[k]) rowMatch1 = false;
                        if (r != p2[k]) rowMatch2 = false;
                        if (c != p1[k]) colMatch1 = false;
                        if (c != p2[k]) colMatch2 = false;
                    }
                    if (rowMatch1) result += 40;
                    if (rowMatch2) result += 40;
                    if (colMatch1) result += 40;
                    if (colMatch2) result += 40;
                }
            }

            // N4: доля тёмных модулей далеко от 50 %.
            int dark = 0;
            foreach (bool b in m) if (b) dark++;
            int total = size * size;
            int k4 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += Math.Max(0, k4) * 10;
            return result;
        }
    }
}
