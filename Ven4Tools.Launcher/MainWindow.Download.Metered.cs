using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        // Не скачивать фоновые (автоматические) обновления по лимитному подключению.
        // Ручное обновление этим не ограничивается никогда — пользователь сам нажал
        // кнопку и сам решил, что трафик можно тратить.
        private bool _deferUpdatesOnMetered = true;

        // Версия, о которой уже сказали «ждёт безлимитного подключения», — чтобы не
        // показывать всплывающее уведомление на каждом тике фоновой проверки.
        private string _meteredDeferNotifiedVersion = "";

        // Версия, отложенная из-за лимитного подключения: её автообновление
        // повторяется, как только сеть сменится (а не через три часа до следующего тика).
        private string? _meteredDeferredVersion;
        private bool _networkChangeSubscribed;

        internal bool DeferUpdatesOnMetered => _deferUpdatesOnMetered;

        internal void OnDeferUpdatesOnMeteredChanged(bool isChecked)
        {
            _deferUpdatesOnMetered = isChecked;
            SaveSettings();
            if (!isChecked && _meteredDeferredVersion != null)
            {
                // Пользователь снял ограничение — отложенное обновление можно не ждать.
                string version = _meteredDeferredVersion;
                _meteredDeferredVersion = null;
                _ = TriggerAutoClientUpdateAsync(version);
            }
        }

        /// <summary>
        /// Нужно ли отложить ФОНОВОЕ скачивание обновления до безлимитной сети.
        /// true — отложено (запись в журнал и уведомление уже сделаны).
        /// </summary>
        private async Task<bool> DeferBackgroundDownloadIfMeteredAsync(string latestVersion)
        {
            if (!_deferUpdatesOnMetered) return false;

            // COM-вызов в фоне: он быстрый, но UI-поток ждать сеть не должен ни в каком виде.
            bool metered = await Task.Run(NetworkCostProbe.IsCurrentConnectionMetered);
            if (!metered)
            {
                _meteredDeferredVersion = null;
                return false;
            }

            _meteredDeferredVersion = latestVersion;
            SubscribeNetworkChange();

            AddLog($"⏸ Автообновление клиента до {latestVersion} ждёт безлимитного подключения " +
                   "(подключение отмечено в Windows как лимитное). Обновить сейчас можно вручную.");
            if (_meteredDeferNotifiedVersion != latestVersion)
            {
                _meteredDeferNotifiedVersion = latestVersion;
                _notifyIcon?.ShowBalloonTip(
                    8000,
                    Tr("Обновление ждёт безлимитного подключения"),
                    Tr($"Ven4Tools {latestVersion} скачается автоматически, когда компьютер подключится к сети без лимита трафика. Обновить сейчас можно вручную в окне лаунчера."),
                    ToolTipIcon.Info);
            }
            return true;
        }

        // Смена сети (Wi-Fi вместо модема и т.п.) — повод проверить снова. Подписка
        // одна на весь процесс, событие приходит с потока пула.
        private void SubscribeNetworkChange()
        {
            if (_networkChangeSubscribed) return;
            _networkChangeSubscribed = true;
            NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkChangedForDeferredUpdate();
        }

        private int _networkRecheckScheduled;

        private void OnNetworkChangedForDeferredUpdate()
        {
            if (_meteredDeferredVersion == null) return;
            // Смена сети приходит пачкой событий — одна перепроверка на пачку, с паузой,
            // пока новое подключение получит адрес и Windows определит его стоимость.
            if (System.Threading.Interlocked.Exchange(ref _networkRecheckScheduled, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    if (NetworkCostProbe.IsCurrentConnectionMetered()) return;
                    Dispatcher.Invoke(() =>
                    {
                        string? version = _meteredDeferredVersion;
                        if (version == null) return;
                        _meteredDeferredVersion = null;
                        // Пока ждали сеть, эту версию могли поставить вручную.
                        string? installed = ReadInstalledClientVersion();
                        if (installed != null && !VersionComparer.IsNewer(version, installed)) return;
                        AddLog("🌐 Подключение больше не лимитное — продолжаю отложенное автообновление");
                        _ = TriggerAutoClientUpdateAsync(version);
                    });
                }
                catch { /* завершение приложения — Dispatcher уже недоступен */ }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _networkRecheckScheduled, 0);
                }
            });
        }
    }
}
