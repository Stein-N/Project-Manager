// Only one instance runs: a second start hands its arguments to the running one (which shows its window) and ends.

using System.IO.Pipes;
using System.Text;

namespace ProjectManager.Services;

public sealed class SingleInstance : IDisposable
{
    private static string PipeName => $"net.morthen.projectmanager-{Environment.UserName}";

    private readonly CancellationTokenSource _stop = new();

    /// <summary>A later start with its arguments (on a worker thread).</summary>
    public event Action<string[]>? Started;

    private SingleInstance()
    {
    }

    /// <summary>
    /// The instance if this is the first one; null if another one runs – it got <paramref name="args"/> then.
    /// </summary>
    public static SingleInstance? Acquire(string[] args)
    {
        if (TrySend(args)) return null;
        var instance = new SingleInstance();
        instance.Listen();
        return instance;
    }

    private static bool TrySend(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(300);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args));
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Listen() => Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(_stop.Token);
                Started?.Invoke(text.Length == 0 ? [] : text.Split('\n'));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }
    });

    public void Dispose() => _stop.Cancel();
}
