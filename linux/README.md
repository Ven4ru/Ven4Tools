# Ven4Tools для Linux (бета)

Минимальная версия клиента для Linux: каталог программ Ven4Tools с установкой через Flatpak (Flathub) и Snap.
Отдельное приложение на Avalonia 11 (.NET 10), клиент Windows не затрагивает.

## Что умеет

- Загружает тот же подписанный каталог `master.json`: сначала ven4tools.ru, затем CDN, затем GitHub.
  Подпись проверяется тем же кодом, что в клиенте Windows (`Ven4Tools/Services/CatalogSignatureVerifier.cs`, подключён ссылкой).
  Каталог с версией ниже уже принятой отвергается (защита от отката).
  Если сети нет, используется сохранённая копия, затем встроенная; подпись проверяется так же.
- Сопоставляет программы каталога с пакетами Linux по `Ven4Tools.Linux/linux-packages.json`.
- Показывает поиск, фильтр по категориям, отметку «установлено» и кнопку «Установить/Удалить».
- Flatpak ставит программы в профиль пользователя (`--user`), пароль не нужен.
  Snap ставит через `pkexec`, при этом появляется системное окно пароля.
- Дистрибутив определяется по `/etc/os-release` и показывается в строке состояния. Бета проверяется на Ubuntu, Linux Mint и Fedora:
  в Mint и Fedora Flatpak уже стоит, Snap нет (в Mint он выключен намеренно) — программы ставятся с Flathub.
- Если в системе нет Flatpak (так на Ubuntu из коробки), кнопка «Включить Flathub» ставит его системным менеджером пакетов
  через `pkexec`: `apt-get`, `dnf`, `zypper` или `pacman` — что есть в дистрибутиве.
- Отмеченные программы ставятся очередью: каждая группа (Flatpak, Snap) одной командой, snap-группа под одним запросом пароля.
- Вкладка «Наборы»: готовые наборы клиента Windows и свои наборы. Для каждого видно, что поставится, что заменяется аналогом и чего под Linux нет.
- Вкладка «Обновления»: `flatpak update` и `snap refresh`, список и кнопка «Обновить всё».
- Избранное, английский интерфейс, экспорт и импорт профиля (архив совместим с клиентом Windows).

## Чего нет

Офлайн-установщики, твики, лаунчер, автообновление самого клиента, значки программ.

## Соответствие пакетов

В `linux-packages.json` 64 программы из 138. Каждый идентификатор проверен 22.10.2026:
Flatpak по Flathub API (`https://flathub.org/api/v2/appstream/<id>`, ответ 200), Snap командой `snap info` на Ubuntu 26.04.
Исходный список кандидатов лежит в `candidates.txt`, результат проверки в `verified.txt`.
Не нашлись: WhatsApp, NAPS2, LM Studio, Cursor, CrystalDiskInfo; у Wireshark и Kdenlive нет snap, у 7-Zip нет ни Flatpak, ни Snap.

Поле `alternative` означает, что под Linux ставится не сама программа, а замена с тем же назначением
(ShareX и Greenshot заменяет Flameshot, Epic и GOG заменяет Heroic, Everything заменяет FSearch, Teams заменяет Teams for Linux, 7-Zip заменяет PeaZip).

## Сборка

```
cd linux/Ven4Tools.Linux
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ../publish/linux-x64
```

Получается один файл `ven4tools` (около 95 МБ, .NET внутри). На машине с Linux ему нужен только графический сеанс.

## Тестовые машины

ВМ Hyper-V `Ubuntu-Ven4Tools` на VenchWork (Ubuntu 26.04, `ssh v4t-linux`).
Перед тестом установки можно откатиться к снимку «Чистая установка 22.10.2026».

Живые сессии (без установки системы), проверено 31.10.2026: Ubuntu 26.04.1, Linux Mint 22.3 Cinnamon,
Fedora Workstation 44. Во всех трёх программа определила дистрибутив, загрузила подписанный каталог
и поставила Audacity с Flathub; на Ubuntu Flatpak сначала поставлен кнопкой «Включить Flathub»
(apt-get через окно пароля). В живой сессии диск для записи маленький: ВМ нужно 12 ГБ памяти, а в Fedora
папку `~/.local/share/flatpak` на время проверки — в tmpfs, иначе Flathub упирается в «Not enough disk space».
