using System.Threading.Channels;
using Cmux.Terminal;

namespace Cmux.Desktop;

// One worker owns all calls into ConPTY. Output has its separate native drain.
internal sealed class TerminalConnection : IAsyncDisposable
{
    private sealed record Operation(Func<ConPtySession, Task> Run, TaskCompletionSource Done);
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<Operation> commands = Channel.CreateBounded<Operation>(64);
    private readonly Task worker;
    private readonly TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ProcessId => started.Task.GetAwaiter().GetResult();

    internal TerminalConnection(ShellLaunch launch, short cols, short rows, Action<string> output)
    {
        worker = Task.Run(async () =>
        {
            try
            {
                using var session = ConPtySession.Start(launch.Executable, launch.Arguments, launch.Directory, cols, rows, output);
                started.TrySetResult(session.ProcessId);
                await foreach (var op in commands.Reader.ReadAllAsync())
                {
                    try { await op.Run(session); op.Done.TrySetResult(); }
                    catch (Exception error) { op.Done.TrySetException(error); }
                }
            }
            catch (Exception error)
            {
                started.TrySetException(error);
                commands.Writer.TryComplete(error);
                while (commands.Reader.TryRead(out var op)) op.Done.TrySetException(error);
                throw;
            }
        });
    }
    internal Task Started => started.Task;
    private async Task Send(Func<ConPtySession, Task> run)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await commands.Writer.WriteAsync(new(run, done));
        await done.Task;
    }
    internal Task Write(string text) => Send(s => s.WriteAsync(text, stopping.Token));
    internal Task Resize(short cols, short rows) => Send(s => { s.Resize(cols, rows); return Task.CompletedTask; });
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        commands.Writer.TryComplete();
        await worker.WaitAsync(TimeSpan.FromSeconds(25));
        stopping.Dispose();
    }
}
