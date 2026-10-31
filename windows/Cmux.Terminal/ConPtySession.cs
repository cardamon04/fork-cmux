using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cmux.Terminal;

// A single caller owns input, resize and disposal. Output is drained independently
// from creation through teardown, including when the diagnostic consumer throws.
public sealed class ConPtySession : IDisposable
{
    private nint console;
    private SafeProcessHandle? process;
    private Stream? input;
    private Task? reader;
    private bool disposed;

    private ConPtySession() { }

    public int ProcessId { get; private set; }

    public static ConPtySession Start(string executable, IReadOnlyList<string> arguments, string directory,
        short columns, short rows, Action<string> onOutput)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("ConPTY requires Windows build 17763 or later.");
        ValidateSize(columns, rows);
        ArgumentNullException.ThrowIfNull(onOutput);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("An existing absolute executable path is required.", nameof(executable));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var session = new ConPtySession();
        try
        {
            // An overlapped input pipe lets the UI cancel a write when a child
            // stops consuming input. Anonymous CreatePipe handles cannot do so.
            var pipeName = "cmux-input-" + Guid.NewGuid().ToString("N");
            session.input = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var accepted = ((NamedPipeServerStream)session.input).WaitForConnectionAsync();
            using var inputClient = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
            inputClient.Connect(5000);
            accepted.GetAwaiter().GetResult();
            using (var inputRead = new SafeFileHandle(inputClient.SafePipeHandle.DangerousGetHandle(), ownsHandle: false))
            {
                Check(NativeMethods.CreatePipe(out var outputRead, out var outputWrite, 0, 0));
                using (outputRead)
                using (outputWrite)
                {
                    Marshal.ThrowExceptionForHR(NativeMethods.CreatePseudoConsole(
                        new(columns, rows), inputRead, outputWrite, 0, out session.console));
                    // Transfer ownership before constructing the streams. The
                    // temporary native endpoints close after process creation.
                    var ownedOutput = new SafeFileHandle(outputRead.DangerousGetHandle(), ownsHandle: true);
                    outputRead.SetHandleAsInvalid();
                    try
                    {
                        var output = new FileStream(ownedOutput, FileAccess.Read, 4096, isAsync: false);
                        session.reader = Task.Factory.StartNew(() => Drain(output, onOutput),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                    catch
                    {
                        ownedOutput.Dispose();
                        throw;
                    }
                    session.Spawn(executable, arguments, Path.GetFullPath(directory));
                }
            }
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void Spawn(string executable, IReadOnlyList<string> arguments, string directory)
    {
        nuint bytes = 0;
        NativeMethods.InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
        if (bytes == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes = Marshal.AllocHGlobal(checked((nint)bytes));
        var initialized = false;
        try
        {
            Check(NativeMethods.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            initialized = true;
            Check(NativeMethods.UpdateProcThreadAttribute(attributes, 0, NativeMethods.PseudoConsoleAttribute,
                console, (nuint)nint.Size, 0, 0));
            var startup = new NativeMethods.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
            // With a redirected parent, Windows otherwise duplicates its pipe
            // handles into the child even when inheritHandles is false. Null
            // standard handles with STARTF_USESTDHANDLES bind the child to ConPTY.
            startup.StartupInfo.Flags = 0x00000100; // STARTF_USESTDHANDLES
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument)));
            Check(NativeMethods.CreateProcessW(executable, command, 0, 0, false,
                NativeMethods.ExtendedStartupInfoPresent, 0, directory, ref startup, out var information));
            process = new SafeProcessHandle(information.Process, ownsHandle: true);
            NativeMethods.CloseHandle(information.Thread);
            ProcessId = checked((int)information.ProcessId);
        }
        finally
        {
            if (initialized)
                NativeMethods.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    // Windows argv quoting; an explicit application path prevents executable
    // search ambiguity when the path or an argument contains spaces.
    internal static string QuoteArgument(string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("NUL is not allowed in a process argument.", nameof(value));
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Write(string text)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var bytes = Encoding.UTF8.GetBytes(text);
        input!.Write(bytes);
        input.Flush();
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await input!.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    public void Resize(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateSize(columns, rows);
        Marshal.ThrowExceptionForHR(NativeMethods.ResizePseudoConsole(console, new(columns, rows)));
    }

    public uint WaitForExit(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds >= uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var result = NativeMethods.WaitForSingleObject(process!, (uint)timeout.TotalMilliseconds);
        if (result == NativeMethods.WaitTimeout)
            throw new TimeoutException("The terminal process did not exit within the deadline.");
        if (result != NativeMethods.WaitObject0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(NativeMethods.GetExitCodeProcess(process!, out var exitCode));
        return exitCode;
    }

    private static void Drain(FileStream output, Action<string> onOutput)
    {
        using (output)
        using (var text = new StreamReader(output, new UTF8Encoding(false, true), false, 4096))
        {
            var buffer = new char[4096];
            Exception? consumerError = null;
            int count;
            try
            {
                while ((count = text.Read(buffer, 0, buffer.Length)) != 0)
                {
                    try { if (consumerError is null) onOutput(new string(buffer, 0, count)); }
                    catch (Exception error) { consumerError = error; }
                }
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) == 109) { } // ERROR_BROKEN_PIPE is EOF.
            if (consumerError is not null)
                throw new InvalidOperationException("The output consumer failed; the pipe was still drained.", consumerError);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            // Keep draining while ClosePseudoConsole sends the final screen frame.
            // Closing input first ends any pending console input request.
            input?.Dispose();
            if (console != 0)
            {
                NativeMethods.ClosePseudoConsole(console);
                console = 0;
            }
            // Since Windows 11 24H2, ClosePseudoConsole returns immediately.
            // Output EOF alone does not prove that the client released its
            // executable and working directory; wait on our own process handle.
            if (process is not null && !process.IsInvalid)
            {
                var result = NativeMethods.WaitForSingleObject(process, 10000);
                if (result == NativeMethods.WaitTimeout)
                    throw new TimeoutException("The ConPTY client did not exit during shutdown.");
                if (result != NativeMethods.WaitObject0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (reader is not null && !reader.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("ConPTY output did not reach EOF during shutdown.");
        }
        finally { process?.Dispose(); }
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void ValidateSize(short columns, short rows)
    {
        if (columns <= 0 || rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be positive.");
    }
}
