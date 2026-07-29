using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        /// <summary>
        /// Команда с асинхронным обработчиком.
        /// <para>Лямбда вида <c>async _ =&gt; await ...</c>, переданная в обычный
        /// конструктор, компилируется в <c>async void</c>: исключение из неё не попадает
        /// ни в один catch вызывающего кода и роняет всё приложение целиком. Этот класс
        /// сбоя в проекте уже закрывали точечно — у установки из пина
        /// (<c>MainWindow.PinInstallBtn_Click</c>), переустановки из истории
        /// (<c>HistoryTab.BtnReinstall_Click</c>), карточки приложения и повтора неудачной
        /// установки (<c>FailedInstallViewModel</c>), — но каждый раз отдельной копией
        /// try/catch в конкретном месте, из-за чего асинхронные команды каталога так и
        /// остались без защиты. Здесь перехват сделан один раз для всех сразу.</para>
        /// </summary>
        public static RelayCommand FromAsync(
            Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            return new RelayCommand(async parameter =>
            {
                try
                {
                    await execute(parameter);
                }
                catch (OperationCanceledException)
                {
                    // Отмена — штатный исход (пользователь прервал операцию либо
                    // истёк таймаут HttpClient), в журнал ошибок не пишем.
                }
                catch (Exception ex)
                {
                    AppLogger.Write(ex, "Ошибка выполнения команды");
                }
            }, canExecute);
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
}
