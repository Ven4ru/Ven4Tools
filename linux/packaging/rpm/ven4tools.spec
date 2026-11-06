%global debug_package %{nil}
%global __strip /bin/true

Name:           ven4tools
Version:        7.1.0
Release:        1%{?dist}
Summary:        Program catalog with one-click Flathub installs (beta)
License:        MIT
URL:            https://ven4tools.ru
Source0:        https://github.com/Ven4ru/Ven4Tools/releases/download/linux-v%{version}/Ven4Tools-Linux-%{version}.tar.gz
ExclusiveArch:  x86_64
Requires:       libicu
Requires:       fontconfig
Recommends:     flatpak

%description
Ven4Tools for Linux (beta): a catalog of popular programs installed from
Flathub (and Snap where available) in one click, ready-made sets, updates,
system info and cleanup, autostart, and desktop personalization with Venchik.
The catalog is signed and every entry is checked by a real install daily.

%prep
%setup -q -n ven4tools-%{version}

%build

%install
install -Dm755 ven4tools %{buildroot}/opt/ven4tools/ven4tools
install -Dm644 ven4tools.desktop %{buildroot}%{_datadir}/applications/ven4tools.desktop
install -Dm644 ven4tools.png %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/ven4tools.png
install -d %{buildroot}%{_bindir}
ln -s /opt/ven4tools/ven4tools %{buildroot}%{_bindir}/ven4tools

%files
%dir /opt/ven4tools
/opt/ven4tools/ven4tools
%{_bindir}/ven4tools
%{_datadir}/applications/ven4tools.desktop
%{_datadir}/icons/hicolor/256x256/apps/ven4tools.png

%changelog
* Fri Nov 06 2026 Ven4Tools <ven4ru@users.noreply.github.com> - 7.1.0-1
- Отдельный каталог Linux с проверкой установкой, интерфейс как у клиента Windows
