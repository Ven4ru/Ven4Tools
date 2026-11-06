#!/usr/bin/env bash
# Сборка пакетов Linux-версии: .tar.gz, .deb, .rpm и SHA256SUMS.
# Запуск (Linux или WSL, нужны dpkg-deb, rpmbuild, fakeroot):
#   linux/packaging/build-packages.sh <папка с ven4tools> <версия> <куда сложить>
# Пример: linux/packaging/build-packages.sh linux/publish/linux-x64 7.1.0 /tmp/out
set -euo pipefail

PUB="$1"; VER="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(date +%s)}"

# --- .tar.gz: исполняемый файл, README, ярлык и значок (их же берёт spec для .rpm)
NAME="ven4tools-$VER"
mkdir -p "$WORK/$NAME"
install -m755 "$PUB/ven4tools" "$WORK/$NAME/ven4tools"
install -m644 "$HERE/ven4tools.desktop" "$HERE/ven4tools.png" "$WORK/$NAME/"
cat > "$WORK/$NAME/README.txt" <<EOF
Ven4Tools для Linux $VER (бета)

Запуск:  ./ven4tools
Нужен графический сеанс. Программы ставятся с Flathub (Flatpak) и из Snap.
Если Flatpak нет (Ubuntu из коробки), нажмите «Включить Flathub» — система спросит пароль.
Каталог программ подписан, каждая программа ежедневно проверяется установкой.
Проверено: Ubuntu 26.04, Linux Mint 22.3, Fedora 44.
Пакеты: .deb (Ubuntu, Mint, Debian), .rpm (Fedora), COPR: dnf copr enable ven4dev/ven4tools
Сайт: https://ven4tools.ru
EOF
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$SOURCE_DATE_EPOCH" \
    -C "$WORK" -czf "$OUT/Ven4Tools-Linux-$VER.tar.gz" "$NAME"
cp "$OUT/Ven4Tools-Linux-$VER.tar.gz" "$OUT/Ven4Tools-Linux-beta.tar.gz"

# --- .deb
D="$WORK/deb"
install -Dm755 "$PUB/ven4tools" "$D/opt/ven4tools/ven4tools"
install -Dm644 "$HERE/ven4tools.desktop" "$D/usr/share/applications/ven4tools.desktop"
install -Dm644 "$HERE/ven4tools.png" "$D/usr/share/icons/hicolor/256x256/apps/ven4tools.png"
mkdir -p "$D/usr/bin" "$D/DEBIAN"
ln -s /opt/ven4tools/ven4tools "$D/usr/bin/ven4tools"
SIZE=$(du -sk "$D/opt" "$D/usr" | awk '{s+=$1} END {print s}')
cat > "$D/DEBIAN/control" <<EOF
Package: ven4tools
Version: $VER
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $SIZE
Depends: libc6, libfontconfig1, libicu80 | libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70
Recommends: flatpak
Maintainer: Ven4Tools <ven4ru@users.noreply.github.com>
Homepage: https://ven4tools.ru
Description: Program catalog with one-click Flathub installs (beta)
 Ven4Tools for Linux: popular programs from Flathub (and Snap where
 available) in one click, ready-made sets, updates, system info and
 cleanup, autostart and desktop personalization with Venchik.
 The catalog is signed and every entry is checked by a real install daily.
EOF
fakeroot dpkg-deb --build -Zxz "$D" "$OUT/ven4tools_${VER}_amd64.deb" >/dev/null

# --- .rpm (из того же архива и spec, что и в COPR)
R="$WORK/rpm"
mkdir -p "$R"/{SOURCES,SPECS,BUILD,RPMS,SRPMS}
cp "$OUT/Ven4Tools-Linux-$VER.tar.gz" "$R/SOURCES/"
sed "s/^Version:.*/Version:        $VER/" "$HERE/rpm/ven4tools.spec" > "$R/SPECS/ven4tools.spec"
rpmbuild --quiet --define "_topdir $R" --define "dist %{nil}" -ba "$R/SPECS/ven4tools.spec" >/dev/null
cp "$R"/RPMS/x86_64/*.rpm "$OUT/"
cp "$R"/SRPMS/*.src.rpm "$OUT/"

cd "$OUT"
sha256sum Ven4Tools-Linux-"$VER".tar.gz ven4tools_"$VER"_amd64.deb ven4tools-"$VER"-1.x86_64.rpm > SHA256SUMS
cat SHA256SUMS
