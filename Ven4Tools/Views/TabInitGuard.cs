using System;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Единая точка запуска инициализации вкладки (и любого другого окна) из
    /// обработчика <c>Loaded</c>.
    /// <para>Лямбда вида <c>Loaded += async (_, _) =&gt; await vm.InitializeAsync();</c>
    /// компилируется в <c>async void</c>: исключение из неё не попадает ни в один catch
    /// вызывающего кода и роняет приложение целиком через
    /// <c>DispatcherUnhandledException</c> в <c>App.xaml.cs</c> — тот же класс сбоя,
    /// ради которого сделан <see cref="ViewModels.RelayCommand.FromAsync"/>. Но
    /// инициализация по <c>Loaded</c> идёт мимо команд, поэтому во время MVVM-миграции
    /// этот паттерн разошёлся по вкладкам шестью независимыми копиями, пять из которых
    /// остались без единого try/catch. Здесь перехват сделан один раз для всех сразу.</para>
    /// <para>Сбой инициализации гасится не молча: причина уходит в журнал приложения,
    /// иначе пустая вкладка ничем не отличалась бы от вкладки без данных.</para>
    /// </summary>
    internal static class TabInitGuard
    {
        /// <summary>
        /// Однократный (на экземпляр вкладки) запуск асинхронной инициализации.
        /// Флаг взводится ДО запуска — повторный <c>Loaded</c>, пришедший, пока задача
        /// ещё выполняется, второй инициализации не начнёт.
        /// </summary>
        public static void RunOnce(ref bool guard, Func<Task> initAsync, string context)
        {
            if (guard) return;
            guard = true;
            Run(initAsync, context);
        }

        /// <summary>Запуск асинхронной инициализации при каждом показе вкладки.</summary>
        public static async void Run(Func<Task> initAsync, string context)
        {
            try
            {
                await initAsync();
            }
            catch (OperationCanceledException)
            {
                // Отмена — штатный исход (закрытие окна во время загрузки, таймаут
                // HttpClient), в журнал ошибок не пишем.
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, context);
            }
        }

        /// <summary>
        /// Синхронный вариант — для вкладок, чья инициализация ничего не ждёт.
        /// Отдельное имя, а не перегрузка: лямбда <c>() =&gt; Foo()</c> подошла бы и под
        /// <see cref="Action"/>, и под <c>Func&lt;Task&gt;</c>, а выбирать перегрузку
        /// по возвращаемому типу вызываемого метода — источник тихих ошибок.
        /// </summary>
        public static void RunSync(Action init, string context)
        {
            try
            {
                init();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, context);
            }
        }
    }
}
