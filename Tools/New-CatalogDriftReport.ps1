<#
.SYNOPSIS
    Строит markdown-отчёт о расхождениях каталога по результатам Verify-CatalogDownloads.ps1.

.DESCRIPTION
    Verify-CatalogDownloads.ps1 скачивает каждый установщик с прямой ссылки
    downloadUrl и складывает построчный результат в report.json, но сам не
    решает, что считать проблемой, и всегда завершается с кодом 0.

    Этот скрипт сравнивает фактические данные из report.json с тем, что
    зафиксировано в Catalog/master.json (sha256 / version / size), и формирует
    человекочитаемый отчёт для issue.

    Скрипт НИЧЕГО не правит в каталоге: обновление master.json и переподписание
    каталога приватным ключом — ручная операция владельца через ven4admin
    и Tools/CatalogSigner.

.PARAMETER ReportPath
    Путь к report.json, который оставил Verify-CatalogDownloads.ps1.

.PARAMETER CatalogPath
    Путь к Catalog/master.json.

.PARAMETER MarkdownPath
    Куда записать готовый markdown-отчёт (UTF-8 без BOM).

.PARAMETER RunUrl
    Ссылка на прогон CI, попадает в шапку отчёта. Необязательна.

.OUTPUTS
    Код возврата всегда 0. Признак наличия расхождений пишется в
    $env:GITHUB_OUTPUT (has-drift / drift-count / checked-count), если
    переменная задана, и дублируется в консоль.

.EXAMPLE
    .\Verify-CatalogDownloads.ps1
    .\New-CatalogDriftReport.ps1 -MarkdownPath .\drift.md
#>
param(
    [string]$ReportPath = (Join-Path $env:TEMP 'Ven4Tools-CatalogAudit\report.json'),
    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\Catalog\master.json'),
    [string]$MarkdownPath = (Join-Path $env:TEMP 'Ven4Tools-CatalogAudit\drift.md'),
    [string]$RunUrl = ''
)

$ErrorActionPreference = 'Stop'

# .OUTPUTS обещает "код возврата всегда 0", но без обработчика непредвиденная
# ошибка (битый report.json, недоступный CatalogPath и т.п.) уронила бы скрипт
# с ненулевым кодом — сосед Verify-CatalogDownloads.ps1 отдельно защищён шагом
# workflow (continue-on-error: true), а этот шаг — нет. Trap ловит то же самое
# здесь, чтобы контракт из .OUTPUTS был правдой, а не пожеланием.
trap {
    Write-Host "Построение отчёта о расхождениях упало: $($_.Exception.Message)"
    $fallback = "## Ревалидация каталога`n`n> [!WARNING]`n> Построение отчёта о расхождениях завершилось ошибкой: " +
        "$($_.Exception.Message)`n> Смотри лог прогона за подробностями.`n"
    Write-Utf8NoBom -Path $MarkdownPath -Text $fallback
    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value 'has-drift=true'
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value 'drift-count=0'
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value 'checked-count=0'
    }
    exit 0
}

# Предел тела issue у GitHub — 65536 символов, оставляем запас на служебный текст.
$MaxBodyLength = 60000

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    # Запись через System.IO вместо Set-Content: в Windows PowerShell 5.1 ключ
    # -Encoding UTF8 добавляет BOM, и он вылезает первым символом тела issue.
    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function ConvertTo-ComparableVersion {
    <#
        Приводит версию к сравнимому виду: берёт первую версиеподобную
        последовательность («6.0.1.0 (build 42)» -> «6.0.1.0») и отбрасывает
        хвостовые нули, чтобы «1.2.3» не разошлось с «1.2.3.0».
    #>
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }

    $match = [regex]::Match($Value, '\d+(?:\.\d+)*')
    if (-not $match.Success) { return $null }

    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($part in $match.Value.Split('.')) {
        $parts.Add(([long]$part).ToString())
    }
    while ($parts.Count -gt 1 -and $parts[$parts.Count - 1] -eq '0') {
        $parts.RemoveAt($parts.Count - 1)
    }
    return ($parts -join '.')
}

function ConvertTo-Bytes {
    <# «84.7 MB» -> 88805376. Возвращает $null, если строку разобрать не удалось. #>
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }

    $match = [regex]::Match(
        $Value.Trim(),
        '^(?<number>\d+(?:[.,]\d+)?)\s*(?<unit>[KMG]B)$',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    if (-not $match.Success) { return $null }

    $number = [double]::Parse(
        $match.Groups['number'].Value.Replace(',', '.'),
        [System.Globalization.CultureInfo]::InvariantCulture
    )
    switch ($match.Groups['unit'].Value.ToUpperInvariant()) {
        'KB' { return [long]($number * 1KB) }
        'MB' { return [long]($number * 1MB) }
        'GB' { return [long]($number * 1GB) }
    }
    return $null
}

function Format-Bytes {
    <#
        Формат намеренно инвариантный к культуре: значение попадает в отчёт,
        откуда его переносят в поле size каталога, а там разделитель — точка.
        Оператор -f взял бы текущую локаль и на русской машине выдал «95,0 MB».
    #>
    param([long]$Bytes)

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    if ($Bytes -lt 1MB) {
        return [string]::Format($culture, '{0:F1} KB', ($Bytes / 1KB))
    }
    return [string]::Format($culture, '{0:F1} MB', ($Bytes / 1MB))
}

function Format-TableCell {
    <# Экранирует вертикальную черту, иначе текст ошибки ломает markdown-таблицу. #>
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '—' }
    return ($Value -replace '\|', '\|')
}

function Format-ShortHash {
    param([string]$Hash)
    if ([string]::IsNullOrWhiteSpace($Hash)) { return '—' }
    if ($Hash.Length -le 16) { return $Hash }
    return $Hash.Substring(0, 16) + '…'
}

# --- Загрузка исходных данных -------------------------------------------------

$catalogById = @{}
if (Test-Path -LiteralPath $CatalogPath) {
    $catalog = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($app in $catalog.apps) {
        $catalogById[[string]$app.id] = $app
    }
}

$results = @()
$reportMissing = $false
if (Test-Path -LiteralPath $ReportPath) {
    $raw = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        # Разворачиваем аккуратно и без конвейера. В Windows PowerShell 5.1
        # ConvertFrom-Json отдаёт массив одним объектом, поэтому запись вида
        # @($raw | ConvertFrom-Json) даёт вложенный массив из одного элемента —
        # весь каталог схлопывается в одну запись. Плюс обратный случай:
        # при единственной записи ConvertTo-Json пишет объект, а не массив.
        $parsed = ConvertFrom-Json -InputObject $raw
        if ($null -eq $parsed) { $results = @() }
        elseif ($parsed -is [System.Array]) { $results = $parsed }
        else { $results = @($parsed) }
    }
}
else {
    $reportMissing = $true
}

# --- Разбор расхождений -------------------------------------------------------

$broken = [System.Collections.Generic.List[object]]::new()
$drifted = [System.Collections.Generic.List[object]]::new()

foreach ($item in $results) {
    $id = [string]$item.id
    $catalogApp = $catalogById[$id]

    if (-not [string]::IsNullOrWhiteSpace([string]$item.error)) {
        $broken.Add([pscustomobject]@{
            id     = $id
            name   = [string]$item.name
            url    = [string]$item.sourceUrl
            status = $item.httpStatus
            # Скобки обязательны: внутри хеш-литерала запятая в -replace
            # иначе разбирается как разделитель элементов хеша.
            reason = ((([string]$item.error) -replace '\s+', ' ').Trim())
        })
        continue
    }

    $issues = [System.Collections.Generic.List[string]]::new()
    $kinds = [System.Collections.Generic.List[string]]::new()

    # sha256 — главный сигнал: именно он валит проверку в клиенте.
    if ($item.shaChanged -eq $true) {
        $kinds.Add('sha256')
        $issues.Add("**sha256** — в каталоге ``$([string]$item.previousSha256)``, фактический ``$([string]$item.sha256)``")
    }

    # Версия — сверяем ProductVersion скачанного PE с полем version каталога.
    $catalogVersion = if ($catalogApp) { [string]$catalogApp.version } else { [string]$item.catalogVersion }
    $actualVersion = [string]$item.productVersion
    $normalizedCatalog = ConvertTo-ComparableVersion $catalogVersion
    $normalizedActual = ConvertTo-ComparableVersion $actualVersion
    if ($normalizedCatalog -and $normalizedActual -and $normalizedCatalog -ne $normalizedActual) {
        $kinds.Add('версия')
        $issues.Add("**версия** — в каталоге ``$catalogVersion``, у файла ``$actualVersion``")
    }

    # Размер — сравниваем с допуском: каталог хранит округление до 0.1 МБ.
    if ($catalogApp) {
        $catalogBytes = ConvertTo-Bytes ([string]$catalogApp.size)
        $actualBytes = [long]$item.bytes
        if ($null -ne $catalogBytes -and $catalogBytes -gt 0 -and $actualBytes -gt 0) {
            $delta = [Math]::Abs($actualBytes - $catalogBytes)
            $relative = $delta / [double]$catalogBytes
            if ($relative -gt 0.03 -and $delta -gt 200KB) {
                $kinds.Add('размер')
                $issues.Add("**размер** — в каталоге ``$([string]$catalogApp.size)``, фактический ``$(Format-Bytes $actualBytes)``")
            }
        }
    }

    # Ссылка отвечает 200, но отдаёт не установщик — типичный признак того,
    # что вендор подменил прямую ссылку страницей-заглушкой или капчей.
    if (-not $item.validInstaller -and ([string]$item.format) -ne 'zip') {
        $kinds.Add('не установщик')
        $issues.Add("**формат** — по ссылке пришёл не установщик (не PE/MSI/ZIP), Content-Type: ``$([string]$item.contentType)``. Похоже на страницу-заглушку вместо файла.")
    }

    # Подпись проверяем только у PE: у ZIP её не бывает по определению.
    $signature = [string]$item.signatureStatus
    if (([string]$item.format) -eq 'pe' -and $signature -and $signature -ne 'Valid') {
        $kinds.Add('подпись')
        $issues.Add("**подпись Authenticode** — статус ``$signature``")
    }

    if ($issues.Count -gt 0) {
        $drifted.Add([pscustomobject]@{
            id      = $id
            name    = [string]$item.name
            url     = [string]$item.sourceUrl
            final   = [string]$item.finalUrl
            kinds   = ($kinds -join ', ')
            issues  = $issues
            newHash = [string]$item.sha256
        })
    }
}

$checkedCount = @($results).Count
$driftCount = $broken.Count + $drifted.Count

# --- Сборка markdown ----------------------------------------------------------

$lines = [System.Collections.Generic.List[string]]::new()
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'

$lines.Add('## Ревалидация каталога')
$lines.Add('')

if ($reportMissing) {
    $lines.Add('> [!WARNING]')
    $lines.Add('> Файл `report.json` не найден — ревалидация не отработала до конца.')
    $lines.Add('> Смотри лог прогона: скорее всего упал сам `Tools/Verify-CatalogDownloads.ps1`.')
    $lines.Add('')
}

$lines.Add("Проверено записей с прямой ссылкой: **$checkedCount**. Расхождений: **$driftCount**.")
$lines.Add("Прогон: $timestamp.")
if (-not [string]::IsNullOrWhiteSpace($RunUrl)) {
    $lines.Add("Лог: $RunUrl")
}
$lines.Add('')
$lines.Add('<sub>Каталог фиксирует `sha256`, `version` и `size`, а часть ссылок вендоров — «вечные» (например `download.mozilla.org/?product=firefox-latest`). Как только вендор выкладывает новую сборку, зафиксированный хеш перестаёт сходиться: клиент молча проваливает проверку SHA256 и уходит на winget/choco, а `version` и `size` в карточке начинают врать.</sub>')
$lines.Add('')

if ($driftCount -eq 0 -and -not $reportMissing) {
    $lines.Add('Расхождений нет — все прямые ссылки отдают ровно то, что записано в каталоге.')
    $lines.Add('')
}

if ($broken.Count -gt 0) {
    $lines.Add('### Битые ссылки (' + $broken.Count + ')')
    $lines.Add('')
    $lines.Add('Файл не скачался вообще: ссылка протухла, вендор сменил структуру URL или сервер недоступен.')
    $lines.Add('')
    $lines.Add('| Приложение | id | HTTP | Причина |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($entry in $broken) {
        $status = if ($null -ne $entry.status) { [string]$entry.status } else { '—' }
        $lines.Add("| $(Format-TableCell $entry.name) | ``$($entry.id)`` | $status | $(Format-TableCell $entry.reason) |")
    }
    $lines.Add('')
    foreach ($entry in $broken) {
        $lines.Add("- ``$($entry.id)`` → $($entry.url)")
    }
    $lines.Add('')
}

if ($drifted.Count -gt 0) {
    $lines.Add('### Расхождения с каталогом (' + $drifted.Count + ')')
    $lines.Add('')
    $lines.Add('| Приложение | id | Что разошлось | Фактический sha256 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($entry in $drifted) {
        $lines.Add("| $(Format-TableCell $entry.name) | ``$($entry.id)`` | $($entry.kinds) | ``$(Format-ShortHash $entry.newHash)`` |")
    }
    $lines.Add('')
    $lines.Add('<details><summary>Подробности по каждой записи</summary>')
    $lines.Add('')
    foreach ($entry in $drifted) {
        $lines.Add("#### $($entry.name) — ``$($entry.id)``")
        $lines.Add('')
        $lines.Add("Ссылка: $($entry.url)")
        if (-not [string]::IsNullOrWhiteSpace($entry.final) -and $entry.final -ne $entry.url) {
            $lines.Add("После редиректов: $($entry.final)")
        }
        $lines.Add('')
        foreach ($issue in $entry.issues) {
            $lines.Add("- $issue")
        }
        $lines.Add('')
    }
    $lines.Add('</details>')
    $lines.Add('')
}

if ($driftCount -gt 0 -or $reportMissing) {
    $lines.Add('### Что с этим делать')
    $lines.Add('')
    $lines.Add('1. Проверить, что новая сборка у вендора — та, что нужна, и ссылка ведёт на официальный источник.')
    $lines.Add('2. Обновить `sha256`, `version` и `size` в `Catalog/master.json` через `ven4admin`.')
    $lines.Add('3. Переподписать каталог (`Tools/CatalogSigner`) и обновить `Catalog/master.json.sig`.')
    $lines.Add('4. Разложить каталог и подпись на CDN.')
    $lines.Add('')
    $lines.Add('> [!NOTE]')
    $lines.Add('> Правку каталога и подпись CI намеренно не делает сам: приватного ключа подписи у CI нет и быть не должно, поэтому автообновление `master.json` оставило бы каталог с невалидной подписью — клиент отверг бы его целиком. Это ручной шаг владельца.')
    $lines.Add('')
    $lines.Add('Issue закроется сама на следующем прогоне, когда расхождений не останется.')
}

$body = ($lines -join "`n")
if ($body.Length -gt $MaxBodyLength) {
    $body = $body.Substring(0, $MaxBodyLength) +
        "`n`n…отчёт обрезан по лимиту GitHub. Полный ``report.json`` приложен артефактом к прогону."
}

Write-Utf8NoBom -Path $MarkdownPath -Text $body

# --- Итог ---------------------------------------------------------------------

$hasDrift = ($driftCount -gt 0 -or $reportMissing)

Write-Host "Проверено записей: $checkedCount"
Write-Host "Битых ссылок: $($broken.Count)"
Write-Host "Расхождений с каталогом: $($drifted.Count)"
Write-Host "Отчёт: $MarkdownPath"

if ($env:GITHUB_OUTPUT) {
    $flag = if ($hasDrift) { 'true' } else { 'false' }
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "has-drift=$flag"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "drift-count=$driftCount"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "checked-count=$checkedCount"
}

exit 0
