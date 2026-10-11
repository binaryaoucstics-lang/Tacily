using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenTranslator.App.Services.Networking;

/// <summary>Owned proxy cores terminate with their parent, including abrupt termination.</summary>
internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public ProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var info = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { _handle.Dispose(); throw new System.ComponentModel.Win32Exception(); }
    }
    public void Attach(Process process)
    { if (!AssignProcessToJobObject(_handle, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
