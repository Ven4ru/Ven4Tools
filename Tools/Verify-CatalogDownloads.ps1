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
            $result.sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            $result.shaChanged = -not [string]::Equals(
                [string]$result.sha256,
                [string]$result.previousSha256,
                [StringComparison]::OrdinalIgnoreCase
            )

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
