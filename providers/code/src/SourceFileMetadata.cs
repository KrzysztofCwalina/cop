// Reads platform file metadata used for source-cache invalidation.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cop.Providers;

/// <summary>
/// Filesystem metadata used to describe a captured source-file version.
/// </summary>
public static class SourceFileMetadata
{
    /// <summary>
    /// Reads the filesystem change timestamp used to describe a source-file version.
    /// </summary>
    public static long ReadChangeTime(string path)
    {
        // Attribute-only access detects same-length edits even when LastWriteTime is restored.
        string nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        using SafeFileHandle handle = CreateFileW(nativePath, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 0, out FileBasicInfo information,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            Win32Exception error = new Win32Exception(Marshal.GetLastWin32Error());
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
