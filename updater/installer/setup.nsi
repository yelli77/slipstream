!define APPNAME "StarTruckMP Updater"
!define COMPANYNAME "StarTruckMP"
!define DESCRIPTION "Installer & Auto-Updater fuer den StarTruckMP Multiplayer-Mod"
!define INSTALLDIR "$LOCALAPPDATA\StarTruckMP"

Unicode true
Name "${APPNAME}"
OutFile "..\..\..\..\..\dist-installer\SlipstreamInstaller.exe"
InstallDir "${INSTALLDIR}"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!include "MUI2.nsh"

!define MUI_ICON "..\icon.ico"
!define MUI_UNICON "..\icon.ico"
!define MUI_WELCOMEPAGE_TITLE "${APPNAME} Setup"
!define MUI_WELCOMEPAGE_TEXT "Dieser Assistent installiert den StarTruckMP Updater, mit dem du den Multiplayer-Mod installierst und aktuell haeltst.$\r$\n$\r$\nDer Updater laedt bei jedem Start automatisch die neueste Mod-Version von GitHub (yelli77/slipstream)."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\StarTruckMPUpdater.exe"
!define MUI_FINISHPAGE_RUN_TEXT "StarTruckMP Updater jetzt starten"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "German"

Section "Install"
    SetOutPath "$INSTDIR"
    File /r "..\bin\Release\net6.0\win-x64\publish\*.*"

    CreateDirectory "$SMPROGRAMS\StarTruckMP"
    CreateShortcut "$SMPROGRAMS\StarTruckMP\StarTruckMP Updater.lnk" "$INSTDIR\StarTruckMPUpdater.exe"
    CreateShortcut "$SMPROGRAMS\StarTruckMP\Uninstall.lnk" "$INSTDIR\Uninstall.exe"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StarTruckMPUpdater" "DisplayName" "${APPNAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StarTruckMPUpdater" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StarTruckMPUpdater" "Publisher" "${COMPANYNAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StarTruckMPUpdater" "InstallLocation" "$INSTDIR"
SectionEnd

Section "Uninstall"
    RMDir /r "$INSTDIR"
    RMDir /r "$SMPROGRAMS\StarTruckMP"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\StarTruckMPUpdater"
SectionEnd
