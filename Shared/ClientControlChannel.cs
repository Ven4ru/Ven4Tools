using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Shared
{
    internal sealed class ClientControlServer : IDisposable
    {
        internal const string PipeName = "Ven4Tools.Client.Control.v1";
        private readonly Action _shutdownRequested;
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;

        public ClientControlServer(Action shutdownRequested)
        {
            _shutdownRequested = shutdownRequested ?? throw new ArgumentNullException(nameof(shutdownRequested));
        }

        public void Start()
        {
            _listenTask ??= ListenAsync(_cts.Token);
        }

        private async Task ListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        CreateCurrentUserPipeSecurity());
                    await pipe.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    string? command = await reader.ReadLineAsync(token);
                    if (string.Equals(command, "shutdown", StringComparison.Ordinal))
                        _shutdownRequested();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // Клиент pipe мог отключиться между подключением и чтением.
                    // Та же ошибка приходит и при создании pipe, если имя уже занято
                    // другим процессом («все экземпляры заняты»), — без паузы цикл
                    // превращался бы в непрерывное пересоздание на 100% ядра.
                    try { await Task.Delay(500, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // PipeOptions.CurrentUserOnly здесь не годится: .NET строит ACL и владельца
        // pipe по WindowsIdentity.Owner, а у elevated-токена владелец по умолчанию —
        // группа Администраторы, а не сам пользователь. Pipe получался доступным только
        // elevated-процессам, и лаунчер (asInvoker) не мог к нему подключиться, а его
        // клиентская проверка CurrentUserOnly (владелец pipe == Owner своего токена)
        // отвергала бы соединение в любом случае — в штатной связке «клиент elevated,
        // лаунчер asInvoker» закрытие клиента через pipe не срабатывало. Поэтому ACL и владелец задаются явно SID пользователя
        // (WindowsIdentity.User): он одинаков у elevated- и обычного токена одного
        // пользователя, а другим пользователям доступ по-прежнему закрыт.
        private static PipeSecurity CreateCurrentUserPipeSecurity()
        {
            using var identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User
                ?? throw new InvalidOperationException("Не удалось определить SID текущего пользователя.");
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.SetOwner(user);
            return security;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    internal static class ClientControlChannel
    {
        public static async Task<bool> RequestShutdownAsync(int timeoutMs = 2000)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                // Без PipeOptions.CurrentUserOnly — по той же причине, что и у сервера:
                // встроенная проверка сверяет владельца pipe с WindowsIdentity.Owner, а у
                // лаунчера, запущенного «от имени администратора», это группа
                // Администраторы, тогда как сервер назначает владельцем SID пользователя.
                // Та же проверка «pipe принадлежит мне» делается явно ниже.
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    ClientControlServer.PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                if (!IsOwnedByCurrentUser(pipe))
                    return false;

                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("shutdown".AsMemory(), timeout.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsOwnedByCurrentUser(NamedPipeClientStream pipe)
        {
            try
            {
                var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner == null) return false;
                using var identity = WindowsIdentity.GetCurrent();
                return owner == identity.User || owner == identity.Owner;
            }
            catch
            {
                return false;
            }
        }
    }
}
