# Пакет AUR `ven4tools-bin`

Готовый `PKGBUILD` для AUR (Arch, Manjaro, EndeavourOS). Ставит собранный файл из релиза GitHub.

Перед отправкой в AUR: подставить `sha256sums` из `SHA256SUMS` релиза, собрать `.SRCINFO`
(`makepkg --printsrcinfo > .SRCINFO`) и отправить в `ssh://aur@aur.archlinux.org/ven4tools-bin.git`.
