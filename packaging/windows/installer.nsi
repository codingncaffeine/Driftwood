; Driftwood's Windows installer: per user, no administrator prompt (the game needs none), built on
; Linux by packaging/windows/build-installer.sh from the SAME files as the zip.
;   Installs to %LOCALAPPDATA%\Programs\Driftwood, adds a Start menu shortcut (and a desktop one when
;   ticked), and registers an uninstaller under Apps & Features. Uninstalling removes the program
;   only: worlds, packs and settings in %APPDATA%\Driftwood stay, as they are the player's work.
; Defines given by the build script: VERSION (x.y.z), PAYLOAD (folder holding the zip's files),
; ICON (the game's .ico), OUTFILE.

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user
ManifestDPIAware true

!include "MUI2.nsh"

!define APPNAME "Driftwood"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Driftwood"

Name "${APPNAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "${UNINSTKEY}" "InstallLocation"
BrandingText "${APPNAME} v${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "codingncaffeine"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_NODESC
!define MUI_FINISHPAGE_RUN "$INSTDIR\Driftwood.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Play Driftwood"

!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}\LICENSE.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; The program's files, by name: an upgrade replaces exactly these and an uninstall removes exactly
; these, so nothing else a player put in the folder is ever deleted by a wildcard.
!macro ProgramFiles ACTION
  ${ACTION} "$INSTDIR\Driftwood.exe"
  ${ACTION} "$INSTDIR\SDL3.dll"
  ${ACTION} "$INSTDIR\glfw3.dll"
  ${ACTION} "$INSTDIR\soft_oal.dll"
  ${ACTION} "$INSTDIR\Driftwood.Net.Eos.dll"
  ${ACTION} "$INSTDIR\EOSSDK-Win64-Shipping.dll"
  ${ACTION} "$INSTDIR\eos.txt"
  ${ACTION} "$INSTDIR\LICENSE.txt"
!macroend

Section "Driftwood" SecGame
  SectionIn RO
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${PAYLOAD}\Driftwood.exe"
  File "${PAYLOAD}\SDL3.dll"
  File "${PAYLOAD}\glfw3.dll"
  File "${PAYLOAD}\soft_oal.dll"
  File "${PAYLOAD}\Driftwood.Net.Eos.dll"
  File "${PAYLOAD}\EOSSDK-Win64-Shipping.dll"
  File "${PAYLOAD}\eos.txt"
  File "${PAYLOAD}\LICENSE.txt"
  WriteUninstaller "$INSTDIR\Uninstall Driftwood.exe"

  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\Driftwood.exe"

  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "codingncaffeine"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://github.com/codingncaffeine/Driftwood"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\Driftwood.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall Driftwood.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall Driftwood.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ; Apps & Features shows the size; the payload is a little over 110 MB.
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" 116000
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\Driftwood.exe"
SectionEnd

Section "Uninstall"
  !insertmacro ProgramFiles Delete
  Delete "$INSTDIR\Uninstall Driftwood.exe"
  ; Only when empty: a folder holding anything the player added is left alone.
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd
