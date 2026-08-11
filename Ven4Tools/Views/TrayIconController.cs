using System;
using System.Windows.Threading;
using Ven4Tools.Services;
using Forms = System.Windows.Forms;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Иконка в системном трее: контекстное меню, двойной клик и всплывающие
    /// подсказки. Вынесено из главного окна, потому что это целиком WinForms-объект
    /// (<see cref="Forms.NotifyIcon"/>) со своим временем жизни; окну остаются только
    /// два действия — «показать» и «выйти», которые оно передаёт обратными вызовами.
    /// Здесь же живёт регистрация уведомителя фонового сервиса обновлений: балуны
    /// показываются через эту же иконку, чтобы не плодить вторую в трее.
    /// </summary>
    public sealed class TrayIconController : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action _showRequested;
        private readonly Action _exitRequested;
        private Forms.NotifyIcon? _icon;

        /// <summary>
        /// true, если иконку в трее удалось создать. Сворачивание в трей должно
        /// проверять этот флаг — иначе при сбое <see cref="Initialize"/> окно
        /// прячется без иконки, вернуть его нечем (единственный экземпляр держит
        /// мьютекс, повторный запуск невозможен).
        /// </summary>
        public bool IsAvailable => _icon != null;

        public TrayIconController(Dispatcher dispatcher, Action showRequested, Action exitRequested)
        {
            _dispatcher = dispatcher;
            _showRequested = showRequested;
            _exitRequested = exitRequested;
        }

        public void Initialize()
        {
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                                  ?? string.Empty;
                System.Drawing.Icon icon = string.IsNullOrEmpty(exePath)
                    ? System.Drawing.SystemIcons.Application
                    : System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;

                _icon = new Forms.NotifyIcon
                {
                    Icon    = icon,
                    Visible = true,
                    Text    = "Ven4Tools"
                };

                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Открыть", null, (_, _) => _dispatcher.Invoke(_showRequested));
                menu.Items.Add("-");
                menu.Items.Add("Выход", null, (_, _) => _dispatcher.Invoke(_exitRequested));

                _icon.ContextMenuStrip = menu;
                _icon.DoubleClick += (_, _) => _dispatcher.Invoke(_showRequested);

                // Фоновый сервис уведомлений показывает балуны через нашу трей-иконку,
                // чтобы не плодить вторую иконку в трее.
                UpdateBackgroundService.RegisterNotifier((title, body) =>
                    _dispatcher.Invoke(() =>
                    {
                        try
                        {
                            _icon?.ShowBalloonTip(8000, title, body, Forms.ToolTipIcon.Info);
                        }
                        catch { }
                    }));
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[TrayIconController] Не удалось создать иконку в трее: {ex.Message}");
            }
        }

        public void ShowBalloon(int timeoutMs, string title, string body) =>
            _icon?.ShowBalloonTip(timeoutMs, title, body, Forms.ToolTipIcon.Info);

        /// <summary>
        /// Снимает подписку фонового сервиса на балуны. Отдельно от <see cref="Dispose"/>:
        /// иконка уничтожается при сворачивании-выходе, а уведомитель отвязывается при
        /// закрытии окна — так же, как было до выделения этого класса.
        /// </summary>
        public void UnregisterNotifier() => UpdateBackgroundService.UnregisterNotifier();

        public void Dispose() => _icon?.Dispose();
    }
}
