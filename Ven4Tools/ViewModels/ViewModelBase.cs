using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Общая база для всех ViewModel и моделей-строк с уведомлениями об изменениях.
    /// До 2026-08-31 каждая вкладка носила собственную копию <c>SetField</c>/
    /// <c>OnPropertyChanged</c> (17 копий, два несовместимых варианта: одни
    /// возвращали <c>bool</c>, другие <c>void</c>). Из-за <c>void</c>-варианта
    /// сеттеры не могли отличить реальное изменение от повторной записи того же
    /// значения и дёргали <c>RaiseCanExecuteChanged</c> безусловно. Здесь вариант
    /// один и он возвращает <c>bool</c>.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Записывает значение в поле и поднимает уведомление, если значение
        /// действительно изменилось. Возвращает <c>true</c> при реальном изменении —
        /// на этом строятся сеттеры вида
        /// <c>if (SetField(ref _f, value)) Command.RaiseCanExecuteChanged();</c>.
        /// </summary>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            // EqualityComparer<T>.Default вместо object.Equals: для значимых типов
            // (bool/int/double/Visibility — самые частые здесь) он не боксирует и
            // вызывает строго типизированный IEquatable<T>. Для ссылочных типов
            // результат тот же, что у прежнего Equals(field, value).
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
