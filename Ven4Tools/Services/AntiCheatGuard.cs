using System;
using System.Windows.Threading;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Страж игр с античитом: пока Ven4Tools запущен, раз в 3 секунды смотрит, не началась
    /// ли защищённая игра. Когда началась:
    /// — оверлей скрывается (если человек сам не разрешил его в таких играх);
    /// — драйвер датчиков отпускается и выключается;
    /// — если драйвер загружался после старта Windows, приходит уведомление в трее:
    ///   что делать, если игра не запустится. Окно с вопросом посреди игры не показывается.
    ///
    /// Работает, только когда есть что защищать: оверлей включён или драйвер датчиков
    /// установлен. Иначе таймер ничего не делает.
    /// </summary>
    public static class AntiCheatGuard
    {
        /// <summary>Уведомление в трее (заголовок, текст); подключает главное окно.</summary>
        public static Action<string, string>? Notify;

        private static DispatcherTimer? _timer;
        private static AntiCheat? _current;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += async (_, _) => await TickAsync();
            _timer.Start();
        }

        /// <summary>Идёт ли сейчас игра с античитом (по последней проверке).</summary>
        public static AntiCheat? Current => _current;

        private static async System.Threading.Tasks.Task TickAsync()
        {
            bool overlay = OverlayService.Instance.IsVisible;
            bool driver = PawnIoDriver.FeatureEnabled && PawnIoDriver.IsInstalled();
            if (!overlay && !driver) { _current = null; return; }

            AntiCheat? now;
            try { now = AntiCheatDetector.Active(); }
            catch { return; }

            if (now == null) { _current = null; return; }
            if (_current == now) return;
            _current = now;
            string name = AntiCheatDetector.ShortName(now.Value);
            AppLogger.Write($"🛡 Запущена игра с античитом {name}");

            if (overlay && !ProfileService.Current.OverlayInAntiCheatGames)
            {
                OverlayService.Instance.Hide();
                Send(Tr("Оверлей скрыт"),
                    string.Format(Tr("Запущена игра с античитом {0}. Оверлей скрыт, чтобы античит не принял его за постороннюю программу. Разрешить его в таких играх можно галочкой в «Диагностике» — на свой риск."), name));
            }

            if (driver)
            {
                bool loaded = PawnIoDriver.LoadedSinceBoot();
                await HardwareSensorService.Instance.ReleaseDriverAsync();
                if (now == AntiCheat.Faceit)
                    Send(Tr("Драйвер датчиков и FACEIT"),
                        Tr("FACEIT может не запускаться, пока в системе установлен драйвер датчиков PawnIO. Если клиент FACEIT сообщит о несовместимом драйвере — удалите драйвер в «Диагностике» и перезагрузите компьютер."));
                else if (loaded)
                    Send(Tr("Драйвер датчиков выключен"),
                        string.Format(Tr("Запущена игра с античитом {0}. Драйвер датчиков процессора загружался после старта Windows. Если игра не запустится или сообщит о несовместимом драйвере — перезагрузите компьютер: драйвер уже выключен и после перезагрузки не загрузится."), name));
            }
        }

        private static void Send(string title, string body)
        {
            AppLogger.Write($"🛡 {title}: {body}");
            try { Notify?.Invoke(title, body); }
            catch (Exception ex) { AppLogger.Write(ex, "AntiCheatGuard.Notify"); }
        }
    }
}
