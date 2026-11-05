<#
  Замер нагрузки блоков «Панель задач и Пуск» (Ven4Tools, Ven4Start, Ven4Bar) в ВМ.
  Раз в Interval секунд пишет в CSV по каждому процессу: доля процессора (% от всей машины),
  рабочий набор и частная память (МБ), дескрипторы, GDI- и USER-объекты, потоки.
  В конце — сводка: среднее и максимум процессора, рост памяти и дескрипторов (признак утечки).

  Пример: .\load_probe.ps1 -Minutes 10 -Label base      (чистая система)
          .\load_probe.ps1 -Minutes 10 -Label ven4bar   (с запущенной панелью)
#>
param(
    [int]$Minutes = 10,
    [int]$Interval = 2,
    [string]$Label = 'run',
    [string[]]$Names = @('explorer', 'Ven4Tools', 'Ven4Start', 'Ven4Bar', 'StartMenuExperienceHost', 'SearchHost', 'ShellExperienceHost', 'dwm'),
    [string]$OutDir = "$env:PUBLIC\Ven4LoadProbe"
)

Add-Type -Namespace V4 -Name Gui -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetGuiResources(System.IntPtr hProcess, uint uiFlags);
'@

New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$csv = Join-Path $OutDir "$Label`_$stamp.csv"
$cores = [Environment]::ProcessorCount
$prev = @{}
$rows = New-Object System.Collections.Generic.List[object]
$end = (Get-Date).AddMinutes($Minutes)
$os = (Get-CimInstance Win32_OperatingSystem)
"# $($os.Caption) $($os.Version) | ядер: $cores | метка: $Label" | Out-File $csv -Encoding utf8

while ((Get-Date) -lt $end) {
    $now = Get-Date
    foreach ($p in Get-Process -Name $Names -ErrorAction SilentlyContinue) {
        try {
            $key = "$($p.Name)#$($p.Id)"
            $cpuMs = $p.TotalProcessorTime.TotalMilliseconds
            $pct = $null
            if ($prev.ContainsKey($key)) {
                $dt = ($now - $prev[$key].T).TotalMilliseconds
                if ($dt -gt 0) { $pct = [math]::Round(100 * ($cpuMs - $prev[$key].Cpu) / ($dt * $cores), 3) }
            }
            $prev[$key] = @{ T = $now; Cpu = $cpuMs }
            $row = [pscustomobject]@{
                Time    = $now.ToString('HH:mm:ss')
                Name    = $p.Name
                Id      = $p.Id
                CpuPct  = $pct
                WsMB    = [math]::Round($p.WorkingSet64 / 1MB, 1)
                PrivMB  = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
                Handles = $p.HandleCount
                Gdi     = [V4.Gui]::GetGuiResources($p.Handle, 0)
                User    = [V4.Gui]::GetGuiResources($p.Handle, 1)
                Threads = $p.Threads.Count
            }
            $rows.Add($row)
        } catch { }
    }
    Start-Sleep -Seconds $Interval
}

$rows | Export-Csv $csv -NoTypeInformation -Encoding utf8 -Append -Force

"`n=== Сводка ($Label, $Minutes мин) ==="
$rows | Group-Object Name | ForEach-Object {
    $g = $_.Group
    $cpu = $g | Where-Object { $_.CpuPct -ne $null } | Measure-Object CpuPct -Average -Maximum
    $first = $g | Select-Object -First 1; $last = $g | Select-Object -Last 1
    [pscustomobject]@{
        Процесс        = $_.Name
        'ЦП ср %'      = [math]::Round($cpu.Average, 3)
        'ЦП макс %'    = [math]::Round($cpu.Maximum, 2)
        'Память МБ'    = $last.WsMB
        'Рост памяти'  = [math]::Round($last.PrivMB - $first.PrivMB, 1)
        'Рост дескр.'  = $last.Handles - $first.Handles
        'Рост GDI'     = $last.Gdi - $first.Gdi
    }
} | Format-Table -AutoSize
"CSV: $csv"
