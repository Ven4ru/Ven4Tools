; ============================================================================
; Ven4Tools.Setup.nsi — установщик Ven4Tools Launcher
; ============================================================================
; Компиляция (из корня репозитория):
;   makensis /INPUTCHARSET UTF8 /DVERSION=2.1.0 installer\Ven4Tools.Setup.nsi
; Обычно вызывается через build_installer.ps1 — он передаёт VERSION,
; PUBLISH_DIR и OUTFILE автоматически.
;
; Ключевые решения:
;   - Установка в %LOCALAPPDATA%\Ven4Tools\Launcher — не требует прав
;     администратора (RequestExecutionLevel user, без UAC).
;   - Страницы выбора папки НЕТ намеренно: лаунчер проверяет, что он
;     запущен именно из этой папки (LauncherUpdateService), путь фиксирован.
;   - Повторный запуск установщика поверх старой версии = тихое обновление:
;     процесс лаунчера закрывается, файлы перезаписываются (SetOverwrite on).
;
; Режим тихого самообновления (запускается самим лаунчером):
;   Ven4Tools.Setup-X.Y.Z.exe /S /UPDATE /WAITPID=<pid> /RELAUNCH
;     /UPDATE   — обновить уже установленную копию: дождаться завершения
;                 лаунчера, сделать бэкап exe, поставить новую версию,
;                 проверить её и при неудаче откатить бэкап;
;     /WAITPID= — PID процесса лаунчера, завершения которого нужно дождаться;
;     /RELAUNCH — запустить лаунчер после установки (или после отката).
;   В этом режиме НЕ создаются ярлыки и НЕ трогаются автозапуск,
;   настройки и каталог клиента — обновляется только exe лаунчера,
;   деинсталлятор и версия в «Программы и компоненты».
;   Коды возврата: 0 — успех; 3 — установка не удалась, выполнен откат;
;   4 — версия нового exe не совпала с ожидаемой; 5 — файл лаунчера занят.
; ============================================================================

Unicode true

; --- Параметры сборки (переопределяются через /D из build_installer.ps1) ---
!ifndef VERSION
  !define VERSION "2.1.0"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\Ven4Tools.Launcher\bin\Release\net8.0-windows\win-x64\publish"
!endif
!ifndef OUTFILE
  !define OUTFILE "..\_release\Ven4Tools.Setup-${VERSION}.exe"
!endif

; --- Константы приложения ---
!define APP_NAME    "Ven4Tools Launcher"
!define EXE_NAME    "Ven4Tools.Launcher.exe"
!define CLIENT_EXE  "Ven4Tools.exe"
!define PUBLISHER   "Ven4ru"
!define SITE_URL    "https://ven4tools.ru"
!define REPO_URL    "https://github.com/Ven4ru/Ven4Tools"
!define UNINST_KEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\Ven4Tools"
!define RUN_KEY     "Software\Microsoft\Windows\CurrentVersion\Run"
; Имя значения автозапуска совпадает с тем, что пишет сам лаунчер
; (MainWindow.Settings.cs → SetAutostart), чтобы галка в трее и установщик
; управляли одной и той же записью реестра.
!define RUN_VALUE   "Ven4Tools.Launcher"
!define DATA_DIR    "$LOCALAPPDATA\Ven4Tools"
!define SM_DIR      "$SMPROGRAMS\Ven4Tools"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Ven4Tools\Launcher"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
BrandingText "Ven4Tools — ven4tools.ru"

; --- Метаданные exe-файла установщика ---
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName"     "${APP_NAME}"
VIAddVersionKey /LANG=1049 "ProductVersion"  "${VERSION}"
VIAddVersionKey /LANG=1049 "FileVersion"     "${VERSION}.0"
VIAddVersionKey /LANG=1049 "FileDescription" "Установщик ${APP_NAME}"
VIAddVersionKey /LANG=1049 "CompanyName"     "${PUBLISHER}"
VIAddVersionKey /LANG=1049 "LegalCopyright"  "© ${PUBLISHER}"

; ============================================================================
; Интерфейс (Modern UI 2)
; ============================================================================
!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "..\Ven4Tools.Launcher\icon.ico"
!define MUI_UNICON "..\Ven4Tools.Launcher\icon.ico"
!define MUI_ABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "Установка ${APP_NAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Мастер установит ${APP_NAME} — бесплатный установщик программ для Windows.$\r$\n$\r$\nЛаунчер будет установлен в профиль пользователя и не требует прав администратора.$\r$\n$\r$\nНажмите «Далее» для продолжения."

; Страницы установщика
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE_NAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

; Страницы деинсталлятора
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Russian"

; ============================================================================
; Параметры командной строки (режим самообновления)
; ============================================================================

Var UpdateMode   ; "1" — тихое самообновление (/UPDATE)
Var WaitPid      ; PID процесса лаунчера, завершения которого ждём (/WAITPID=)
Var Relaunch     ; "1" — перезапустить лаунчер после установки (/RELAUNCH)

Function .onInit
  StrCpy $UpdateMode "0"
  StrCpy $WaitPid ""
  StrCpy $Relaunch "0"

  ${GetParameters} $R0

  ClearErrors
  ${GetOptions} $R0 "/UPDATE" $R1
  IfErrors no_update_flag
  StrCpy $UpdateMode "1"
  SetSilent silent
  no_update_flag:

  ClearErrors
  ${GetOptions} $R0 "/WAITPID=" $R1
  IfErrors no_waitpid_flag
  StrCpy $WaitPid $R1
  no_waitpid_flag:

  ClearErrors
  ${GetOptions} $R0 "/RELAUNCH" $R1
  IfErrors no_relaunch_flag
  StrCpy $Relaunch "1"
  no_relaunch_flag:

  ; PID принимается только целым числом — мусор в аргументах игнорируется
  ; (значение подставляется в командную строку tasklist).
  StrCmp $WaitPid "" pid_checked
  IntOp $R2 $WaitPid + 0
  StrCmp $R2 $WaitPid pid_checked
  StrCpy $WaitPid ""
  pid_checked:
FunctionEnd

; ============================================================================
; Общие функции
; ============================================================================

; Деинсталлятор + регистрация в «Программы и компоненты» (HKCU).
; Вызывается и при обычной установке, и при тихом самообновлении —
; версия в реестре всегда соответствует установленному exe.
Function WriteUninstallRegistry
  WriteUninstaller "$INSTDIR\uninstall.exe"

  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName"          "${APP_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKCU "${UNINST_KEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon"          "$INSTDIR\${EXE_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString"      '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegStr   HKCU "${UNINST_KEY}" "URLInfoAbout"         "${SITE_URL}"
  WriteRegStr   HKCU "${UNINST_KEY}" "HelpLink"             "${REPO_URL}"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify"             1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair"             1

  ; Реальный размер установки в КБ → EstimatedSize
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
FunctionEnd

; Ожидание завершения процесса лаунчера с PID из /WAITPID= (до 60 секунд).
; По таймауту процесс закрывается принудительно — как при обычной установке.
Function WaitForLauncherExit
  StrCmp $WaitPid "" wait_done
  StrCpy $R2 0
  wait_loop:
    ; find возвращает 0, пока процесс с этим PID и именем лаунчера жив
    nsExec::ExecToStack 'cmd /c tasklist /FI "PID eq $WaitPid" /FI "IMAGENAME eq ${EXE_NAME}" /NH /FO CSV | find "$WaitPid"'
    Pop $R3
    Pop $R4
    StrCmp $R3 "0" 0 wait_done
    IntOp $R2 $R2 + 1
    IntCmp $R2 60 wait_timeout 0 wait_timeout
    Sleep 1000
    Goto wait_loop
  wait_timeout:
    DetailPrint "Лаунчер не завершился за 60 секунд — закрываем принудительно"
    nsExec::Exec 'taskkill /f /im "${EXE_NAME}"'
    Pop $R3
    Sleep 500
  wait_done:
FunctionEnd

; Запуск установленного лаунчера, если передан /RELAUNCH.
; Вызывается и при успехе, и после отката — пользователь не остаётся без лаунчера.
Function RelaunchInstalled
  StrCmp $Relaunch "1" 0 relaunch_skip
  IfFileExists "$INSTDIR\${EXE_NAME}" 0 relaunch_skip
  Exec '"$INSTDIR\${EXE_NAME}"'
  relaunch_skip:
FunctionEnd

; Тихое самообновление установленной копии: бэкап → установка → проверка →
; при неудаче откат. Ярлыки, автозапуск, настройки и каталог клиента не трогаются.
Function UpdateInstall
  DetailPrint "Тихое самообновление ${APP_NAME} до версии ${VERSION}"
  InitPluginsDir

  ; 1. Новый exe во временную папку установщика + контроль его версии
  File "/oname=$PLUGINSDIR\${EXE_NAME}" "${PUBLISH_DIR}\${EXE_NAME}"
  ${GetFileVersion} "$PLUGINSDIR\${EXE_NAME}" $R5
  StrCmp $R5 "${VERSION}.0" version_ok
    DetailPrint "Версия нового exe ($R5) не совпадает с ожидаемой (${VERSION}.0) — отмена"
    SetErrorLevel 4
    Call RelaunchInstalled
    Quit
  version_ok:

  ; 2. Ждём завершения процесса лаунчера, запустившего обновление
  Call WaitForLauncherExit

  ; 3. Бэкап текущего exe. Rename не проходит, пока файл занят другим
  ;    процессом — это одновременно и проверка разблокировки (до 15 попыток).
  IfFileExists "$INSTDIR\${EXE_NAME}" 0 backup_done
  Delete "$INSTDIR\${EXE_NAME}.bak"
  StrCpy $R2 0
  backup_retry:
    ClearErrors
    Rename "$INSTDIR\${EXE_NAME}" "$INSTDIR\${EXE_NAME}.bak"
    IfErrors 0 backup_done
    IntOp $R2 $R2 + 1
    IntCmp $R2 15 backup_failed 0 backup_failed
    Sleep 1000
    Goto backup_retry
  backup_failed:
    DetailPrint "Файл лаунчера занят — обновление отменено, старая версия сохранена"
    SetErrorLevel 5
    Call RelaunchInstalled
    Quit
  backup_done:

  ; 4. Установка нового exe в папку установки
  CreateDirectory "$INSTDIR"
  ClearErrors
  CopyFiles /SILENT "$PLUGINSDIR\${EXE_NAME}" "$INSTDIR"
  IfErrors update_rollback

  ; 5. Проверка результата: файл на месте и его версия совпадает с ожидаемой
  IfFileExists "$INSTDIR\${EXE_NAME}" 0 update_rollback
  ${GetFileVersion} "$INSTDIR\${EXE_NAME}" $R5
  StrCmp $R5 "${VERSION}.0" update_ok update_rollback

  update_ok:
  ; 6. Успех: удаляем бэкап, обновляем деинсталлятор и версию в реестре
  Delete "$INSTDIR\${EXE_NAME}.bak"
  Call WriteUninstallRegistry
  DetailPrint "Обновление до ${VERSION} завершено"
  Call RelaunchInstalled
  Return

  update_rollback:
  ; 7. Неудача: возвращаем бэкап на место и запускаем старую версию
  DetailPrint "Установка новой версии не удалась — откат на предыдущую"
  Delete "$INSTDIR\${EXE_NAME}"
  IfFileExists "$INSTDIR\${EXE_NAME}.bak" 0 rollback_done
  Rename "$INSTDIR\${EXE_NAME}.bak" "$INSTDIR\${EXE_NAME}"
  rollback_done:
  SetErrorLevel 3
  Call RelaunchInstalled
  Quit
FunctionEnd

; ============================================================================
; Секции установки
; ============================================================================

Section "Лаунчер Ven4Tools (обязательно)" SEC_MAIN
  SectionIn RO

  ; Режим самообновления: только замена exe с бэкапом и откатом.
  StrCmp $UpdateMode "1" 0 normal_install
  Call UpdateInstall
  Goto section_done

  normal_install:
  ; Закрываем работающий лаунчер, чтобы exe не был занят (переустановка поверх).
  ; nsExec прячет окно консоли taskkill.
  nsExec::Exec 'taskkill /f /im "${EXE_NAME}"'
  Pop $0
  Sleep 500

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${PUBLISH_DIR}\${EXE_NAME}"

  ; Ярлыки: рабочий стол + меню «Пуск» (только при обычной установке —
  ; тихое самообновление не воссоздаёт удалённые пользователем ярлыки)
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  CreateDirectory "${SM_DIR}"
  CreateShortCut "${SM_DIR}\${APP_NAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  CreateShortCut "${SM_DIR}\Удалить ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe"

  ; Деинсталлятор + регистрация в «Программы и компоненты»
  Call WriteUninstallRegistry

  section_done:
SectionEnd

Section /o "Автозапуск при входе в Windows" SEC_AUTORUN
  ; Та же запись, которой управляет галка «Запускать при старте Windows»
  ; в трее лаунчера — конфликтов не будет.
  WriteRegStr HKCU "${RUN_KEY}" "${RUN_VALUE}" '"$INSTDIR\${EXE_NAME}"'
SectionEnd

; --- Описания секций на странице компонентов ---
LangString DESC_SecMain    ${LANG_RUSSIAN} "Файлы лаунчера, ярлыки и регистрация в «Программы и компоненты». Обязательный компонент."
LangString DESC_SecAutorun ${LANG_RUSSIAN} "Запускать лаунчер автоматически при входе в Windows (значок в трее). Можно изменить позже в настройках лаунчера."

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN}    $(DESC_SecMain)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTORUN} $(DESC_SecAutorun)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ============================================================================
; Деинсталляция
; ============================================================================

Section "Uninstall"
  ; Закрываем лаунчер и клиент, чтобы файлы и папки не были заняты
  nsExec::Exec 'taskkill /f /im "${EXE_NAME}"'
  Pop $0
  nsExec::Exec 'taskkill /f /im "${CLIENT_EXE}"'
  Pop $0
  Sleep 500

  ; Файлы лаунчера (включая возможный бэкап незавершённого самообновления)
  Delete "$INSTDIR\${EXE_NAME}"
  Delete "$INSTDIR\${EXE_NAME}.bak"
  Delete "$INSTDIR\uninstall.exe"

  ; Ярлыки
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "${SM_DIR}\${APP_NAME}.lnk"
  Delete "${SM_DIR}\Удалить ${APP_NAME}.lnk"
  RMDir "${SM_DIR}"

  ; Реестр: автозапуск + запись в «Программы и компоненты»
  DeleteRegValue HKCU "${RUN_KEY}" "${RUN_VALUE}"
  DeleteRegKey   HKCU "${UNINST_KEY}"

  ; Папка установки (удалится, только если пуста — лишнего не трогаем)
  RMDir "$INSTDIR"

  ; Пользовательские данные — спрашиваем явно.
  ; При тихой деинсталляции (/S) по умолчанию НЕ удаляем (/SD IDNO).
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "Удалить пользовательские данные?$\r$\n$\r$\nБудут удалены: клиент Ven4Tools, логи, настройки и сохранённая сессия.$\r$\nПапка: ${DATA_DIR}" \
    /SD IDNO IDNO skip_data
    RMDir /r "${DATA_DIR}"
  skip_data:
SectionEnd
