using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Проверка Authenticode-подписи скачанных пакетов Microsoft (winget/VCLibs/UI.Xaml)
    /// перед elevated-установкой. В отличие от SHA256-пиннинга не ломается при плановом
    /// обновлении содержимого по тем же URL — тот же подход, что и в клиенте (OfficeTab).
    /// </summary>
    public static class AuthenticodeVerifier
    {
        public static bool IsSignedByMicrosoft(string filePath, out string error)
        {
            if (!File.Exists(filePath))
            {
                error = "файл не найден";
                return false;
            }

            int trustStatus = NativeMethods.VerifyAuthenticodeSignature(filePath);

            // Проверка отзыва (revocation) идёт по всей цепочке сертификатов, но требует
            // сети. Если онлайн-статус отзыва получить не удалось (нет сети / сервер CRL
            // или OCSP недоступен), WinVerifyTrust возвращает CERT_E_REVOCATION_FAILURE
            // либо CRYPT_E_REVOCATION_OFFLINE — при этом сама подпись валидна. Такой
            // случай не считаем провалом: fail-open только для недоступности проверки
            // отзыва (иначе лаунчер не смог бы установить компоненты оффлайн), а сама
            // подпись остаётся fail-closed. Отозванный сертификат вернёт CERT_E_REVOKED
            // и попадёт в общую ветку отказа ниже.
            const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x80092012);
            const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
            if (trustStatus != 0 &&
                trustStatus != CERT_E_REVOCATION_FAILURE &&
                trustStatus != CRYPT_E_REVOCATION_OFFLINE)
            {
                error = $"проверка Authenticode вернула код 0x{trustStatus:X8}";
                return false;
            }

            try
            {
#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
                if (certificate.Subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                {
                    error = "";
                    return true;
                }

                error = $"неожиданный издатель: {certificate.Subject}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"не удалось прочитать сертификат: {ex.Message}";
                return false;
            }
        }

        private static class NativeMethods
        {
            private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            // WTD_REVOKE_WHOLECHAIN — проверять отзыв по всей цепочке сертификатов,
            // а не только для листового (WTD_REVOKE_NONE = 0, как было раньше).
            private const uint WTD_REVOKE_WHOLECHAIN = 0x00000001;

            public static int VerifyAuthenticodeSignature(string filePath)
            {
                IntPtr filePathPtr = IntPtr.Zero;
                IntPtr fileInfoPtr = IntPtr.Zero;
                try
                {
                    filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
                    var fileInfo = new WintrustFileInfo
                    {
                        cbStruct       = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                        pcwszFilePath  = filePathPtr,
                        hFile          = IntPtr.Zero,
                        pgKnownSubject = IntPtr.Zero
                    };

                    fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
                    Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                    var trustData = new WintrustData
                    {
                        cbStruct            = (uint)Marshal.SizeOf<WintrustData>(),
                        pPolicyCallbackData = IntPtr.Zero,
                        pSIPClientData      = IntPtr.Zero,
                        dwUIChoice          = 2,
                        fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                        dwUnionChoice       = 1,
                        pFile               = fileInfoPtr,
                        dwStateAction       = 0,
                        hWVTStateData       = IntPtr.Zero,
                        pwszURLReference    = IntPtr.Zero,
                        dwProvFlags         = 0,
                        dwUIContext         = 0,
                        pSignatureSettings  = IntPtr.Zero
                    };

                    return WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref trustData);
                }
                finally
                {
                    if (fileInfoPtr != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(fileInfoPtr);
                    if (filePathPtr != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(filePathPtr);
                }
            }

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern int WinVerifyTrust(
                IntPtr hwnd,
                [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId,
                ref WintrustData pWVTData);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WintrustFileInfo
            {
                public uint cbStruct;
                public IntPtr pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WintrustData
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }
        }
    }
}
