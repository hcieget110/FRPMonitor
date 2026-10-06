using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FRPMonitor;

public static class FileLocation
{
    public static void Reveal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("导出的文件已移动或删除，无法选中。", fullPath);
        var apartment = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? 2u : 0u;
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, apartment));
        IntPtr item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, IntPtr.Zero, out item, 0, out _));
            // A single absolute item with count=0 opens its parent and selects that exact file.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally { if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item); CoUninitialize(); }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}
