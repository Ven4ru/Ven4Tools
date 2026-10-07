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
  !define PUBLISH_DIR "..\Ven4Tools.Launcher\bin\Release\net10.0-windows\win-x64\publish"
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
; Выбор языка — общий для установщика, лаунчера и клиента ("ru" или "en").
!define LANG_KEY    "Software\Ven4Tools"
!define LANG_VALUE  "Language"
!ifndef LANG_PACK_EN
  !define LANG_PACK_EN "..\Localization\packs\launcher-en.json"
!endif
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
VIAddVersionKey /LANG=1033 "ProductName"     "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion"  "${VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion"     "${VERSION}.0"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey /LANG=1033 "CompanyName"     "${PUBLISHER}"
VIAddVersionKey /LANG=1033 "LegalCopyright"  "© ${PUBLISHER}"

; ============================================================================
; Интерфейс (Modern UI 2)
; ============================================================================
!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "..\Ven4Tools.Launcher\icon.ico"
!define MUI_UNICON "..\Ven4Tools.Launcher\icon.ico"
!define MUI_ABORTWARNING

; Тексты страниц — языковыми строками (см. «Тексты на двух языках» ниже):
; установщик говорит на языке, выбранном в первом окне.
!define MUI_WELCOMEPAGE_TITLE "$(TEXT_WelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT "$(TEXT_WelcomeText)"

; Окно выбора языка. Подписи сразу на двух языках: до выбора язык ещё неизвестен.
!define MUI_LANGDLL_WINDOWTITLE "Язык / Language"
!define MUI_LANGDLL_INFO "Выберите язык установщика и программы.$\r$\nChoose the language for setup and the app."

; Страницы установщика
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE_NAME}"
!define MUI_FINISHPAGE_RUN_TEXT "$(TEXT_FinishRun)"
!insertmacro MUI_PAGE_FINISH

; Страницы деинсталлятора
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Свою .onGUIInit объявить нельзя — её определяет сам MUI2. Штатная точка
; расширения: MUI_CUSTOMFUNCTION_GUIINIT, MUI вызовет её из своей .onGUIInit.
; Определение обязано идти ДО MUI_LANGUAGE, сама функция описана ниже, после секций.
!define MUI_CUSTOMFUNCTION_GUIINIT Ven4GuiInit

; Русский идёт первым: он язык исходных текстов. Какой язык показать, решает .onInit.
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_RESERVEFILE_LANGDLL

; ============================================================================
; Тексты на двух языках
; ============================================================================
LangString TEXT_WelcomeTitle ${LANG_RUSSIAN} "Установка ${APP_NAME} ${VERSION}"
LangString TEXT_WelcomeTitle ${LANG_ENGLISH} "${APP_NAME} ${VERSION} Setup"
LangString TEXT_WelcomeText  ${LANG_RUSSIAN} "Мастер установит ${APP_NAME} — бесплатный установщик программ для Windows.$\r$\n$\r$\nЛаунчер будет установлен в профиль пользователя и не требует прав администратора.$\r$\n$\r$\nНажмите «Далее» для продолжения."
LangString TEXT_WelcomeText  ${LANG_ENGLISH} "This wizard will install ${APP_NAME}, a free app installer for Windows.$\r$\n$\r$\nThe launcher is installed into your user profile and does not need administrator rights.$\r$\n$\r$\nClick Next to continue."
LangString TEXT_FinishRun    ${LANG_RUSSIAN} "Запустить ${APP_NAME}"
LangString TEXT_FinishRun    ${LANG_ENGLISH} "Launch ${APP_NAME}"

LangString NAME_SecMain      ${LANG_RUSSIAN} "Лаунчер Ven4Tools (обязательно)"
LangString NAME_SecMain      ${LANG_ENGLISH} "Ven4Tools Launcher (required)"
LangString NAME_SecAutorun   ${LANG_RUSSIAN} "Автозапуск при входе в Windows"
LangString NAME_SecAutorun   ${LANG_ENGLISH} "Start when you sign in to Windows"
LangString NAME_SecWinget    ${LANG_RUSSIAN} "Установить Winget (рекомендуется)"
LangString NAME_SecWinget    ${LANG_ENGLISH} "Install Winget (recommended)"
LangString NAME_SecWingetHas ${LANG_RUSSIAN} "Установить Winget (уже установлен — будет пропущено)"
LangString NAME_SecWingetHas ${LANG_ENGLISH} "Install Winget (already installed — will be skipped)"
LangString NAME_SecChoco     ${LANG_RUSSIAN} "Установить Chocolatey (опционально)"
LangString NAME_SecChoco     ${LANG_ENGLISH} "Install Chocolatey (optional)"

; Имя ярлыка удаления в меню «Пуск». Деинсталлятор убирает оба варианта.
LangString NAME_UninstallLink ${LANG_RUSSIAN} "Удалить ${APP_NAME}"
LangString NAME_UninstallLink ${LANG_ENGLISH} "Uninstall ${APP_NAME}"

LangString TEXT_RemoveData ${LANG_RUSSIAN} "Удалить пользовательские данные?$\r$\n$\r$\nБудут удалены: логи, настройки и сохранённая сессия.$\r$\nПапка: ${DATA_DIR}$\r$\n$\r$\nПапка клиента Ven4Tools, если она находится вне этой папки (по умолчанию — в «Документах»), не удаляется — её можно удалить кнопкой «Удалить клиент» в лаунчере до деинсталляции."
LangString TEXT_RemoveData ${LANG_ENGLISH} "Delete user data?$\r$\n$\r$\nThis removes logs, settings and the saved session.$\r$\nFolder: ${DATA_DIR}$\r$\n$\r$\nThe Ven4Tools client folder, if it is outside this folder (by default it is in Documents), is not removed — you can remove it with the “Uninstall client” button in the launcher before uninstalling."

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

  ; Язык. Сохранённый выбор (прошлая установка или настройки программ) важнее
  ; языка системы. Без выбора: русская Windows — русский, любая другая —
  ; английский (сам NSIS для незнакомого языка взял бы первый из списка, русский).
  ; Затем — окно выбора языка; в тихом режиме (/S, самообновление) его нет.
  Call ResolveLanguage
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Function ResolveLanguage
  ReadRegStr $R1 HKCU "${LANG_KEY}" "${LANG_VALUE}"
  StrCmp $R1 "en" language_english
  StrCmp $R1 "ru" language_russian
  StrCmp $LANGUAGE ${LANG_RUSSIAN} language_done language_english
  language_russian:
    StrCpy $LANGUAGE ${LANG_RUSSIAN}
    Goto language_done
  language_english:
    StrCpy $LANGUAGE ${LANG_ENGLISH}
  language_done:
FunctionEnd

; Английский языковой пакет лаунчера: ставится, только если выбран английский.
; Русский встроен в саму программу. Пакет лежит рядом с exe (lang\en.json), и
; лаунчер принимает его, только если пакет собран вместе с этим самым exe.
Function InstallLanguagePack
  StrCmp $LANGUAGE ${LANG_ENGLISH} 0 language_pack_remove
    SetOutPath "$INSTDIR\lang"
    SetOverwrite on
    File "/oname=en.json" "${LANG_PACK_EN}"
    SetOutPath "$INSTDIR"
    Goto language_pack_done
  language_pack_remove:
    Delete "$INSTDIR\lang\en.json"
    RMDir "$INSTDIR\lang"
  language_pack_done:
FunctionEnd

; ----------------------------------------------------------------------------
; Обнаружение установленного winget. Результат — "1"/"0" в $R3.
;
; Два независимых признака, как и в самом лаунчере:
;   1. псевдоним %LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe — есть на
;      обычной машине, но его может не быть сразу после установки winget (до
;      перезахода в систему) или когда псевдонимы отключены в параметрах Windows;
;   2. пакет Microsoft.DesktopAppInstaller_* в реестре развёртывания AppX —
;      читается обычным пользователем и не зависит от псевдонима.
; Каталог Program Files\WindowsApps не трогаем: перечислять его установщику,
; работающему без прав администратора, Windows не даёт.
; ----------------------------------------------------------------------------
Function IsWingetInstalled
  Push $0
  Push $1
  Push $2
  StrCpy $R3 "0"

  IfFileExists "$LOCALAPPDATA\Microsoft\WindowsApps\winget.exe" 0 check_registry
    StrCpy $R3 "1"
    Goto winget_detect_done

  check_registry:
  StrCpy $0 0
  registry_loop:
    EnumRegKey $1 HKCU       "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages" $0
    StrCmp $1 "" winget_detect_done
    ; 30 символов — длина префикса "Microsoft.DesktopAppInstaller_"
    StrCpy $2 $1 30
    StrCmp $2 "Microsoft.DesktopAppInstaller_" 0 registry_next
      StrCpy $R3 "1"
      Goto winget_detect_done
    registry_next:
    IntOp $0 $0 + 1
    Goto registry_loop

  winget_detect_done:
  Pop $2
  Pop $1
  Pop $0
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
    ; Именно процесс с этим PID, а не все по имени образа: иначе заодно убивался
    ; бы и экземпляр, запущенный пользователем уже после старта обновления, —
    ; возможно, посреди установки клиента. Такой экземпляр держит exe, и шаг
    ; бэкапа ниже штатно завершится кодом 5 без потери старой версии.
    nsExec::Exec 'taskkill /f /pid $WaitPid'
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

  ; 2а. Прошлое обновление оборвалось между переименованиями (отключение питания):
  ;     exe нет, а его копия лежит в .bak — возвращаем её, иначе шаг бэкапа ниже
  ;     сочтёт, что бэкапить нечего, и удалит единственную рабочую версию.
  IfFileExists "$INSTDIR\${EXE_NAME}" restore_done
  IfFileExists "$INSTDIR\${EXE_NAME}.bak" 0 restore_done
  Rename "$INSTDIR\${EXE_NAME}.bak" "$INSTDIR\${EXE_NAME}"
  restore_done:

  ; 2б. Новый exe кладём в папку установки под временным именем ДО того, как
  ;     трогать рабочий. Раньше 48 МБ копировались уже после переименования
  ;     старого exe в .bak, и обрыв посреди копирования оставлял лаунчер вовсе
  ;     без exe (ярлык в никуда). Теперь долгое копирование идёт, пока старый
  ;     exe ещё на месте, а сама замена — два переименования на одном томе.
  CreateDirectory "$INSTDIR"
  Delete "$INSTDIR\${EXE_NAME}.new"
  ClearErrors
  Rename "$PLUGINSDIR\${EXE_NAME}" "$INSTDIR\${EXE_NAME}.new"
  IfErrors 0 stage_ok
    DetailPrint "Не удалось подготовить новый exe в папке установки — обновление отменено"
    Delete "$INSTDIR\${EXE_NAME}.new"
    SetErrorLevel 3
    Call RelaunchInstalled
    Quit
  stage_ok:

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
    Delete "$INSTDIR\${EXE_NAME}.new"
    SetErrorLevel 5
    Call RelaunchInstalled
    Quit
  backup_done:

  ; 4. Подготовленный exe — на рабочее имя (переименование на том же томе)
  ClearErrors
  Rename "$INSTDIR\${EXE_NAME}.new" "$INSTDIR\${EXE_NAME}"
  IfErrors update_rollback

  ; 5. Проверка результата: файл на месте и его версия совпадает с ожидаемой
  IfFileExists "$INSTDIR\${EXE_NAME}" 0 update_rollback
  ${GetFileVersion} "$INSTDIR\${EXE_NAME}" $R5
  StrCmp $R5 "${VERSION}.0" update_ok update_rollback

  update_ok:
  ; 6. Успех: удаляем бэкап, обновляем деинсталлятор и версию в реестре
  Delete "$INSTDIR\${EXE_NAME}.bak"
  ; Языковой пакет меняется вместе с exe: прежний пакет новой сборке не подходит.
  Call InstallLanguagePack
  Call WriteUninstallRegistry
  DetailPrint "Обновление до ${VERSION} завершено"
  Call RelaunchInstalled
  Return

  update_rollback:
  ; 7. Неудача: возвращаем бэкап на место и запускаем старую версию
  DetailPrint "Установка новой версии не удалась — откат на предыдущую"
  Delete "$INSTDIR\${EXE_NAME}.new"
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

Section "$(NAME_SecMain)" SEC_MAIN
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

  ; Язык: пакет перевода — по выбранному языку. Сам выбор запоминается, только если
  ; его сделал человек: тихая установка (/S) оставляет «как в системе».
  Call InstallLanguagePack
  IfSilent language_choice_done
  StrCmp $LANGUAGE ${LANG_ENGLISH} 0 +3
    WriteRegStr HKCU "${LANG_KEY}" "${LANG_VALUE}" "en"
    Goto language_choice_done
  WriteRegStr HKCU "${LANG_KEY}" "${LANG_VALUE}" "ru"
  language_choice_done:

  ; Удаляем задания от предыдущей незавершённой установки. Выбранные ниже
  ; опциональные секции создадут актуальные одноразовые маркеры заново.
  Delete "$INSTDIR\install-winget.pending"
  Delete "$INSTDIR\install-chocolatey.pending"

  ; Ярлыки: рабочий стол + меню «Пуск» (только при обычной установке —
  ; тихое самообновление не воссоздаёт удалённые пользователем ярлыки)
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  CreateDirectory "${SM_DIR}"
  CreateShortCut "${SM_DIR}\${APP_NAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  ; Ярлык удаления — на выбранном языке; вариант на другом языке мог остаться от прошлой установки.
  Delete "${SM_DIR}\Удалить ${APP_NAME}.lnk"
  Delete "${SM_DIR}\Uninstall ${APP_NAME}.lnk"
  CreateShortCut "${SM_DIR}\$(NAME_UninstallLink).lnk" "$INSTDIR\uninstall.exe"

  ; Деинсталлятор + регистрация в «Программы и компоненты»
  Call WriteUninstallRegistry

  section_done:
SectionEnd

Section /o "$(NAME_SecAutorun)" SEC_AUTORUN
  ; Та же запись, которой управляет галка «Запускать при старте Windows»
  ; в трее лаунчера — конфликтов не будет.
  WriteRegStr HKCU "${RUN_KEY}" "${RUN_VALUE}" '"$INSTDIR\${EXE_NAME}"'
SectionEnd

Section "$(NAME_SecWinget)" SEC_WINGET
  ; Компонент устанавливает launcher после первого запуска: Winget является
  ; MSIX/App Installer и требует собственной логики регистрации.
  StrCmp $UpdateMode "1" winget_section_done
  FileOpen $0 "$INSTDIR\install-winget.pending" w
  FileWrite $0 "1"
  FileClose $0
  winget_section_done:
SectionEnd

Section /o "$(NAME_SecChoco)" SEC_CHOCO
  ; Launcher запросит UAC только для установки Chocolatey.
  StrCmp $UpdateMode "1" choco_section_done
  FileOpen $0 "$INSTDIR\install-chocolatey.pending" w
  FileWrite $0 "1"
  FileClose $0
  choco_section_done:
SectionEnd

; Состояние галок на странице компонентов. Именно .onGUIInit, а не .onInit:
; NSIS разбирает скрипт линейно, и в .onInit индекс ${SEC_WINGET} ещё не определён
; (секции идут ниже по файлу). В тихом режиме самообновления графики нет и эта
; функция не вызывается — там секции и так ничего не делают (проверка $UpdateMode).
Function Ven4GuiInit
  ; Winget уже стоит — снимаем галку и говорим об этом прямо на странице компонентов.
  ; Раньше проверки не было вообще: секция всегда была отмечена, и страница выглядела
  ; так, будто winget надо ставить заново, — при том что её же описание обещало
  ; «если Winget уже установлен, действие будет безопасно пропущено» (пропускал его
  ; лаунчер, но уже после установки и молча). Секцию не блокируем, но и отмеченная
  ; она ничего не переустановит: лаунчер при установленном winget шаг пропускает.
  Call IsWingetInstalled
  StrCmp $R3 "1" 0 winget_check_done
  SectionSetFlags ${SEC_WINGET} 0
  SectionSetText  ${SEC_WINGET} "$(NAME_SecWingetHas)"
  winget_check_done:
FunctionEnd

; --- Описания секций на странице компонентов ---
LangString DESC_SecMain    ${LANG_RUSSIAN} "Файлы лаунчера, ярлыки и регистрация в «Программы и компоненты». Обязательный компонент."
LangString DESC_SecAutorun ${LANG_RUSSIAN} "Запускать лаунчер автоматически при входе в Windows (значок в трее). Можно изменить позже в настройках лаунчера."
LangString DESC_SecWinget  ${LANG_RUSSIAN} "Установить Winget после первого запуска launcher. Если Winget уже установлен, действие будет безопасно пропущено."
LangString DESC_SecChoco   ${LANG_RUSSIAN} "Установить Chocolatey как дополнительный источник программ. Потребуется отдельное подтверждение UAC."
LangString DESC_SecMain    ${LANG_ENGLISH} "Launcher files, shortcuts and the entry in Apps & features. Required."
LangString DESC_SecAutorun ${LANG_ENGLISH} "Start the launcher automatically when you sign in to Windows (tray icon). You can change this later in the launcher settings."
LangString DESC_SecWinget  ${LANG_ENGLISH} "Install Winget after the launcher starts for the first time. If Winget is already installed, this step is safely skipped."
LangString DESC_SecChoco   ${LANG_ENGLISH} "Install Chocolatey as an additional app source. A separate UAC confirmation will be required."

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN}    $(DESC_SecMain)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTORUN} $(DESC_SecAutorun)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_WINGET}  $(DESC_SecWinget)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CHOCO}   $(DESC_SecChoco)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ============================================================================
; Деинсталляция
; ============================================================================

; Деинсталлятор говорит на том же языке, что и программы: выбор читается из реестра.
Function un.onInit
  ReadRegStr $R1 HKCU "${LANG_KEY}" "${LANG_VALUE}"
  StrCmp $R1 "en" un_language_english
  StrCmp $R1 "ru" un_language_russian
  StrCmp $LANGUAGE ${LANG_RUSSIAN} un_language_done un_language_english
  un_language_russian:
    StrCpy $LANGUAGE ${LANG_RUSSIAN}
    Goto un_language_done
  un_language_english:
    StrCpy $LANGUAGE ${LANG_ENGLISH}
  un_language_done:
FunctionEnd

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
  Delete "$INSTDIR\install-winget.pending"
  Delete "$INSTDIR\install-chocolatey.pending"
  Delete "$INSTDIR\lang\en.json"
  RMDir  "$INSTDIR\lang"

  ; Ярлыки
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "${SM_DIR}\${APP_NAME}.lnk"
  Delete "${SM_DIR}\Удалить ${APP_NAME}.lnk"
  Delete "${SM_DIR}\Uninstall ${APP_NAME}.lnk"
  RMDir "${SM_DIR}"

  ; Реестр: автозапуск, запись в «Программы и компоненты», выбор языка
  DeleteRegValue HKCU "${RUN_KEY}" "${RUN_VALUE}"
  DeleteRegKey   HKCU "${UNINST_KEY}"
  DeleteRegValue HKCU "${LANG_KEY}" "${LANG_VALUE}"
  DeleteRegKey /ifempty HKCU "${LANG_KEY}"

  ; Папка установки (удалится, только если пуста — лишнего не трогаем)
  RMDir "$INSTDIR"

  ; Пользовательские данные — спрашиваем явно.
  ; При тихой деинсталляции (/S) по умолчанию НЕ удаляем (/SD IDNO).
  ; Клиент по умолчанию ставится в «Документы» (или в папку, выбранную в лаунчере),
  ; а не в ${DATA_DIR} — обещать его удаление здесь нельзя: удаляется только то,
  ; что лежит внутри этой папки (клиент — лишь при старой раскладке рядом с лаунчером).
  MessageBox MB_YESNO|MB_ICONQUESTION "$(TEXT_RemoveData)" /SD IDNO IDNO skip_data
    RMDir /r "${DATA_DIR}"
  skip_data:
SectionEnd
