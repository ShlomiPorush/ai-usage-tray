using System.Runtime.InteropServices;
using System.Windows;

namespace costats.App.Services;

public sealed class TaskbarPositionService
{
    /// <summary>
    /// Returns the position for the popup widget: a small offset above the
    /// bottom-right corner of the work area.
    /// </summary>
    public Point GetWidgetPosition(double width, double height, double margin)
    {
        if (TryGetTaskbarEdge(out var edge, out var workArea))
        {
            return edge switch
            {
                TaskbarEdge.Top => new Point(workArea.Right - width - margin, workArea.Top + margin),
                TaskbarEdge.Left => new Point(workArea.Left + margin, workArea.Bottom - height - margin),
                TaskbarEdge.Right => new Point(workArea.Right - width - margin, workArea.Bottom - height - margin),
                _ => new Point(workArea.Right - width - margin, workArea.Bottom - height - margin)
            };
        }

        var fallback = SystemParameters.WorkArea;
        return new Point(fallback.Right - width - margin, fallback.Bottom - height - margin);
    }

    private static bool TryGetTaskbarEdge(out TaskbarEdge edge, out Rect workArea)
    {
        edge = TaskbarEdge.Bottom;
        workArea = SystemParameters.WorkArea;

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>()
        };

        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0)
        {
            return false;
        }

        edge = (TaskbarEdge)data.uEdge;
        workArea = SystemParameters.WorkArea;
        return true;
    }

    private enum TaskbarEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    private const int ABM_GETTASKBARPOS = 0x00000005;

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
}
