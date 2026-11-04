Unicode True
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

!ifndef MSI_PATH
  !error "MSI_PATH is required"
!endif
!ifndef PACKAGE_PATH
  !error "PACKAGE_PATH is required"
!endif
!ifndef MIGRATION_SCRIPTS
  !error "MIGRATION_SCRIPTS is required"
!endif
!ifndef APP_VERSION
  !error "APP_VERSION is required"
!endif
!ifndef APP_VERSION4
  !error "APP_VERSION4 is required"
!endif
!ifndef RUNTIME
  !error "RUNTIME is required"
!endif
!ifndef OUT_FILE
  !error "OUT_FILE is required"
!endif
!ifndef MSI_SHA256
  !error "MSI_SHA256 is required"
!endif
!ifndef PACKAGE_SHA256
  !error "PACKAGE_SHA256 is required"
!endif

!define APP_NAME "Toastify Reloaded"
!define APP_KEY "Software\ToastifyReloaded"
!define PACKAGE_NAME "ToastifyReloaded-${APP_VERSION}-${RUNTIME}-full.nupkg"
Name "${APP_NAME}"
OutFile "${OUT_FILE}"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
RequestExecutionLevel admin
; MSI and nupkg payloads are already compressed; avoid an expensive solid
; recompression pass for a small size saving in this one-time bridge wrapper.
SetCompressor zlib
BrandingText "${APP_NAME}"
ShowInstDetails show
VIProductVersion "${APP_VERSION4}"
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "MarliusLab"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} Setup (${RUNTIME})"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Toastify Reloaded contributors"
!define MUI_ABORTWARNING
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipUpdateWizardPage
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipUpdateWizardPage
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Italian"

Var UpdatePid
Var IsUpdate
Var RestartMinimized
Var InstallMutex

Function .onInit
!if "${RUNTIME}" == "win-arm64"
    ReadEnvStr $R0 "PROCESSOR_ARCHITEW6432"
    ${If} $R0 != "ARM64"
        ReadEnvStr $R0 "PROCESSOR_ARCHITECTURE"
    ${EndIf}
    ${If} $R0 != "ARM64"
        MessageBox MB_OK|MB_ICONSTOP "Questo pacchetto richiede Windows ARM64. Su PC Intel o AMD scarica ToastifyReloaded-Setup-win-x64.exe."
        SetErrorLevel 1633
        Abort
    ${EndIf}
!endif
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Global\ToastifyReloaded.PackageTransition") p .r0 ?e'
    Pop $1
    StrCpy $InstallMutex $0
    ${If} $0 == 0
    ${OrIf} $1 == 183
        MessageBox MB_OK|MB_ICONSTOP "Un altro aggiornamento di Toastify Reloaded e gia in corso, oppure Windows non consente di verificarlo. Chiudi l'altro installer e riprova."
        SetErrorLevel 1618
        Abort
    ${EndIf}
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
    ${If} ${Errors}
        ; GetOptions clears its output when the option is absent. Restore the
        ; manual-launch default before validating; otherwise .onInit exits.
        StrCpy $RestartMinimized "0"
        ClearErrors
    ${EndIf}
    ${If} $RestartMinimized != "0"
    ${AndIf} $RestartMinimized != "1"
        MessageBox MB_OK|MB_ICONSTOP "Opzione di riavvio non valida. Avvia il setup senza opzioni aggiuntive."
        SetErrorLevel 87
        Abort
    ${EndIf}
    ${If} $UpdatePid != ""
        StrCpy $IsUpdate "1"
        SetAutoClose true
    ${EndIf}
FunctionEnd

Function .onGUIEnd
    ${If} $InstallMutex != 0
        System::Call 'kernel32::CloseHandle(p $InstallMutex)'
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
    ; Same legacy protocol: validate the exact PID and wait without killing it.
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
    DetailPrint "Attendo la chiusura di ${APP_NAME} (PID $UpdatePid)..."
    System::Call 'kernel32::OpenProcess(i 0x00100000, i 0, i $UpdatePid) p .r0 ?e'
    Pop $1
    ${If} $0 != 0
        System::Call 'kernel32::WaitForSingleObject(p r0, i 60000) i .r1'
        System::Call 'kernel32::CloseHandle(p r0)'
        ${If} $1 == 258
            SetErrorLevel 1460
            Abort "Toastify Reloaded non si e chiuso entro 60 secondi. Nessun file modificato."
        ${ElseIf} $1 != 0
            SetErrorLevel 5
            Abort "Impossibile attendere la chiusura di Toastify Reloaded."
        ${EndIf}
    ${ElseIf} $1 != 87
        SetErrorLevel 5
        Abort "Impossibile verificare la chiusura di Toastify Reloaded."
    ${EndIf}
FunctionEnd

Function CheckApplicationStopped
    ; Same session-local mutex as App.OnStartup. Do not create it: holding a
    ; new application mutex through this helper would block the final restart.
    ; This is a fail-closed preflight, not a lock against a later manual launch.
    System::Call 'kernel32::OpenMutexW(i 0x00100001, i 0, w "ToastifyReloaded.SingleInstance") p .r0 ?e'
    Pop $1
    ${If} $0 == 0
        ${If} $1 == 2
            Return
        ${EndIf}
        SetErrorLevel 5
        Abort "Impossibile verificare se Toastify Reloaded e chiuso. Chiudilo dal menu dell'icona nell'area di notifica e riprova."
    ${EndIf}
    System::Call 'kernel32::WaitForSingleObject(p r0, i 0) i .r1'
    ${If} $1 == 0
    ${OrIf} $1 == 128
        ; The mutex was unowned/abandoned. Release our temporary ownership now.
        System::Call 'kernel32::ReleaseMutex(p r0)'
        System::Call 'kernel32::CloseHandle(p r0)'
        Return
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
    SetErrorLevel 5
    Abort "Toastify Reloaded e ancora in esecuzione. Scegli Esci dal menu dell'icona nell'area di notifica e riprova. Nessun file modificato."
FunctionEnd

Section "${APP_NAME}"
    Call WaitForUpdatingProcess
    Call CheckApplicationStopped
    ; All helper files live outside current and are removed by NSIS itself.
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=Transition.msi "${MSI_PATH}"
    File /oname=${PACKAGE_NAME} "${PACKAGE_PATH}"
    File "${MIGRATION_SCRIPTS}\WindowsPackageMigration.ps1"
    File "${MIGRATION_SCRIPTS}\WindowsPackageMigration.psm1"
    File "${MIGRATION_SCRIPTS}\WindowsDesktopUserLauncher.cs"
    DetailPrint "Installazione della versione ${APP_VERSION}; impostazioni e avvio automatico vengono conservati..."
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\WindowsPackageMigration.ps1" -InstallRoot "$INSTDIR" -MsiPath "$PLUGINSDIR\Transition.msi" -PackagePath "$PLUGINSDIR\${PACKAGE_NAME}" -Version "${APP_VERSION}" -Runtime "${RUNTIME}" -MsiSha256 "${MSI_SHA256}" -PackageSha256 "${PACKAGE_SHA256}" -RestartMinimized "$RestartMinimized"'
    Pop $0
    ${If} $0 == 10
        SetAutoClose false
        DetailPrint "Installazione completata. Riapri Toastify Reloaded dal menu Start."
        MessageBox MB_OK|MB_ICONINFORMATION "Installazione completata. L'avvio automatico non e riuscito: apri Toastify Reloaded dal menu Start. Le impostazioni sono state conservate."
    ${ElseIf} $0 != 0
        SetErrorLevel 1
        Abort "Aggiornamento non completato. Consulta i dettagli; le impostazioni non sono state rimosse."
    ${EndIf}
    SetErrorLevel 0
SectionEnd
