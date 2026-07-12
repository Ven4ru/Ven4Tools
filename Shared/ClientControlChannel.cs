using System;
using System.IO;
using System.IO.Pipes;
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
                    await using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
                }
            }
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
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    ClientControlServer.PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token);

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
    }
}
