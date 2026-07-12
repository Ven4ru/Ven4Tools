# ============================================================================
# set_ci_display_resolution.ps1 — расширяет виртуальный экран Windows-раннера
# GitHub Actions перед UI-тестами лаунчера/клиента.
#
# По умолчанию раннер windows-latest поднимает интерактивную сессию с
# разрешением 1024x768 — уже 1024px не хватает под окно лаунчера
# (ширина 1080 в MainWindow.xaml), из-за чего Windows подрезает окно до
# ширины экрана и pixel-snapshot тест (LauncherSmokeTests) падает с
# детерминированным несовпадением размера (Expected 1060x680, Actual 1024x680;
# инцидент 2026-07-12). Один раз меняем разрешение через ChangeDisplaySettings
# на 1920x1080 — этого достаточно с запасом для текущих и обозримых будущих
# размеров окон обоих приложений.
# ============================================================================

param(
    [int]$Width  = 1920,
    [int]$Height = 1080
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public class Ven4ToolsDisplay
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll")]
    public static extern int EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    public static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_PELSWIDTH  = 0x00080000;
    public const int DM_PELSHEIGHT = 0x00100000;

    public static int SetResolution(int width, int height)
    {
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) == 0)
            return -100;

        dm.dmPelsWidth  = width;
        dm.dmPelsHeight = height;
        dm.dmFields     = DM_PELSWIDTH | DM_PELSHEIGHT;

        return ChangeDisplaySettings(ref dm, 0);
    }
}
'@

$result = [Ven4ToolsDisplay]::SetResolution($Width, $Height)
Start-Sleep -Seconds 2

Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "ChangeDisplaySettings: код $result (0 = успех)"
Write-Host "Итоговое разрешение экрана: $($screen.Width)x$($screen.Height)"

if ($screen.Width -lt $Width -or $screen.Height -lt $Height) {
    Write-Warning "Разрешение не применилось полностью, snapshot-тест может упасть на подрезанном окне."
}
