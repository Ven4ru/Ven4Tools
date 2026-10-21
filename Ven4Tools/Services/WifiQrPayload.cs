using System;
using System.Text;

namespace Ven4Tools.Services
{
    /// <summary>Тип защиты сети для строки QR (поле T:).</summary>
    public enum WifiSecurityKind
    {
        /// <summary>Открытая сеть без пароля.</summary>
        Open,
        /// <summary>WPA/WPA2/WPA3 с общим паролем (личный режим).</summary>
        Wpa,
        /// <summary>Устаревший WEP.</summary>
        Wep,
        /// <summary>Корпоративная сеть (802.1X): общего пароля нет, QR не строится.</summary>
        Enterprise,
        /// <summary>Не удалось распознать.</summary>
        Unknown,
    }

    /// <summary>
    /// Строка подключения к Wi-Fi для QR-кода в общепринятом формате
    /// <c>WIFI:T:WPA;S:&lt;имя&gt;;P:&lt;пароль&gt;;;</c> — его понимают камеры Android и iPhone.
    /// </summary>
    public static class WifiQrPayload
    {
        /// <summary>
        /// Экранирует служебные символы формата: <c>\ ; , : "</c> — обратной косой чертой.
        /// Без этого пароль с «;» обрывал бы строку, и телефон получал бы чужой пароль.
        /// </summary>
        public static string Escape(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                if (c is '\\' or ';' or ',' or ':' or '"') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Собрать строку для QR. Для корпоративной и нераспознанной сети возвращает null —
        /// общего пароля у них нет, и QR вводил бы в заблуждение.
        /// </summary>
        public static string? Build(string ssid, string? password, WifiSecurityKind kind, bool hidden)
        {
            ArgumentNullException.ThrowIfNull(ssid);
            string? type = kind switch
            {
                WifiSecurityKind.Open => "nopass",
                WifiSecurityKind.Wpa => "WPA",
                WifiSecurityKind.Wep => "WEP",
                _ => null,
            };
            if (type == null) return null;
            if (kind != WifiSecurityKind.Open && string.IsNullOrEmpty(password)) return null;

            var sb = new StringBuilder("WIFI:");
            sb.Append("T:").Append(type).Append(';');
            sb.Append("S:").Append(Escape(ssid)).Append(';');
            if (kind != WifiSecurityKind.Open)
                sb.Append("P:").Append(Escape(password!)).Append(';');
            if (hidden) sb.Append("H:true;");
            sb.Append(';');
            return sb.ToString();
        }
    }
}
