# Уведомления о сторонних компонентах

Ven4Tools распространяется в виде самодостаточных (self-contained) сборок,
которые включают среду выполнения .NET 10 и перечисленные ниже сторонние
пакеты NuGet. Их лицензии требуют сохранения уведомлений об авторских правах
при распространении, поэтому оригинальные тексты приведены здесь.

Данный файл относится только к компонентам, реально попадающим в дистрибутив
(клиент `Ven4Tools.exe` и лаунчер `Ven4Tools.Launcher.exe`). Пакеты, которые
используются исключительно в тестовых проектах (`FlaUI`, `xunit`, `MSTest`,
`FsCheck`, `coverlet` и т. п.), в дистрибутив не входят
и здесь не перечисляются.

Собственная лицензия Ven4Tools — см. файл [`LICENSE`](LICENSE).

---

## Клиент (Ven4Tools.exe)

| Компонент | Версия | Лицензия |
|-----------|--------|----------|
| Newtonsoft.Json | 13.0.4 | MIT |
| System.Management | 10.0.12 | MIT |
| System.ServiceProcess.ServiceController | 10.0.12 | MIT |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 |
| HidSharp | 2.6.4 | Apache-2.0 |
| Mono.Posix.NETStandard | 1.0.0 | MIT |
| System.IO.Ports, System.Threading.AccessControl, System.IO.FileSystem.AccessControl, Microsoft.Win32.Registry, System.Memory | 10.x / 5.x / 4.x | MIT |
| Среда выполнения .NET 10 (self-contained) | 10.x | MIT |

## Лаунчер (Ven4Tools.Launcher.exe)

| Компонент | Версия | Лицензия |
|-----------|--------|----------|
| Newtonsoft.Json | 13.0.4 | MIT |
| Среда выполнения .NET 10 (self-contained) | 10.x | MIT |

---

## Тексты лицензий

### MIT License

Под лицензией MIT распространяются: Newtonsoft.Json, Mono.Posix.NETStandard, а также компоненты .NET
(System.Management, System.ServiceProcess.ServiceController) и сама среда выполнения .NET 10.

**Newtonsoft.Json** — Copyright (c) 2007 James Newton-King

**Компоненты .NET и среда выполнения .NET** — Copyright (c) .NET Foundation and Contributors

Стандартный текст лицензии MIT:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Mozilla Public License 2.0

Под лицензией MPL-2.0 распространяются библиотеки датчиков: LibreHardwareMonitorLib
(Copyright (c) LibreHardwareMonitor and Contributors, исходный код —
https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), DiskInfoToolkit и
RAMSPDToolkit-NDD (Copyright (c) Florian K., исходный код —
https://github.com/Blacktempel/DiskInfoToolkit и https://github.com/Blacktempel/RAMSPDToolkit).
Ven4Tools использует их без изменений, в виде отдельных сборок. Полный текст лицензии —
https://mozilla.org/MPL/2.0/.

### Apache License 2.0

Под лицензией Apache-2.0 распространяется HidSharp (Copyright 2010-2025 James F. Bellinger,
https://software.seekye.com/hidsharp) — зависимость LibreHardwareMonitorLib, используется
без изменений. Полный текст лицензии — https://www.apache.org/licenses/LICENSE-2.0.

### Драйвер PawnIO

Драйвер датчиков PawnIO (namazso, GPL-2.0 или новее, https://github.com/namazso/PawnIO) в
дистрибутив Ven4Tools не входит: по кнопке пользователя он устанавливается через winget
из официального пакета `namazso.PawnIO`. Ven4Tools обращается к нему только через
стандартный интерфейс драйвера (DeviceIoControl).
