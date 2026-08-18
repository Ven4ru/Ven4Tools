param(
    [string]$CatalogPath = (Join-Path $PSScriptRoot '..\Catalog\master.json'),
    [string]$OutputDirectory = (Join-Path $env:TEMP 'Ven4Tools-CatalogAudit'),
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$catalog = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$apps = @($catalog.apps | Where-Object { -not [string]::IsNullOrWhiteSpace($_.downloadUrl) })
$downloadDirectory = Join-Path $OutputDirectory 'downloads'
$reportPath = Join-Path $OutputDirectory 'report.json'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $true
$handler.MaxAutomaticRedirections = 10
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes($TimeoutMinutes)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Ven4Tools-Catalog-Audit/1.0')

$results = [System.Collections.Generic.List[object]]::new()

try {
    $index = 0
    foreach ($app in $apps) {
        $index++
        $safeId = ($app.id -replace '[^A-Za-z0-9._-]', '_')
        $target = Join-Path $downloadDirectory "$safeId.download"
        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        Write-Host "[$index/$($apps.Count)] $($app.id)"

        $result = [ordered]@{
            id = $app.id
            name = $app.name
            catalogVersion = $app.version
            sourceUrl = $app.downloadUrl
            finalUrl = $null
            httpStatus = $null
            contentType = $null
            bytes = 0
            size = $null
            format = 'unknown'
            sha256 = $null
            previousSha256 = $app.sha256
            shaChanged = $null
            productVersion = $null
            signatureStatus = $null
            signer = $null
            validInstaller = $false
            error = $null
        }

        try {
            $response = $client.GetAsync(
                [string]$app.downloadUrl,
                [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
            ).GetAwaiter().GetResult()
            try {
                $result.httpStatus = [int]$response.StatusCode
                $result.finalUrl = [string]$response.RequestMessage.RequestUri
                $result.contentType = [string]$response.Content.Headers.ContentType
                $null = $response.EnsureSuccessStatusCode()
                # Заявленный размер запоминаем до чтения тела: без сверки с ним
                # оборванная закачка молча хешировалась и попадала в отчёт как
                # «дрейф sha256». Поймано вживую на microsoft-edge (2026-08-16):
                # скачалось ровно 28 MiB вместо 194.7 MB, и запись неделю висела
                # в issue как расхождение, которого на самом деле не было.
                $expectedBytes = $response.Content.Headers.ContentLength

                $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $output = [System.IO.File]::Open(
                        $target,
                        [System.IO.FileMode]::Create,
                        [System.IO.FileAccess]::Write,
                        [System.IO.FileShare]::None
                    )
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
            finally {
                $response.Dispose()
            }

            $file = Get-Item -LiteralPath $target
            $result.bytes = $file.Length
            $result.size = '{0:F1} MB' -f ($file.Length / 1MB)

            # Недокачанный файл — ошибка загрузки, а не расхождение каталога.
            # Иначе его хеш уходит в отчёт как «фактический» и провоцирует
            # правку каталога на мусорное значение.
            # throw, а не собственный Add+continue: запись кладётся в отчёт единым
            # способом в конце итерации (ниже по коду), и обход этого места ломал бы
            # и тип записи, и промежуточное сохранение report.json.
            if ($null -ne $expectedBytes -and $file.Length -ne [int64]$expectedBytes) {
                throw "Недокачано: получено $($file.Length) байт из $expectedBytes заявленных (Content-Length)"
            }

            $result.sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            # Пустой sha256 в каталоге — осознанное открепление записи с «вечной»
            # ссылкой без версии в URL (вендор пересобирает файл по тому же адресу,
            # любой зафиксированный хеш протухает за дни). Клиент такой источник
            # штатно пропускает и ставит через winget/choco, поэтому расхождением
            # это не считается — иначе issue переоткрывался бы каждую неделю ровно
            # по тем записям, которые открепили намеренно.
            $result.shaChanged = if ([string]::IsNullOrWhiteSpace([string]$result.previousSha256)) {
                $false
            } else {
                -not [string]::Equals(
                    [string]$result.sha256,
                    [string]$result.previousSha256,
                    [StringComparison]::OrdinalIgnoreCase
                )
            }

            $header = [byte[]]::new(8)
            $headerStream = [System.IO.File]::OpenRead($target)
            try {
                $null = $headerStream.Read($header, 0, $header.Length)
            }
            finally {
                $headerStream.Dispose()
            }
            if ($header[0] -eq 0x4D -and $header[1] -eq 0x5A) {
                $result.format = 'pe'
                $result.validInstaller = $true
            }
            elseif ($header[0] -eq 0xD0 -and $header[1] -eq 0xCF -and
                    $header[2] -eq 0x11 -and $header[3] -eq 0xE0) {
                $result.format = 'msi'
                $result.validInstaller = $true
            }
            elseif ($header[0] -eq 0x50 -and $header[1] -eq 0x4B) {
                $result.format = 'zip'
            }

            if ($result.format -eq 'pe') {
                $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($target)
                $result.productVersion = $version.ProductVersion
                $signature = Get-AuthenticodeSignature -LiteralPath $target
                $result.signatureStatus = [string]$signature.Status
                $result.signer = $signature.SignerCertificate.Subject
            }
        }
        catch {
            $result.error = $_.Exception.Message
        }

        $results.Add([pscustomobject]$result)
        $results | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $reportPath -Encoding UTF8
    }
}
finally {
    $client.Dispose()
    $handler.Dispose()
}

$results | Format-Table id,httpStatus,format,size,shaChanged,signatureStatus,error -AutoSize
Write-Host "Report: $reportPath"
