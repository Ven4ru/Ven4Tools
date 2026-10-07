// Окно сообщения рисует сама Windows, и перевод интерфейса до него не доходит. Имя
// MessageBox во всём проекте указывает на обёртку, которая переводит текст и заголовок,
// а затем вызывает настоящее системное окно (см. Shared/Localization/MessageBoxL.cs).
global using MessageBox = Ven4Tools.Localization.MessageBoxL;

// Tr("…") — перевод текста, который показывает не WPF: системные диалоги, значок в трее.
global using static Ven4Tools.Localization.TranslationShortcut;
