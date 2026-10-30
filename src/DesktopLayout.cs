using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace DesktopInteraction
{
    // Only interface geometry and its display label. No paths, PIDLs or file contents.
    public sealed class DesktopSlot
    {
        public readonly Rectangle Cell;
        public readonly Rectangle Image;
        public readonly Rectangle Label;
        public readonly string DisplayName;
        public DesktopSlot(Rectangle cell,Rectangle image,Rectangle label,string displayName="")
        {
            if(cell.Width<=0 || cell.Height<=0)throw new ArgumentException("Invalid desktop geometry");
            Cell=cell;Image=Rectangle.Intersect(cell,image);Label=Rectangle.Intersect(cell,label);
            DisplayName=displayName??string.Empty;
            if(DisplayName.Length>4096)throw new ArgumentException("Display label too long");
        }
    }

    public interface IDesktopGeometry
    {
        List<DesktopSlot> ReadOnce(CancellationToken token);
    }

    public sealed class DesktopGeometry : IDesktopGeometry
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string cls,string title);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
        private int used;

        private static IntPtr ListIn(IntPtr host)
        {
            if(host==IntPtr.Zero)return IntPtr.Zero;
            IntPtr view=FindWindowEx(host,IntPtr.Zero,"SHELLDLL_DefView",null);
            return view==IntPtr.Zero?IntPtr.Zero:FindWindowEx(view,IntPtr.Zero,"SysListView32",null);
        }
        private static IntPtr Container()
        {
            IntPtr list=ListIn(FindWindow("Progman",null));
            if(list!=IntPtr.Zero)return list;
            list=ListIn(GetShellWindow());if(list!=IntPtr.Zero)return list;
            IntPtr worker=IntPtr.Zero;
            for(int i=0;i<32;i++)
            {
                worker=FindWindowEx(IntPtr.Zero,worker,"WorkerW",null);
                if(worker==IntPtr.Zero)break;
                list=ListIn(worker);if(list!=IntPtr.Zero)return list;
            }
            IntPtr directView=FindWindow("SHELLDLL_DefView",null);
            return directView==IntPtr.Zero?IntPtr.Zero:FindWindowEx(directView,IntPtr.Zero,"SysListView32",null);
        }
        public List<DesktopSlot> ReadOnce(CancellationToken token)
        {
            if(Interlocked.Exchange(ref used,1)!=0)throw new InvalidOperationException("One-shot desktop geometry only");
            token.ThrowIfCancellationRequested();
            IntPtr container=Container();
            if(container==IntPtr.Zero)throw new DesktopLayoutUnavailableException();
            UIntPtr countValue;
            // LVM_GETITEMCOUNT returns a scalar. No cross-process buffers or process-memory APIs.
            if(SendMessageTimeout(container,0x1004,UIntPtr.Zero,IntPtr.Zero,0x0002,1500,out countValue)==IntPtr.Zero)
                throw new DesktopLayoutUnavailableException();
            ulong count=countValue.ToUInt64();
            if(count>4096)throw new DesktopLayoutUnavailableException();
            var list=AutomationElement.FromHandle(container);
            var cache=new CacheRequest {TreeScope=TreeScope.Element,TreeFilter=Automation.RawViewCondition,AutomationElementMode=AutomationElementMode.Full};
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.NameProperty);
            AutomationElementCollection items;
            using(cache.Activate())items=list.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
            // Refuse partial/virtualized enumeration rather than fabricating missing icons.
            if((ulong)items.Count!=count)throw new DesktopLayoutUnavailableException();
            var slots=new List<DesktopSlot>();
            var cells=new HashSet<Rectangle>();
            foreach(AutomationElement item in items)
            {
                token.ThrowIfCancellationRequested();
                Rectangle cell=Bounds(item);
                if(cell.IsEmpty)throw new DesktopLayoutUnavailableException();
                if(!cells.Add(cell))throw new DesktopLayoutUnavailableException();
                object nameValue=item.GetCachedPropertyValue(AutomationElement.NameProperty,true);
                if(nameValue==AutomationElement.NotSupported || !(nameValue is string))throw new DesktopLayoutUnavailableException();
                string displayName=(string)nameValue;
                if(displayName.Length>4096)throw new DesktopLayoutUnavailableException();
                Rectangle image=Rectangle.Empty,label=Rectangle.Empty;
                // Child geometry is a separate request; their names are not requested.
                var childCache=new CacheRequest {TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.None,TreeFilter=Automation.RawViewCondition};
                childCache.Add(AutomationElement.BoundingRectangleProperty);childCache.Add(AutomationElement.ControlTypeProperty);
                AutomationElementCollection children;
                using(childCache.Activate())children=item.FindAll(TreeScope.Children,Automation.RawViewCondition);
                foreach(AutomationElement child in children)
                {
                    object type=child.GetCachedPropertyValue(AutomationElement.ControlTypeProperty,true);
                    Rectangle region=Bounds(child);
                    if(region.IsEmpty)continue;
                    if(type==ControlType.Image)image=image.IsEmpty?region:Rectangle.Union(image,region);
                    if(type==ControlType.Text)label=label.IsEmpty?region:Rectangle.Union(label,region);
                }
                slots.Add(new DesktopSlot(cell,image,label,displayName));
            }
            return slots;
        }
        private static Rectangle Bounds(AutomationElement element)
        {
            object value=element.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty,true);
            if(value==AutomationElement.NotSupported)return Rectangle.Empty;
            var rect=(System.Windows.Rect)value;
            if(rect.IsEmpty || double.IsNaN(rect.X) || double.IsInfinity(rect.X) || double.IsNaN(rect.Y) || double.IsInfinity(rect.Y) ||
                rect.Width<1 || rect.Height<1 || rect.Width>4096 || rect.Height>4096)return Rectangle.Empty;
            return Rectangle.FromLTRB((int)Math.Floor(rect.Left),(int)Math.Floor(rect.Top),(int)Math.Ceiling(rect.Right),(int)Math.Ceiling(rect.Bottom));
        }
    }

    public sealed class DesktopLayoutUnavailableException : Exception
    {
        public DesktopLayoutUnavailableException():base("Desktop geometry unavailable; no pixel-guess fallback"){}
    }

    public static class SlotVisuals
    {
        public static Rectangle Local(Rectangle rectangle,Rectangle monitor)
        {
            if(rectangle.IsEmpty)return Rectangle.Empty;
            rectangle.Offset(-monitor.X,-monitor.Y);return rectangle;
        }
        public static void Draw(Graphics target,Frame frame,Theme theme,DesktopSlot slot,WindowsIconLabels labels)
        {
            Rectangle cell=Rectangle.Intersect(new Rectangle(Point.Empty,frame.Pixels.Size),Local(slot.Cell,frame.Bounds));
            if(cell.IsEmpty)return;
            Rectangle image=Local(slot.Image,frame.Bounds),label=Local(slot.Label,frame.Bounds);
            if(image.IsEmpty)
            {
                // Some providers expose only the whole item. Count/cell remain authoritative;
                // this bounded split affects visual sizing, never the number or grid position.
                int size=Math.Max(1,Math.Min((int)Math.Round(cell.Width*0.68),(int)Math.Round(cell.Height*0.55)));
                if(!label.IsEmpty)size=Math.Min(size,Math.Max(1,label.Top-cell.Top-2));
                image=new Rectangle(cell.X+(cell.Width-size)/2,cell.Top,size,size);
            }
            image=Rectangle.Intersect(cell,image);
            if(label.IsEmpty)label=Rectangle.FromLTRB(cell.Left,Math.Min(cell.Bottom,image.Bottom+1),cell.Right,cell.Bottom);
            label=Rectangle.Intersect(cell,label);
            // Draw only the returned interface label, on the new background. No source patch.
            if(!label.IsEmpty && slot.DisplayName.Length>0)
                labels.Draw(target,slot.DisplayName,label);
            if(theme.Ready && !image.IsEmpty)
            {
                double scale=Math.Min((double)image.Width/theme.Icon.Width,(double)image.Height/theme.Icon.Height);
                int w=Math.Max(1,(int)Math.Round(theme.Icon.Width*scale)),h=Math.Max(1,(int)Math.Round(theme.Icon.Height*scale));
                target.DrawImage(theme.Icon,new Rectangle(image.X+(image.Width-w)/2,image.Y+(image.Height-h)/2,w,h));
            }
        }
    }
}
