using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cop.Providers;

public static class SourceFileMetadata
{
    public static long ReadChangeTime(string path)
    {
        // Attribute-only access detects same-length edits even when LastWriteTime is restored.
        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        using var handle = CreateFileW(nativePath, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 0, out var information,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw new IOException($"Could not inspect source file '{path}': {error.Message}", error);
        }
        return information.ChangeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileBasicInfo information, uint size);
}
