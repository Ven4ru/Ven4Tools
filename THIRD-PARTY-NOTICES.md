# Уведомления о сторонних компонентах

Ven4Tools распространяется в виде самодостаточных (self-contained) сборок,
которые включают среду выполнения .NET 8 и перечисленные ниже сторонние
пакеты NuGet. Их лицензии требуют сохранения уведомлений об авторских правах
при распространении, поэтому оригинальные тексты приведены здесь.

Данный файл относится только к компонентам, реально попадающим в дистрибутив
(клиент `Ven4Tools.exe` и лаунчер `Ven4Tools.Launcher.exe`). Пакеты, которые
используются исключительно в тестовых проектах (`FlaUI`, `xunit`, `MSTest`,
`FsCheck`, `coverlet`, `SixLabors.ImageSharp` и т. п.), в дистрибутив не входят
и здесь не перечисляются.

Собственная лицензия Ven4Tools — см. файл [`LICENSE`](LICENSE).

---

## Клиент (Ven4Tools.exe)

| Компонент | Версия | Лицензия |
|-----------|--------|----------|
| Newtonsoft.Json | 13.0.4 | MIT |
| Microsoft.Web.WebView2 | 1.0.4078.44 | Microsoft Software License Terms |
| Microsoft.Extensions.DependencyInjection | 10.0.10 | MIT |
| System.Management | 10.0.10 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.10 | MIT |
| System.ServiceProcess.ServiceController | 10.0.10 | MIT |
| Среда выполнения .NET 8 (self-contained) | 8.x | MIT |

## Лаунчер (Ven4Tools.Launcher.exe)

| Компонент | Версия | Лицензия |
|-----------|--------|----------|
| Newtonsoft.Json | 13.0.4 | MIT |
| Среда выполнения .NET 8 (self-contained) | 8.x | MIT |

---

## Тексты лицензий

### MIT License

Под лицензией MIT распространяются: Newtonsoft.Json, а также компоненты .NET
(Microsoft.Extensions.DependencyInjection, System.Management,
System.Security.Cryptography.ProtectedData, System.ServiceProcess.ServiceController)
и сама среда выполнения .NET 8.

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

### Microsoft.Web.WebView2

Copyright (c) Microsoft Corporation.

Компонент распространяется на условиях «Microsoft Software License Terms»
(Microsoft Edge WebView2), допускающих распространение в составе приложений.
Полный текст лицензии поставляется вместе с пакетом NuGet
`Microsoft.Web.WebView2` (файл лицензии внутри пакета) и доступен на официальной
странице пакета: <https://www.nuget.org/packages/Microsoft.Web.WebView2>.
