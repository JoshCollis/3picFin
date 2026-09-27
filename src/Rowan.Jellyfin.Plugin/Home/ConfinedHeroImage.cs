using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Linux-only, fail-closed descriptor-relative open for an explicitly supplied metadata library root.</summary>
public static class ConfinedHeroImage
{
    private const int AtFdcwd = -100;
    private const long OpenAt2Number = 437; // Linux x86_64 and arm64; reject other architectures below.
    private const ulong OReadOnly = 0;
    private const ulong ODirectory = 0x10000;
    private const ulong ONoFollow = 0x20000;
    private const ulong ONonBlock = 0x800;
    private const ulong OCloseOnExec = 0x80000;
    private const ulong OPath = 0x200000;
    private const ulong ResolveNoXdev = 0x01;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x1;
    private const uint StatxSize = 0x200;
    private const ushort RegularFile = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenAt2(long number, int dirfd, string path, ref OpenHow how, ulong size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(int dirfd, string path, int flags, uint mask, [Out] byte[] result);

    /// <summary>Returns null on unavailable syscall, invalid path, non-regular or oversized image; caller owns the handle.</summary>
    public static FileStream? TryOpen(string? root, string? imagePath)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) ||
            string.IsNullOrEmpty(root) || string.IsNullOrEmpty(imagePath) || root.Contains('\0') || imagePath.Contains('\0') ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(imagePath)) return null;
        try
        {
            // Exact lexical paths avoid dot-segment aliases; the kernel enforces confinement and no symlinks.
            root = Path.TrimEndingDirectorySeparator(root);
            if (root == "/" || Path.GetFullPath(root) != root || Path.GetFullPath(imagePath) != imagePath ||
                !imagePath.StartsWith(root + "/", StringComparison.Ordinal)) return null;
            var relative = imagePath[(root.Length + 1)..];
            if (relative.Length == 0) return null;
            using var slash = new SafeFileHandle(Open("/", (int)(OPath | ODirectory | OCloseOnExec)), true);
            if (slash.IsInvalid) return null;
            var rootHow = new OpenHow { Flags = OPath | ODirectory | OCloseOnExec, Resolve = ResolveBeneath | ResolveNoSymlinks };
            using var directory = new SafeFileHandle(OpenAt2(OpenAt2Number, slash.DangerousGetHandle().ToInt32(), root[1..], ref rootHow, 24), true);
            if (directory.IsInvalid) return null;
            // The trusted metadata root may itself be a mount; prohibit crossing mounts below it.
            var fileHow = new OpenHow { Flags = OReadOnly | ONoFollow | ONonBlock | OCloseOnExec, Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoXdev };
            var handle = new SafeFileHandle(OpenAt2(OpenAt2Number, directory.DangerousGetHandle().ToInt32(), relative, ref fileHow, 24), true);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            // statx AT_EMPTY_PATH inspects the actual opened inode, never a second pathname lookup.
            var stat = new byte[256];
            if (Statx(handle.DangerousGetHandle().ToInt32(), "", AtEmptyPath, StatxType | StatxSize, stat) != 0 ||
                (BitConverter.ToUInt16(stat, 28) & 0xf000) != RegularFile ||
                BitConverter.ToUInt64(stat, 40) is < 23 or > HeroImageBounds.MaxBytes)
            {
                handle.Dispose();
                return null;
            }
            // A native Linux descriptor is synchronous; async FileStream validation rejects it.
            try { return new FileStream(handle, FileAccess.Read, 4096, false); }
            catch { handle.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return null;
        }
    }
}
