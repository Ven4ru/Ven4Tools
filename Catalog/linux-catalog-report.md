# Проверка каталога Linux — 2026-11-06 (выпуск 7.1.0)

Каталог версии 4, 82 программы. Каждая Flatpak-программа поставлена на чистой системе, проверена `flatpak info` и удалена; Snap-программы сверены с магазином Snap.

| Где | Итог |
|---|---|
| GitHub Actions, Ubuntu (workflow `linux-catalog-check`) | ok — 82, недоступны — 0 |
| ВМ Fedora 44 Workstation, сеть в России | ok — 80, недоступны — 2 |

Разбор двух отказов в Fedora:

- **TeamSpeak** — файл программы, который пакет Flathub докачивает с сервера TeamSpeak, отвечает 404 (проверено ещё и с сервера в РФ). Программа помечена «временно недоступна»; ежедневная проверка вернёт её, когда пакет починят.
- **Lunacy** — файл с сервера icons8 в сети тестового ноутбука качался со скоростью ~0,1 КБ/с; с сервера в РФ тот же файл скачан за 3 секунды (22 МБ/с). Это сеть тестовой машины, программа оставлена доступной.

Во время проверки сайт flathub.org (API) из России не отвечал; проверка брала сведения из репозитория dl.flathub.org, сама установка шла как обычно.

## Все проверенные (GitHub Actions)

| id | Программа | Источник | Статус | Результат | Заметка |
|---|---|---|---|---|---|
| alpaca | Alpaca | Flathub · com.jeffser.Alpaca | ok | install-ok |  |
| anydesk | AnyDesk | Flathub · com.anydesk.Anydesk | ok | install-ok |  |
| audacity | Audacity | Flathub · org.audacityteam.Audacity | ok | install-ok |  |
| bitwarden | Bitwarden | Flathub · com.bitwarden.desktop | ok | install-ok |  |
| bleachbit | BleachBit | Flathub · org.bleachbit.BleachBit | ok | install-ok |  |
| blender | Blender | Flathub · org.blender.Blender | ok | install-ok |  |
| bottles | Bottles | Flathub · com.usebottles.bottles | ok | install-ok |  |
| brave | Brave Browser | Flathub · com.brave.Browser | ok | install-ok |  |
| calibre | Calibre | Flathub · com.calibre_ebook.calibre | ok | install-ok |  |
| czkawka | Czkawka | Flathub · com.github.qarmin.czkawka | ok | install-ok |  |
| dbeaver | DBeaver Community | Flathub · io.dbeaver.DBeaverCommunity | ok | install-ok |  |
| deja-dup | Déjà Dup | Flathub · org.gnome.DejaDup | ok | install-ok |  |
| discord | Discord | Flathub · com.discordapp.Discord | ok | install-ok |  |
| disk-usage | Анализатор дисков | Flathub · org.gnome.baobab | ok | install-ok |  |
| drawio | draw.io | Flathub · com.jgraph.drawio.desktop | ok | install-ok |  |
| easyeffects | Easy Effects | Flathub · com.github.wwmm.easyeffects | ok | install-ok |  |
| element | Element | Flathub · im.riot.Riot | ok | install-ok |  |
| extension-manager | Extension Manager | Flathub · com.mattjakeman.ExtensionManager | ok | install-ok |  |
| filezilla | FileZilla | Flathub · org.filezillaproject.Filezilla | ok | install-ok |  |
| firefox | Mozilla Firefox | Flathub · org.mozilla.firefox | ok | install-ok |  |
| flameshot | Flameshot | Flathub · org.flameshot.Flameshot | ok | install-ok |  |
| flatseal | Flatseal | Flathub · com.github.tchx84.Flatseal | ok | install-ok |  |
| free-download-manager | Free Download Manager | Flathub · org.freedownloadmanager.Manager | ok | install-ok |  |
| freecad | FreeCAD | Flathub · org.freecad.FreeCAD | ok | install-ok |  |
| fsearch | FSearch | Flathub · io.github.cboxdoerfer.FSearch | ok | install-ok |  |
| gear-lever | Gear Lever | Flathub · it.mijorus.gearlever | ok | install-ok |  |
| gimp | GIMP | Flathub · org.gimp.GIMP | ok | install-ok |  |
| github-desktop | GitHub Desktop | Flathub · io.github.shiftey.Desktop | ok | install-ok |  |
| google-chrome | Google Chrome | Flathub · com.google.Chrome | ok | install-ok |  |
| gpt4all | GPT4All | Flathub · io.gpt4all.gpt4all | ok | install-ok |  |
| handbrake | HandBrake | Flathub · fr.handbrake.ghb | ok | install-ok |  |
| heroic | Heroic Games Launcher | Flathub · com.heroicgameslauncher.hgl | ok | install-ok |  |
| impression | Impression | Flathub · io.gitlab.adhami3310.Impression | ok | install-ok |  |
| inkscape | Inkscape | Flathub · org.inkscape.Inkscape | ok | install-ok |  |
| itch | itch.io | Flathub · io.itch.itch | ok | install-ok |  |
| jan | Jan | Flathub · ai.jan.Jan | ok | install-ok |  |
| joplin | Joplin | Flathub · net.cozic.joplin_desktop | ok | install-ok |  |
| kdenlive | Kdenlive | Flathub · org.kde.kdenlive | ok | install-ok |  |
| keepassxc | KeePassXC | Flathub · org.keepassxc.KeePassXC | ok | install-ok |  |
| krita | Krita | Flathub · org.kde.krita | ok | install-ok |  |
| libreoffice | LibreOffice | Flathub · org.libreoffice.LibreOffice | ok | install-ok |  |
| librewolf | LibreWolf | Flathub · io.gitlab.librewolf-community | ok | install-ok |  |
| localsend | LocalSend | Flathub · org.localsend.localsend_app | ok | install-ok |  |
| lunacy | Lunacy | Flathub · com.icons8.Lunacy | ok | install-ok |  |
| lutris | Lutris | Flathub · net.lutris.Lutris | ok | install-ok |  |
| mediainfo | MediaInfo | Flathub · net.mediaarea.MediaInfo | ok | install-ok |  |
| microsoft-edge | Microsoft Edge | Flathub · com.microsoft.Edge | ok | install-ok |  |
| mission-center | Mission Center | Flathub · io.missioncenter.MissionCenter | ok | install-ok |  |
| newelle | Newelle | Flathub · io.github.qwersyk.Newelle | ok | install-ok |  |
| nodejs | Node.js | Snap · node | ok | snap-info-ok |  |
| obs-studio | OBS Studio | Flathub · com.obsproject.Studio | ok | install-ok |  |
| obsidian | Obsidian | Flathub · md.obsidian.Obsidian | ok | install-ok |  |
| onlyoffice | ONLYOFFICE | Flathub · org.onlyoffice.desktopeditors | ok | install-ok |  |
| opera | Opera | Flathub · com.opera.Opera | ok | install-ok |  |
| parabolic | Parabolic | Flathub · org.nickvision.tubeconverter | ok | install-ok |  |
| peazip | PeaZip | Flathub · io.github.peazip.PeaZip | ok | install-ok |  |
| pika-backup | Pika Backup | Flathub · org.gnome.World.PikaBackup | ok | install-ok |  |
| pinta | Pinta | Flathub · com.github.PintaProject.Pinta | ok | install-ok |  |
| postman | Postman | Flathub · com.getpostman.Postman | ok | install-ok |  |
| powershell | PowerShell 7 | Snap · powershell | ok | snap-info-ok |  |
| prism-launcher | Prism Launcher | Flathub · org.prismlauncher.PrismLauncher | ok | install-ok |  |
| protonup-qt | ProtonUp-Qt | Flathub · net.davidotek.pupgui2 | ok | install-ok |  |
| putty | PuTTY | Flathub · uk.org.greenend.chiark.sgtatham.putty | ok | install-ok |  |
| qbittorrent | qBittorrent | Flathub · org.qbittorrent.qBittorrent | ok | install-ok |  |
| rustdesk | RustDesk | Flathub · com.rustdesk.RustDesk | ok | install-ok |  |
| slack | Slack | Flathub · com.slack.Slack | ok | install-ok |  |
| spotify | Spotify | Flathub · com.spotify.Client | ok | install-ok |  |
| steam | Steam | Flathub · com.valvesoftware.Steam | ok | install-ok |  |
| tailscale | Tailscale | Snap · tailscale | ok | snap-info-ok |  |
| teams-for-linux | Teams for Linux | Flathub · com.github.IsmaelMartinez.teams_for_linux | ok | install-ok |  |
| teamspeak | TeamSpeak | Flathub · com.teamspeak.TeamSpeak | ok | install-ok |  |
| telegram | Telegram Desktop | Flathub · org.telegram.desktop | ok | install-ok |  |
| thunderbird | Mozilla Thunderbird | Flathub · org.mozilla.thunderbird_esr | ok | install-ok |  |
| vlc | VLC Media Player | Flathub · org.videolan.VLC | ok | install-ok |  |
| vscode | Visual Studio Code | Flathub · com.visualstudio.code | ok | install-ok |  |
| warehouse | Warehouse | Flathub · io.github.flattool.Warehouse | ok | install-ok |  |
| wireshark | Wireshark | Flathub · org.wireshark.Wireshark | ok | install-ok |  |
| wps-office | WPS Office | Flathub · com.wps.Office | ok | install-ok |  |
| xnview | XnView MP | Flathub · com.xnview.XnViewMP | ok | install-ok |  |
| yandex-browser | Яндекс.Браузер | Flathub · ru.yandex.Browser | ok | install-ok |  |
| zen-browser | Zen Browser | Flathub · app.zen_browser.zen | ok | install-ok |  |
| zoom | Zoom Workplace | Flathub · us.zoom.Zoom | ok | install-ok |  |
