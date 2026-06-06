# Ven4Tools — Project Guide

## Structure

```
Ven4Tools/                  ← solution root
├── Ven4Tools/              ← client (main app)
│   ├── Views/Tabs/         ← CatalogTab, InstalledTab, DebloaterTab, etc.
│   ├── Views/              ← SplashWindow, LoginWindow, ProfileWindow, etc.
│   ├── Services/           ← все сервисы (см. ниже)
│   ├── Models/             ← AppEntry, MasterCatalog, UserProfile, etc.
│   ├── Resources/Mascots/  ← loading.png, ready.png (splash images)
│   ├── App.xaml / App.xaml.cs
│   └── MainWindow.xaml / MainWindow.xaml.cs
├── Ven4Tools.Launcher/     ← updater (single-file exe)
│   └── MainWindow.xaml.cs  ← вся логика обновления
├── Catalog/
│   └── master.json         ← каталог приложений (71 app, v5)
└── _release/               ← gitignored, артефакты сборки
```

## Tech Stack

- .NET 8, WPF, win-x64, SelfContained (не Single File для клиента)
- Launcher: SelfContained + PublishSingleFile=true
- NuGet: WebView2, Newtonsoft.Json, System.Management, Microsoft.Extensions.DI

## Key Services (Ven4Tools/Services/)

| Сервис | Что делает |
|---|---|
| `CatalogLoaderService` | Загружает master.json (online → cache → embedded). Статический `LoadedCatalog` + `PreloadAsync()` для splash |
| `ProfileService` | Настройки пользователя (Theme, OfflineMode и др.). `%LocalAppData%\Ven4Tools\profile.json` |
| `ThemeService` | Применяет teal-тему. Вызвать после `ProfileService.Load()` |
| `InstallationService` | Установка через winget/choco/scoop/прямая ссылка |
| `AppManager` | Менеджер списка приложений + `apps.json` |
| `HeartbeatService` | Пинг heartbeat-сервера, dispose при выходе |
| `CrashReportService` | Запись краш-репортов |
| `UserAppsService` | Пользовательские приложения с сервера |
| `SourceOrderService` | Приоритет источников установки |

## Startup Flow

```
Ven4Tools.exe
└── App_Startup (async void, try/catch/finally)
    ├── LocalizationService.Init()
    ├── ThemeService.Apply(ProfileService.Current.Theme)
    ├── HeartbeatService()
    ├── SplashWindow.Show()
    │   ├── CheckNetwork → PreloadCatalog → CheckAdmin → CheckWebView2 → CheckWinget
    │   └── [кнопка «Пропустить» через CancellationToken]
    ├── MainWindow.Show()
    └── SplashWindow.Close()  ← в finally, всегда
```

## Release Rules

### Версии
- Client: `Ven4Tools/Ven4Tools.csproj` → `<Version>`
- Launcher: `Ven4Tools.Launcher/Ven4Tools.Launcher.csproj` → `<Version>`
- Оба должны совпадать при релизе

### Сборка клиента
```
dotnet publish -c Release -r win-x64 --self-contained true -o _release/client_XYZ
Compress-Archive _release/client_XYZ/* _release/Ven4Tools-Client-X.Y.Z.zip
```

### Сборка лаунчера
```
cd Ven4Tools.Launcher
dotnet publish -c Release -r win-x64 --self-contained true
# → bin/Release/net8.0-windows/win-x64/publish/Ven4Tools.Launcher.exe
Copy → _release/Ven4Tools.Launcher-X.Y.Z.exe
```

### Именование ассетов (актуально с 3.3.0+)
- Клиент: `Ven4Tools-Client-X.Y.Z.zip` (дефисы)
- Лаунчер: `Ven4Tools.Launcher-X.Y.Z.exe` (точка перед Launcher, дефис перед версией)

### GitHub release
```
gh release create vX.Y.Z \
  "_release/Ven4Tools-Client-X.Y.Z.zip" \
  "_release/Ven4Tools.Launcher-X.Y.Z.exe" \
  --title "Ven4Tools X.Y.Z — Название" \
  --notes-file notes.md \
  --latest
```

## Gitignore (не коммитить)
`_release/`, `_backups/`, `_publish_*/`, `bin/`, `obj/`, `Secrets.cs`

## Local Data (`%LocalAppData%\Ven4Tools\`)
| Файл | Назначение |
|---|---|
| `profile.json` | Настройки (Theme="teal", OfflineMode и др.) |
| `session.json` | Сохранённая сессия входа |
| `apps.json` | Состояние чекбоксов |
| `logs/` | Логи установок |

## Roadmap
| Версия | Что |
|---|---|
| 3.4.0 | Пресеты — сохранение + шаринг наборов приложений |
| 3.5.0 | Диагностика (bloatware, startup, PATH) |
| 4.0.0 | Portable + синхронизация пресетов |

## Rules
1. Бэкап перед правками → `_backups\YYYYMMDD_HHMMSS\`
2. `dotnet build` должен давать 0 ошибок и 0 предупреждений
3. Не упоминать AI/Claude в коде, коммитах, release notes
4. Релизы только через `main` (ветки `releases/*` — архив)
