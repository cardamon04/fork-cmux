using System.Text;

namespace WindowsTerminal;

internal sealed class Transcript
{
    private readonly object gate = new();
    private readonly StringBuilder text = new();
    private TaskCompletionSource changed = NewSignal();
    private const int Limit = 2 * 1024 * 1024;

    internal void Append(string value)
    {
        lock (gate)
        {
            if (text.Length + value.Length > Limit)
                throw new InvalidOperationException("The diagnostic transcript exceeded its bounded capacity.");
            text.Append(value);
            changed.TrySetResult();
            changed = NewSignal();
        }
    }

    internal async Task WaitFor(string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            Task update;
            lock (gate)
            {
                if (text.ToString().Contains(expected, StringComparison.Ordinal)) return;
                update = changed.Task;
            }
            try { await update.WaitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                lock (gate)
                    throw new TimeoutException($"Expected {expected}; transcript tail: {text.ToString()[Math.Max(0, text.Length - 2000)..]}");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
