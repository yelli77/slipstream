!define APPNAME "Slipstream"
!define COMPANYNAME "Slipstream"
!define DESCRIPTION "Installer & auto-updater for the StarTruckMP multiplayer mod (Slipstream)"
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
!define MUI_WELCOMEPAGE_TITLE "Slipstream Setup"
!define MUI_WELCOMEPAGE_TEXT "This wizard will install Slipstream, which installs and keeps the StarTruckMP multiplayer mod up to date.$\r$\n$\r$\nSlipstream automatically downloads the latest mod version from GitHub (yelli77/slipstream) every time it starts."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Slipstream.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch Slipstream now"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
    SetOutPath "$INSTDIR"
    File /r "..\bin\Release\net6.0-windows\win-x64\publish\*.*"

    CreateDirectory "$SMPROGRAMS\Slipstream"
    CreateShortcut "$SMPROGRAMS\Slipstream\Slipstream.lnk" "$INSTDIR\Slipstream.exe"
    CreateShortcut "$SMPROGRAMS\Slipstream\Uninstall.lnk" "$INSTDIR\Uninstall.exe"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "DisplayName" "${APPNAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "Publisher" "${COMPANYNAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater" "InstallLocation" "$INSTDIR"
SectionEnd

Section "Uninstall"
    ; Gespeicherten Spielpfad aus updater-config.txt lesen (von Slipstream.exe geschrieben),
    ; um auch die dort installierten BepInEx/Doorstop-Dateien mit aufzuraeumen. Ohne das blieben
    ; BepInEx-Ordner, dotnet-Ordner, winhttp.dll etc. dauerhaft im Spielordner liegen.
    ClearErrors
    FileOpen $0 "$INSTDIR\updater-config.txt" r
    IfErrors skip_game_cleanup
    FileRead $0 $1
    FileClose $0
    ; Nur loeschen, wenn der Pfad plausibel noch das Spiel enthaelt (Sicherheitscheck gegen
    ; eine leere/kaputte config-Datei, die sonst versehentlich woanders aufraeumen wuerde).
    IfFileExists "$1\Star Trucker.exe" 0 skip_game_cleanup
        RMDir /r "$1\BepInEx"
        RMDir /r "$1\dotnet"
        Delete "$1\winhttp.dll"
        Delete "$1\doorstop_config.ini"
        Delete "$1\.doorstop_version"
        Delete "$1\changelog.txt"
    skip_game_cleanup:

    RMDir /r "$INSTDIR"
    RMDir /r "$SMPROGRAMS\Slipstream"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SlipstreamUpdater"
SectionEnd
