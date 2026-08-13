!define APPNAME "Slipstream Updater"
!define COMPANYNAME "Slipstream"
!define DESCRIPTION "Installer & Auto-Updater fuer den StarTruckMP Multiplayer-Mod (Slipstream)"
!define INSTALLDIR "$LOCALAPPDATA\Slipstream"

Unicode true
Name "${APPNAME}"
OutFile "..\..\..\..\..\dist-installer\SlipstreamInstaller.exe"
InstallDir "${INSTALLDIR}"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!include "MUI2.nsh"

!define MUI_ICON "..\icon.ico"
!define MUI_UNICON "..\icon.ico"
!define MUI_WELCOMEPAGE_TITLE "Slipstream Updater Setup"
!define MUI_WELCOMEPAGE_TEXT "Dieser Assistent installiert den Slipstream Updater, mit dem du den StarTruckMP Multiplayer-Mod installierst und aktuell haeltst.$\r$\n$\r$\nDer Updater laedt bei jedem Start automatisch die neueste Mod-Version von GitHub (yelli77/slipstream)."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Slipstream.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Slipstream Updater jetzt starten"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "German"

Section "Install"
    SetOutPath "$INSTDIR"
    File /r "..\bin\Release\net6.0\win-x64\publish\*.*"

    CreateDirectory "$SMPROGRAMS\Slipstream"
    CreateShortcut "$SMPROGRAMS\Slipstream\Slipstream Updater.lnk" "$INSTDIR\Slipstream.exe"
    CreateShortcut "$SMPROGRAMS\Slipstream\Uninstall.lnk" "$INSTDIR\Uninstall.exe"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "DisplayName" "${APPNAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "Publisher" "${COMPANYNAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "InstallLocation" "$INSTDIR"
SectionEnd

Section "Uninstall"
    RMDir /r "$INSTDIR"
    RMDir /r "$SMPROGRAMS\Slipstream"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater"
SectionEnd
