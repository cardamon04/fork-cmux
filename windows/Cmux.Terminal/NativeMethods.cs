using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cmux.Terminal;

internal static class NativeMethods
{
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const nuint PseudoConsoleAttribute = 0x00020016;
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord(short x, short y)
    {
        internal short X = x;
        internal short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal nint Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);

    [DllImport("kernel32.dll")]
    internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll")]
    internal static extern int ResizePseudoConsole(nint console, Coord size);

    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
