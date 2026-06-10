; ============================================================================
; Ven4Tools.Setup.nsi — установщик Ven4Tools Launcher 2.0
; ============================================================================
; Компиляция (из корня репозитория):
;   makensis /INPUTCHARSET UTF8 /DVERSION=2.0.0 installer\Ven4Tools.Setup.nsi
; Обычно вызывается через build_installer.ps1 — он передаёт VERSION,
; PUBLISH_DIR и OUTFILE автоматически.
;
; Ключевые решения:
;   - Установка в %LOCALAPPDATA%\Ven4Tools\Launcher — не требует прав
;     администратора (RequestExecutionLevel user, без UAC).
;   - Страницы выбора папки НЕТ намеренно: лаунчер 2.0 проверяет, что он
;     запущен именно из этой папки (LauncherUpdateService), путь фиксирован.
;   - Повторный запуск установщика поверх старой версии = тихое обновление:
;     процесс лаунчера закрывается, файлы перезаписываются (SetOverwrite on).
; ============================================================================

Unicode true

; --- Параметры сборки (переопределяются через /D из build_installer.ps1) ---
!ifndef VERSION
  !define VERSION "2.0.0"
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
; Секции установки
; ============================================================================

Section "Лаунчер Ven4Tools (обязательно)" SEC_MAIN
  SectionIn RO

  ; Закрываем работающий лаунчер, чтобы exe не был занят (путь обновления).
  ; nsExec прячет окно консоли taskkill.
  nsExec::Exec 'taskkill /f /im "${EXE_NAME}"'
  Pop $0
  Sleep 500

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${PUBLISH_DIR}\${EXE_NAME}"

  ; Деинсталлятор
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Ярлыки: рабочий стол + меню «Пуск»
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  CreateDirectory "${SM_DIR}"
  CreateShortCut "${SM_DIR}\${APP_NAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  CreateShortCut "${SM_DIR}\Удалить ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe"

  ; Регистрация в «Программы и компоненты» (Add/Remove Programs, HKCU)
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
SectionEnd

Section "Автозапуск при входе в Windows" SEC_AUTORUN
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

  ; Файлы лаунчера
  Delete "$INSTDIR\${EXE_NAME}"
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
