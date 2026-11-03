Unicode True

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

!ifndef APP_EXE
  !error "APP_EXE is required"
!endif
!ifndef APP_VERSION
  !error "APP_VERSION is required"
!endif
!ifndef APP_VERSION4
  !error "APP_VERSION4 is required"
!endif
!ifndef OUT_FILE
  !error "OUT_FILE is required"
!endif
!ifndef RUNTIME
  !define RUNTIME "win-x64"
!endif

!define APP_NAME "Toastify Reloaded"
!define APP_ID "ToastifyReloaded"
!define APP_EXE_NAME "ToastifyReloaded.exe"
!define PUBLISHER "Marlius92"
!define PROJECT_URL "https://github.com/Marlius92/Toastify-Reloaded"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
!define APP_KEY "Software\${APP_ID}"

Name "${APP_NAME}"
OutFile "${OUT_FILE}"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP_NAME}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${APP_VERSION4}"
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} Setup (${RUNTIME})"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Toastify Reloaded contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE_NAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Avvia ${APP_NAME}"

!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipUpdateWizardPage
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipUpdateWizardPage
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipUpdateWizardPage
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "Italian"

Var UpdatePid
Var IsUpdate
Var RestartMinimized

Function .onInit
    ; Registry commands must run inside a Function/Section. Select the 64-bit
    ; view here and restore the existing install folder explicitly. NSIS
    ; InstallDirRegKey does not honor SetRegView, so ReadRegStr is used instead.
    SetRegView 64
    ReadRegStr $R2 HKLM "${APP_KEY}" "InstallDir"
    ${If} $R2 != ""
        StrCpy $INSTDIR $R2
    ${EndIf}

    StrCpy $IsUpdate "0"
    StrCpy $UpdatePid ""
    StrCpy $RestartMinimized "0"

    ${GetParameters} $R0
    ${GetOptions} $R0 "/UPDATEPID=" $UpdatePid
    ${GetOptions} $R0 "/RESTARTMINIMIZED=" $RestartMinimized
    ${If} $UpdatePid != ""
        StrCpy $IsUpdate "1"
        SetAutoClose true
    ${EndIf}
FunctionEnd

Function SkipUpdateWizardPage
    ${If} $IsUpdate == "1"
        Abort
    ${EndIf}
FunctionEnd

Function WaitForUpdatingProcess
    ${If} $IsUpdate != "1"
        Return
    ${EndIf}

    ; Canonical unsigned decimal must round-trip within a DWORD. This rejects
    ; signs, trailing text, hexadecimal, zero and overflow before OpenProcess.
    StrLen $0 $UpdatePid
    ${If} $0 > 10
        SetErrorLevel 87
        Abort "PID di aggiornamento non valido. Nessun file modificato."
    ${EndIf}
    IntFmt $0 "%u" $UpdatePid
    ${If} $0 != $UpdatePid
    ${OrIf} $0 == "0"
        SetErrorLevel 87
        Abort "PID di aggiornamento non valido. Nessun file modificato."
    ${EndIf}

    ; Wait after the progress page opens, never during .onInit/UAC handoff.
    ; Use a native process handle; command-line values are never executable code.
    DetailPrint "Attendo la chiusura di ${APP_NAME} (PID $UpdatePid)..."
    System::Call 'kernel32::OpenProcess(i 0x00100000, i 0, i $UpdatePid) p .r0 ?e'
    Pop $1
    ${If} $0 != 0
        System::Call 'kernel32::WaitForSingleObject(p r0, i 60000) i .r1'
        System::Call 'kernel32::CloseHandle(p r0)'
        ${If} $1 == 258
            SetErrorLevel 1460
            Abort "Toastify Reloaded non si e chiuso entro 60 secondi. Aggiornamento annullato."
        ${ElseIf} $1 != 0
            SetErrorLevel 5
            Abort "Impossibile attendere la chiusura di Toastify Reloaded. Aggiornamento annullato."
        ${EndIf}
    ${ElseIf} $1 != 87
        ; ERROR_INVALID_PARAMETER means the process already exited. Any other
        ; failure cannot prove the executable is unlocked, so stop safely.
        SetErrorLevel 5
        Abort "Impossibile verificare la chiusura di Toastify Reloaded. Aggiornamento annullato."
    ${EndIf}
    DetailPrint "Applicazione chiusa. Installazione della versione ${APP_VERSION}..."
FunctionEnd

Section "${APP_NAME}" SecMain
    Call WaitForUpdatingProcess
    SetShellVarContext all
    SetRegView 64
    SetOutPath "$INSTDIR"

    ; Clean leftovers from old portable/package layouts without touching user data.
    RMDir /r "$INSTDIR\scripts"
    Delete "$INSTDIR\ToastifyModern.exe"

    File /oname=${APP_EXE_NAME} "${APP_EXE}"
    WriteUninstaller "$INSTDIR\Uninstall.exe"

    CreateDirectory "$SMPROGRAMS\${APP_NAME}"
    CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE_NAME}"
    CreateShortcut "$SMPROGRAMS\${APP_NAME}\Disinstalla ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

    WriteRegStr HKLM "${APP_KEY}" "InstallDir" "$INSTDIR"
    WriteRegStr HKLM "${APP_KEY}" "Version" "${APP_VERSION}"

    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "URLInfoAbout" "${PROJECT_URL}"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" '"$INSTDIR\${APP_EXE_NAME}"'
    WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1

    ; Preserve the user's Start-with-Windows choice while correcting an old
    ; portable path to the newly installed executable.
    ReadRegStr $R1 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ToastifyReloaded"
    ${If} $R1 != ""
        WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ToastifyReloaded" '"$INSTDIR\${APP_EXE_NAME}" --minimized'
    ${EndIf}

    ; In-app updates show installation progress, skip the wizard pages and
    ; restart here. Normal installations use the Finish-page checkbox instead.
    ${If} $IsUpdate == "1"
        ${If} $RestartMinimized == "1"
            Exec '"$INSTDIR\${APP_EXE_NAME}" --minimized'
        ${Else}
            Exec '"$INSTDIR\${APP_EXE_NAME}"'
        ${EndIf}
    ${EndIf}
SectionEnd

Section "Uninstall"
    SetShellVarContext all
    SetRegView 64

    ; Stop Toastify if it is still running so files can be removed cleanly.
    nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /IM ${APP_EXE_NAME} /F'
    Sleep 300

    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ToastifyReloaded"
    DeleteRegKey HKLM "${UNINSTALL_KEY}"
    DeleteRegKey HKLM "${APP_KEY}"

    Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\${APP_NAME}\Disinstalla ${APP_NAME}.lnk"
    RMDir "$SMPROGRAMS\${APP_NAME}"

    Delete "$INSTDIR\${APP_EXE_NAME}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"

    ; User preferences in %APPDATA% are intentionally preserved, so reinstalling
    ; or upgrading does not erase hotkeys, popup settings or compatibility state.
SectionEnd
