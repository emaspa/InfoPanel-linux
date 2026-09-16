Name:           infopanel
Version:        %{app_version}
Release:        1
Summary:        Hardware monitoring dashboards for Linux
License:        GPL-3.0-only AND MIT AND Apache-2.0 AND (LGPL-2.0-only OR GPL-2.0-only) AND OFL-1.1
URL:            https://github.com/emaspa/InfoPanel-linux
Source0:        %{url}/releases/download/v%{version}/infopanel-%{version}-linux-x64.tar.gz
Source1:        stage-package.sh
BuildArch:      x86_64
Requires:       glibc, libgcc, libstdc++, zlib-ng-compat, libicu, fontconfig, libX11, libICE, libSM, libXcursor, libXext, libXi, libXrandr
Suggests:       ffmpeg-free
Suggests:       smartmontools
Suggests:       (pipewire-pulseaudio or pulseaudio)
Suggests:       pulseaudio-utils

# Repackage the self-contained publish output without stripping or rewriting it.
# Private .NET libraries must not become system-wide RPM Provides/Requires.
AutoReqProv:    no
%global debug_package %{nil}
%global __os_install_post %{nil}

%description
Display hardware sensors on desktop overlays, USB LCD panels and web browsers.
Includes the .NET runtime and bundled InfoPanel plugins. Third-party license
notices are included in LICENSES.md.

%prep
%setup -q -n infopanel-%{version}-linux-x64

%build
# Already compiled by packaging/publish.sh in Ubuntu 26.04.

%install
bash %{SOURCE1} "$PWD" "%{buildroot}" usr/share/licenses/infopanel

%post
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload || :
fi
if [ -S /run/udev/control ]; then
    udevadm control --reload-rules || :
fi

%preun
if [ "$1" -eq 0 ] && [ -d /run/systemd/system ]; then
    systemctl disable --now infopanel-smart.timer || :
    systemctl stop infopanel-smart.service || :
fi

%postun
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload || :
fi
if [ -S /run/udev/control ]; then
    udevadm control --reload-rules || :
fi

%files
/opt/infopanel/
/usr/bin/infopanel
/usr/lib/udev/rules.d/99-infopanel.rules
%dir /usr/lib/infopanel
/usr/lib/infopanel/infopanel-smart-dump.sh
/usr/lib/systemd/system/infopanel-smart.service
/usr/lib/systemd/system/infopanel-smart.timer
/usr/share/applications/infopanel.desktop
/usr/share/icons/hicolor/256x256/apps/infopanel.png
%license /usr/share/licenses/infopanel/
