using System;

namespace Ven4Tools.Services
{
    internal static class AppLogger
    {
        public static event Action<string>? MessageReceived;

        public static void Write(string message) => MessageReceived?.Invoke(message);
    }
}
