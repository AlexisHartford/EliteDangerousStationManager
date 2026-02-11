using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace EliteDangerousStationManager.Helpers
{
    public static class MonitorUtil
    {
        public sealed class MonitorData
        {
            public string DeviceName { get; init; } = "";
            public bool Primary { get; init; }
            public Rect BoundsDip { get; init; }   // full monitor area (DIPs)
            public Rect WorkAreaDip { get; init; } // work area (taskbar excluded) (DIPs)
        }

        public static List<MonitorData> GetMonitorsDIP(Visual dpiReference)
        {
            var list = new List<MonitorData>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(hMon, ref info))
                    {
                        // Device px -> DIPs
                        var source = PresentationSource.FromVisual(dpiReference);
                        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

                        // Bounds
                        var bTL = fromDevice.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
                        var bBR = fromDevice.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));
                        // Work area
                        var wTL = fromDevice.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
                        var wBR = fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));

                        list.Add(new MonitorData
                        {
                            DeviceName = info.szDevice?.TrimEnd('\0') ?? "",
                            Primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                            BoundsDip = new Rect(bTL, bBR),
                            WorkAreaDip = new Rect(wTL, wBR)
                        });
                    }
                    return true;
                },
                IntPtr.Zero);

            return list;
        }

        #region Win32
        private const int MONITORINFOF_PRIMARY = 0x00000001;

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }
        #endregion
    }
}
