# Прототип поясняющих подсказок кнопок — план реализации

> **Для исполнителя:** обязательно выполнять этот план через
> `superpowers:executing-plans` по задачам и отмечать пункты `- [ ]`.

**Цель:** собрать в отдельном worktree полностью рабочие клиент и лаунчер Ven4Tools,
где каждая функциональная кнопка имеет понятную русскую всплывающую подсказку.

**Архитектура:** продуктовая логика кнопок не меняется. Тексты задаются рядом с кнопками
через `ToolTip` в XAML либо при программном создании `Button`; оформление задаётся общими
стилями клиента и лаунчера. Отдельный xUnit-тест сканирует XAML и защищает покрытие
функциональных кнопок от регрессий.

**Стек:** .NET 8, WPF, C#, XAML, xUnit, MSTest/FlaUI, Git worktree.

## Общие ограничения

- Все продуктовые изменения выполнять только в
  `C:\Users\Vench\Documents\GitHub\Ven4Tools-button-tooltips-prototype`.
- Ветка прототипа: `prototype/button-tooltips-20260726`.
- Подсказки — только на русском, одно или два коротких предложения, предпочтительно
  не длиннее 140 символов.
- Не менять обработчики, команды, каталог, подписи, версии и релизные файлы.
- Не публиковать: без push, PR, tag, release и deploy.
- До изменения существующего файла скопировать его в timestamp-бэкап `_backups`.
- Навигация, простое закрытие окна, обычная отмена диалога, кнопка «Готово»,
  декоративные `RepeatButton` и звёзды оценки исключены.
- «Отмена» и «Пропустить» получают подсказку, если прерывают длительную операцию.
- UI-тесты не нажимают установку, удаление, сброс сети, очистку данных и бенчмарк.

---

### Задача 1: Изолированная рабочая копия и тестовый контракт покрытия

**Файлы:**

- Создать worktree:
  `C:\Users\Vench\Documents\GitHub\Ven4Tools-button-tooltips-prototype`
- Создать:
  `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs`

**Интерфейсы:**

- Потребляет: XAML-файлы из `Ven4Tools` и `Ven4Tools.Launcher`.
- Производит: тесты `AllFunctionalXamlButtonsHaveExplanations` и
  `DynamicButtonsHaveExplanations`.

- [ ] **Шаг 1: создать worktree от утверждённого локального HEAD**

В основной рабочей копии выполнить:

```powershell
git worktree add -b prototype/button-tooltips-20260726 `
  C:\Users\Vench\Documents\GitHub\Ven4Tools-button-tooltips-prototype HEAD
```

Проверить:

```powershell
git -C C:\Users\Vench\Documents\GitHub\Ven4Tools-button-tooltips-prototype status --short --branch
```

Ожидается чистая ветка `prototype/button-tooltips-20260726`; пользовательские
`version.json` и `version.json.sig` из основной папки отсутствуют.

- [ ] **Шаг 2: создать бэкап до первого изменения**

Создать `_backups\_backup_YYYYMMDD_HHmmss` и сохранить туда исходные файлы, которые
будут изменяться в следующих задачах, с сохранением относительных путей.

- [ ] **Шаг 3: написать падающий xUnit-тест покрытия**

Тест должен:

1. найти корень по `Ven4Tools.sln`;
2. загрузить все `*.xaml` из каталогов двух приложений;
3. рассмотреть элементы пространства имён WPF с именем `Button`;
4. считать кнопку кандидатом, если у неё есть `Click`, `Command` или `x:Name`;
5. исключить:
   - обработчики `NavigateTo*`;
   - `btnClose*`, `btnExit`, `btnOk` с текстом «Готово»;
   - обычные `Cancel`, `Decline`, `Skip`, если они не относятся к длительной операции;
   - `star1`…`star5`;
6. потребовать непустой `ToolTip` у оставшихся кнопок;
7. вывести в сообщении ошибки путь, `x:Name`, `Content`, `Click` и `Command`;
8. проверить три программно создаваемые кнопки:
   `MainWindow.xaml.cs` (`installBtn`, `unpinBtn`) и
   `DiagnosticsTab.RebootHistory.cs` (`fixBtn`) на присваивание `ToolTip`.

Минимальная форма основной проверки:

```csharp
[Fact]
public void AllFunctionalXamlButtonsHaveExplanations()
{
    IReadOnlyList<string> missing = ScanFunctionalButtons()
        .Where(button => string.IsNullOrWhiteSpace(button.ToolTip))
        .Select(button => button.Diagnostic)
        .ToArray();

    Assert.True(missing.Count == 0,
        "Функциональные кнопки без пояснения:" + Environment.NewLine +
        string.Join(Environment.NewLine, missing));
}
```

- [ ] **Шаг 4: запустить тест и подтвердить RED**

```powershell
dotnet test .\tests\Ven4Tools.Tests\Ven4Tools.Tests.csproj -c Release --no-build `
  --filter FullyQualifiedName~ButtonToolTipCoverageTests
```

Ожидается `FAIL` со списком существующих функциональных кнопок без подсказок.

- [ ] **Шаг 5: закоммитить только тестовый контракт**

```powershell
git add tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs
git commit -m "Тест: зафиксировано покрытие пояснений кнопок"
```

---

### Задача 2: Единое оформление всплывающих подсказок

**Файлы:**

- Изменить: `Ven4Tools/App.xaml`
- Изменить: `Ven4Tools.Launcher/App.xaml`
- Тест: `tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs`

**Интерфейсы:**

- Потребляет: существующие ресурсы `CardBackground`, `TextPrimary`, `BorderBrush`.
- Производит: неявный стиль `ToolTip` с переносом текста и ограниченной шириной.

- [ ] **Шаг 1: расширить тест контрактом стиля**

Добавить проверку, что оба `App.xaml` содержат неявный `Style TargetType="ToolTip"`,
а его шаблон содержит `TextBlock` с `TextWrapping="Wrap"` и `MaxWidth="360"`.

- [ ] **Шаг 2: запустить тест и подтвердить RED**

Ожидается ошибка как минимум по переносу и максимальной ширине.

- [ ] **Шаг 3: минимально обновить оба стиля**

Для `ToolTip` задать:

```xml
<Setter Property="MaxWidth" Value="380"/>
<Setter Property="Padding" Value="10,7"/>
<Setter Property="FontSize" Value="12"/>
<Setter Property="ToolTipService.ShowDuration" Value="15000"/>
```

В шаблоне использовать:

```xml
<TextBlock Text="{TemplateBinding Content}"
           TextWrapping="Wrap"
           MaxWidth="360"
           Foreground="{TemplateBinding Foreground}"
           FontSize="{TemplateBinding FontSize}"/>
```

На неявном стиле `Button` задать:

```xml
<Setter Property="ToolTipService.InitialShowDelay" Value="450"/>
<Setter Property="ToolTipService.BetweenShowDelay" Value="100"/>
<Setter Property="ToolTipService.ShowDuration" Value="15000"/>
```

- [ ] **Шаг 4: запустить тест покрытия стиля**

Ожидается PASS для стилевых проверок; общий тест кнопок пока остаётся RED.

- [ ] **Шаг 5: закоммитить оформление**

```powershell
git add Ven4Tools/App.xaml Ven4Tools.Launcher/App.xaml `
  tests/Ven4Tools.Tests/ButtonToolTipCoverageTests.cs
git commit -m "Прототип: унифицированы всплывающие подсказки"
```

---

### Задача 3: Пояснения кнопок лаунчера

**Файлы:**

- Изменить: `Ven4Tools.Launcher/MainWindow.xaml`
- Изменить: `Ven4Tools.Launcher/SettingsWindow.xaml`
- Изменить: `Ven4Tools.Launcher/InstallReportWindow.xaml`
- Изменить: `Ven4Tools.Launcher/CrashReportWindow.xaml`
- Изменить: `tests/Ven4Tools.UITests/LauncherSmokeTests.cs`

**Интерфейсы:**

- Потребляет: стилевой контракт задачи 2.
- Производит: пояснения для установки/запуска, проверки обновлений, списка изменений,
  выбора папки, поиска клиента, настроек, обновления лаунчера, установки компонентов,
  удаления клиента, отмены загрузки и отправки отчётов.

- [ ] **Шаг 1: добавить падающую UI-проверку основных кнопок**

В `LauncherSmokeTests` добавить тест, который для безопасного набора
`btnSelectFolder`, `btnFindClient`, `btnCheckUpdates`, `btnLaunchApp`,
`btnChangelog`, `btnOpenSettings`, `btnDeleteClient` проверяет непустой
`AutomationElement.HelpText` либо открываемый WPF ToolTip.

- [ ] **Шаг 2: запустить launcher UI-тест и подтвердить RED**

```powershell
dotnet test .\tests\Ven4Tools.UITests\Ven4Tools.UITests.csproj -c Release `
  --filter FullyQualifiedName~FunctionalButtonsExposeExplanations
```

- [ ] **Шаг 3: добавить точные подсказки в XAML лаунчера**

Каждая формулировка должна соответствовать обработчику. Для рискованных действий
обязательно указать подтверждение либо возможное изменение файлов. Не добавлять
подсказки на `btnExit`, `btnCloseDetails` и `btnCloseSettings`.

- [ ] **Шаг 4: запустить тесты лаунчера**

```powershell
dotnet test .\tests\Ven4Tools.Tests\Ven4Tools.Tests.csproj -c Release `
  --filter FullyQualifiedName~ButtonToolTipCoverageTests
dotnet test .\tests\Ven4Tools.UITests\Ven4Tools.UITests.csproj -c Release `
  --filter FullyQualifiedName~FunctionalButtonsExposeExplanations
```

Ожидается PASS для лаунчерной части; клиентская часть общего покрытия ещё может быть RED.

- [ ] **Шаг 5: закоммитить блок лаунчера**

```powershell
git add Ven4Tools.Launcher tests/Ven4Tools.UITests/LauncherSmokeTests.cs
git commit -m "Прототип: пояснены действия лаунчера"
```

---

### Задача 4: Пояснения главного окна и диалогов клиента

**Файлы:**

- Изменить: `Ven4Tools/MainWindow.xaml`
- Изменить: `Ven4Tools/MainWindow.xaml.cs`
- Изменить: `Ven4Tools/AlternativeSourceDialog.xaml`
- Изменить: `Ven4Tools/Views/AppCardWindow.xaml`
- Изменить: `Ven4Tools/Views/CategorySelectionWindow.xaml`
- Изменить: `Ven4Tools/Views/EulaConfirmWindow.xaml`
- Изменить: `Ven4Tools/Views/FeedbackWindow.xaml`
- Изменить: `Ven4Tools/Views/LocalInstallerDialog.xaml`
- Изменить: `Ven4Tools/Views/MasGuideWindow.xaml`
- Изменить: `Ven4Tools/Views/PresetSaveDialog.xaml`
- Изменить: `Ven4Tools/Views/SnapshotNameDialog.xaml`
- Изменить: `Ven4Tools/Views/SplashWindow.xaml`
- Изменить: `Ven4Tools/Views/WindowsUpdateResultWindow.xaml`

**Интерфейсы:**

- Потребляет: стилевой контракт задачи 2.
- Производит: пояснения глобальных действий, карточки приложения, подтверждений,
  обратной связи, локального установщика и длительной предзагрузки.

- [ ] **Шаг 1: добавить подсказки статическим функциональным кнопкам**

Не пояснять навигационные `btn*Tab`, простое закрытие, обычную отмену и звёзды.
Пояснить очистку глобального журнала, запуск/установку/переустановку/удаление приложения,
принятие EULA с установкой, отправку обратной связи, открытие PowerShell, сохранение
пресета/снапшота, пропуск предзагрузки и немедленную перезагрузку.

- [ ] **Шаг 2: пояснить динамические кнопки закреплённых приложений**

В инициализаторах `installBtn` и `unpinBtn` назначить `ToolTip`:

```csharp
ToolTip = "Установит или запустит закреплённое приложение в зависимости от его состояния."
```

и

```csharp
ToolTip = "Уберёт приложение из панели закреплённых. Само приложение останется на компьютере."
```

- [ ] **Шаг 3: запустить тест покрытия**

Ожидается отсутствие ошибок по главному окну и диалогам; вкладки ещё могут оставаться RED.

- [ ] **Шаг 4: закоммитить блок окон**

```powershell
git add Ven4Tools/MainWindow.xaml Ven4Tools/MainWindow.xaml.cs Ven4Tools/AlternativeSourceDialog.xaml `
  Ven4Tools/Views
git commit -m "Прототип: пояснены действия окон клиента"
```

---

### Задача 5: Пояснения функциональных кнопок вкладок клиента

**Файлы:**

- Изменить XAML:
  `Ven4Tools/Views/Tabs/AboutTab.xaml`,
  `ActivationTab.xaml`, `BenchmarkTab.xaml`, `CatalogTab.xaml`, `DebloaterTab.xaml`,
  `DiagnosticsTab.xaml`, `HistoryTab.xaml`, `InstalledTab.xaml`, `NetworkTab.xaml`,
  `OfficeTab.xaml`, `SystemTab.xaml`, `WindowsUpdateTab.xaml`
- Изменить:
  `Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs`
- Тест: `Ven4Tools.ClientUITests/KeyButtonsSmokeTests.cs`

**Интерфейсы:**

- Потребляет: стилевой контракт задачи 2 и критерии функциональной кнопки из спецификации.
- Производит: полное покрытие основных вкладок и динамической кнопки исправления
  журнала перезагрузок.

- [ ] **Шаг 1: добавить падающий in-process/UI smoke контракт**

Добавить безопасную проверку непустых пояснений у репрезентативных кнопок:
`btnInstall`, `btnRefresh`, `btnCheckUpdates`, `btnCheck`, `btnDownloadOffice`,
`btnActivateWindows`, `btnApplyDebloat`, `btnRunAll`, `btnClearHistory`,
`btnGitHub`, `btnStartBenchmark`.

- [ ] **Шаг 2: запустить клиентский тест и подтвердить RED**

```powershell
dotnet test .\Ven4Tools.ClientUITests\Ven4Tools.ClientUITests.csproj -c Release `
  --filter FullyQualifiedName~ФункциональныеКнопки_ИмеютПояснения
```

- [ ] **Шаг 3: пройти вкладки по одной и добавить точные тексты**

Для каждого действия прочитать соответствующий `Click`-обработчик или `Command`.
Особенно явно описать:

- устанавливает ли действие что-либо или только проверяет;
- создаётся ли точка восстановления;
- меняются ли сеть, реестр, службы, кэш или файлы;
- открывается ли внешний сайт;
- может ли операция занять несколько минут;
- что именно отменяется кнопкой остановки.

Существующие полезные подсказки сохранить; технические и слишком короткие уточнить.

- [ ] **Шаг 4: пояснить динамическую кнопку исправления**

В `DiagnosticsTab.RebootHistory.cs` назначить `fixBtn.ToolTip` с фактическим эффектом
конкретного предлагаемого исправления и указанием, что изменение выполняется только
после нажатия.

- [ ] **Шаг 5: получить GREEN полного контракта покрытия**

```powershell
dotnet test .\tests\Ven4Tools.Tests\Ven4Tools.Tests.csproj -c Release `
  --filter FullyQualifiedName~ButtonToolTipCoverageTests
dotnet test .\Ven4Tools.ClientUITests\Ven4Tools.ClientUITests.csproj -c Release `
  --filter FullyQualifiedName~ФункциональныеКнопки_ИмеютПояснения
```

Ожидается PASS без пропущенных функциональных кнопок.

- [ ] **Шаг 6: закоммитить блок вкладок**

```powershell
git add Ven4Tools/Views/Tabs Ven4Tools.ClientUITests/KeyButtonsSmokeTests.cs
git commit -m "Прототип: пояснены действия вкладок клиента"
```

---

### Задача 6: Полная сборка, визуальная проверка и итоговый аудит

**Файлы:**

- Изменять тесты прототипа только для исправления подтверждённой ошибки проверки;
  продуктовый код после начала итогового gate не расширять.
- Создать локальные снимки в `TestResults` без добавления в Git.

**Интерфейсы:**

- Потребляет: результаты задач 1–5.
- Производит: проверенный собираемый прототип и итоговый отчёт.

- [ ] **Шаг 1: выполнить полную сборку**

```powershell
dotnet build .\Ven4Tools.sln -c Release --nologo
```

Ожидается 0 ошибок и 0 предупреждений.

- [ ] **Шаг 2: запустить полный статический контракт**

```powershell
dotnet test .\tests\Ven4Tools.Tests\Ven4Tools.Tests.csproj -c Release `
  --filter FullyQualifiedName~ButtonToolTipCoverageTests
```

- [ ] **Шаг 3: выполнить безопасные UI smoke**

Запустить новые клиентский и launcher-тесты подсказок. Навести курсор на длинную
подсказку в каждом приложении и визуально проверить:

- появление после короткой задержки;
- перенос строк;
- читаемый контраст;
- отсутствие обрезания;
- исчезновение после ухода курсора;
- отсутствие смещения интерфейса.

- [ ] **Шаг 4: проверить diff и секреты**

```powershell
git diff --check
C:\Users\Vench\Tools\gitleaks\gitleaks.exe dir . --no-banner --redact
git status --short --branch
git diff --stat b7c8e74..HEAD
```

- [ ] **Шаг 5: сверить требования спецификации**

Проверить каждый пункт
`docs/superpowers/specs/2026-07-26-button-tooltips-prototype-design.md`:
функциональные кнопки покрыты, исключения обоснованы, основная рабочая копия не содержит
продуктовых правок, публикаций нет.

- [ ] **Шаг 6: сделать итоговый коммит только при наличии финальных правок**

```powershell
git add Ven4Tools Ven4Tools.Launcher Ven4Tools.ClientUITests `
  tests/Ven4Tools.Tests tests/Ven4Tools.UITests
git commit -m "Прототип: завершены пояснения функциональных кнопок"
```

- [ ] **Шаг 7: дополнить дневной журнал**

Записать изменённые файлы, причины, команды, результаты сборки/тестов, путь worktree,
путь бэкапа и факт отсутствия push/PR/tag/release.
