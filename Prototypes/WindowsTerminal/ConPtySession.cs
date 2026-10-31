using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsTerminal;

// A single caller owns input, resize and disposal. Output is drained independently
// from creation through teardown, including when the diagnostic consumer throws.
internal sealed class ConPtySession : IDisposable
{
    private nint console;
    private SafeProcessHandle? process;
    private FileStream? input;
    private Task? reader;
    private bool disposed;

    private ConPtySession() { }

    internal int ProcessId { get; private set; }

    internal static ConPtySession Start(string executable, IReadOnlyList<string> arguments, string directory,
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
            Check(NativeMethods.CreatePipe(out var inputRead, out var inputWrite, 0, 0));
            using (inputRead)
            using (inputWrite)
            {
                Check(NativeMethods.CreatePipe(out var outputRead, out var outputWrite, 0, 0));
                using (outputRead)
                using (outputWrite)
                {
                    Marshal.ThrowExceptionForHR(NativeMethods.CreatePseudoConsole(
                        new(columns, rows), inputRead, outputWrite, 0, out session.console));
                    // Transfer ownership before constructing the streams. The
                    // temporary native endpoints close after process creation.
                    var ownedInput = new SafeFileHandle(inputWrite.DangerousGetHandle(), ownsHandle: true);
                    var ownedOutput = new SafeFileHandle(outputRead.DangerousGetHandle(), ownsHandle: true);
                    inputWrite.SetHandleAsInvalid();
                    outputRead.SetHandleAsInvalid();
                    try
                    {
                        session.input = new FileStream(ownedInput, FileAccess.Write, 4096, isAsync: false);
                        var output = new FileStream(ownedOutput, FileAccess.Read, 4096, isAsync: false);
                        session.reader = Task.Factory.StartNew(() => Drain(output, onOutput),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                    catch
                    {
                        ownedInput.Dispose();
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

    internal void Write(string text)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var bytes = Encoding.UTF8.GetBytes(text);
        input!.Write(bytes);
        input.Flush();
    }

    internal void Resize(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateSize(columns, rows);
        Marshal.ThrowExceptionForHR(NativeMethods.ResizePseudoConsole(console, new(columns, rows)));
    }

    internal uint WaitForExit(TimeSpan timeout)
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
