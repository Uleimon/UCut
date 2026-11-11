; UCut installer (NSIS). Built with: makensis -DVERSION=x.y.z -DSOURCE=<publish folder> installer/UCut.nsi
; Installs for the current user (no admin rights), with start menu and desktop shortcut and a normal uninstaller.
Unicode true
!include "MUI2.nsh"

!ifndef VERSION
  !define VERSION "0.3.0"
!endif
!ifndef SOURCE
  !define SOURCE "..\publish\UCut"
!endif
!ifndef OUTFILE
  !define OUTFILE "..\publish\UCut-Setup-${VERSION}.exe"
!endif

Name "UCut"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\UCut"
InstallDirRegKey HKCU "Software\UCut" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "UCut ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "UCut"
VIAddVersionKey "FileDescription" "UCut Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Uleimon"

!define MUI_ICON "..\src\UCut.Desktop\Assets\ucut.ico"
!define MUI_UNICON "..\src\UCut.Desktop\Assets\ucut.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "UCut ${VERSION} installieren"
!define MUI_WELCOMEPAGE_TEXT "UCut macht aus deinen Clips fertige Shorts: Schnitt, Untertitel, Logo, Titel und Hashtags. Mit Galerie, Schnittraum und Twitch-Import.$\r$\n$\r$\nAdmin-Rechte sind nicht nötig."
!define MUI_FINISHPAGE_RUN "$INSTDIR\UCut.exe"
!define MUI_FINISHPAGE_RUN_TEXT "UCut jetzt starten"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!define MUI_COMPONENTSPAGE_NODESC
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "German"

!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\UCut"

Section "UCut" SecMain
  SectionIn RO
  ; A running UCut would lock its files.
  nsExec::Exec 'taskkill /IM UCut.exe /F'
  nsExec::Exec 'taskkill /IM ucut-cli.exe /F'
  Sleep 1000
  ; A new version replaces the old one completely: the program folder is emptied first, so no old files stay behind.
  ; Settings, accounts, clips and downloaded tools live elsewhere (%APPDATA%\UCut, %LOCALAPPDATA%\UCut, Videos) and stay.
  IfFileExists "$INSTDIR\UCut.exe" 0 +2
    RMDir /r "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${SOURCE}\*.*"
  WriteUninstaller "$INSTDIR\Deinstallieren.exe"

  CreateDirectory "$SMPROGRAMS\UCut"
  CreateShortcut "$SMPROGRAMS\UCut\UCut.lnk" "$INSTDIR\UCut.exe" "" "$INSTDIR\UCut.exe" 0
  CreateShortcut "$SMPROGRAMS\UCut\UCut deinstallieren.lnk" "$INSTDIR\Deinstallieren.exe"

  WriteRegStr HKCU "Software\UCut" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "UCut"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "Uleimon"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\UCut.exe"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Deinstallieren.exe"'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; "Öffnen mit → UCut" for videos.
  WriteRegStr HKCU "Software\Classes\Applications\UCut.exe\shell\open\command" "" '"$INSTDIR\UCut.exe" "%1"'
  WriteRegStr HKCU "Software\Classes\Applications\UCut.exe\SupportedTypes" ".mp4" ""
  WriteRegStr HKCU "Software\Classes\Applications\UCut.exe\SupportedTypes" ".mkv" ""
  WriteRegStr HKCU "Software\Classes\Applications\UCut.exe\SupportedTypes" ".mov" ""
SectionEnd

Section "Verknüpfung auf dem Desktop" SecDesktop
  CreateShortcut "$DESKTOP\UCut.lnk" "$INSTDIR\UCut.exe" "" "$INSTDIR\UCut.exe" 0
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /IM UCut.exe /F'
  Delete "$DESKTOP\UCut.lnk"
  RMDir /r "$SMPROGRAMS\UCut"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\UCut"
  DeleteRegKey HKCU "Software\Classes\Applications\UCut.exe"
  ; Settings (%APPDATA%\UCut) and your clips stay.
SectionEnd
