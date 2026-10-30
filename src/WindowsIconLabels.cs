using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DesktopInteraction
{
    // Read only the public icon-title display preference. Never read the registry.
    public sealed class WindowsIconLabels : IDisposable
    {
        [StructLayout(LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private sealed class LogFont
        {
            public int Height,Width,Escapement,Orientation,Weight;
            public byte Italic,Underline,StrikeOut,CharSet,OutPrecision,ClipPrecision,Quality,PitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string FaceName;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect
        {
            public int Left,Top,Right,Bottom;
            public Rect(Rectangle r){Left=r.Left;Top=r.Top;Right=r.Right;Bottom=r.Bottom;}
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        private static extern bool SystemParametersInfoForDpi(uint action,uint size,[In,Out] LogFont value,uint flags,uint dpi);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]
        private static extern bool SystemParametersInfo(uint action,uint size,out int value,uint flags);
        [DllImport("gdi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateFontIndirect(LogFont font);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc,int saved);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc,int mode);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc,uint color);
        [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr dc,int left,int top,int right,int bottom);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int DrawText(IntPtr dc,string text,int count,ref Rect rect,uint format);
        private IntPtr font;
        private readonly uint format;
        private readonly int shadowOffset;
        public readonly int FontHeight;
        public readonly string FontFace;
        public WindowsIconLabels(uint dpi)
        {
            if(dpi<48 || dpi>960)throw new ArgumentOutOfRangeException("dpi");
            var spec=new LogFont();
            if(!SystemParametersInfoForDpi(0x001F,(uint)Marshal.SizeOf(spec),spec,0,dpi))throw new Win32Exception();
            FontHeight=Math.Abs(spec.Height);FontFace=spec.FaceName;
            font=CreateFontIndirect(spec);if(font==IntPtr.Zero)throw new Win32Exception();
            int wrap;
            bool wrapping=!SystemParametersInfo(0x0019,0,out wrap,0) || wrap!=0;
            // GDI: center, literal ampersands, ellipsis, whole lines, system wrap setting.
            format=0x0001|0x0800|0x8000|(wrapping?0x0010u|0x2000u:0x0020u);
            shadowOffset=Math.Max(1,(int)Math.Round(dpi/96.0));
        }
        public void Draw(Graphics target,string text,Rectangle box)
        {
            if(font==IntPtr.Zero)throw new ObjectDisposedException("WindowsIconLabels");
            IntPtr dc=target.GetHdc();int saved=SaveDC(dc);
            try
            {
                SelectObject(dc,font);SetBkMode(dc,1);
                IntersectClipRect(dc,box.Left,box.Top,box.Right,box.Bottom);
                Rectangle shifted=box;shifted.Offset(shadowOffset,shadowOffset);
                var shadow=new Rect(shifted);SetTextColor(dc,0x00222222);
                DrawText(dc,text,text.Length,ref shadow,format);
                var foreground=new Rect(box);SetTextColor(dc,0x00FFFFFF);
                DrawText(dc,text,text.Length,ref foreground,format);
            }
            finally{RestoreDC(dc,saved);target.ReleaseHdc(dc);}
        }
        public void Dispose(){if(font!=IntPtr.Zero){DeleteObject(font);font=IntPtr.Zero;}}
    }
}
