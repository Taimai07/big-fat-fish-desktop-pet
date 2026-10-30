using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopInteraction;

public static class GeometryCases
{
    sealed class FakeGeometry : IDesktopGeometry
    {
        public int Reads;
        public List<DesktopSlot> ReadOnce(CancellationToken token)
        {
            Reads++;token.ThrowIfCancellationRequested();
            return new List<DesktopSlot>{new DesktopSlot(new Rectangle(20,20,80,90),new Rectangle(35,20,40,40),new Rectangle(20,65,80,35))};
        }
    }
    sealed class GeometrySource : IFrameSource
    {
        public readonly FakeGeometry Geometry=new FakeGeometry();
        public Task<List<Frame>> CaptureOnceAsync(CancellationToken token)
        {
            var slots=Geometry.ReadOnce(token);
            var bitmap=new Bitmap(300,200);
            using(var g=Graphics.FromImage(bitmap))g.Clear(Color.Navy);
            return Task.FromResult(new List<Frame>{new Frame(new Rectangle(0,0,300,200),new Rectangle(0,0,300,200),bitmap,slots)});
        }
    }
    public static void Run(string output,Action<bool,string> check)
    {
        using(var bg=new Bitmap(40,40))
        using(var icon=new Bitmap(20,20))
        {
            using(var g=Graphics.FromImage(bg))g.Clear(Color.Blue);
            using(var g=Graphics.FromImage(icon))g.Clear(Color.Red);
            using(var theme=new Theme(new Bitmap(bg),new Bitmap(icon)))
            {
                var source=new GeometrySource();
                var denied=Session.PrepareAsync(false,source,theme,CancellationToken.None).GetAwaiter().GetResult();
                check(source.Geometry.Reads==0 && denied.Count==0,"denial prevents even read-only desktop geometry access");
                var allowed=Task.Run(()=>Session.PrepareAsync(true,source,theme,CancellationToken.None)).GetAwaiter().GetResult();
                check(source.Geometry.Reads==1 && allowed.Count==1,"explicitly allowed pipeline reads geometry exactly once");
                foreach(var frame in allowed)frame.Dispose();
                var bounds=new Rectangle(-500,-100,600,400);
                var slots=new List<DesktopSlot>{
                    new DesktopSlot(new Rectangle(-480,-70,90,100),new Rectangle(-460,-70,40,40),new Rectangle(-480,-20,90,40),"应用示例 A"),
                    new DesktopSlot(new Rectangle(-170,77,130,110),new Rectangle(-130,77,50,50),new Rectangle(-170,134,130,45),"示例 B\n第二行")};
                using(var pixels=new Bitmap(600,400))
                {
                    using(var g=Graphics.FromImage(pixels))
                    {
                        g.Clear(Color.DarkGreen);
                        // Unrelated wallpaper details must not produce extra replacement icons.
                        for(int i=0;i<8;i++)g.FillEllipse(Brushes.White,110+i*45,20,24,24);
                        for(int i=0;i<slots.Count;i++)
                        {
                            Rectangle label=SlotVisuals.Local(slots[i].Label,bounds);
                            g.FillRectangle(i==0?Brushes.DarkSlateGray:Brushes.Purple,label);
                            using(var font=new Font("Microsoft YaHei UI",10))g.DrawString(i==0?"应用示例 A":"示例 B\n第二行",font,Brushes.White,label);
                        }
                    }
                    using(var frame=new Frame(bounds,bounds,new Bitmap(pixels),slots))
                    using(var rendered=Composer.Render(frame,theme,CancellationToken.None))
                    {
                        check(rendered.GetPixel(60,50).ToArgb()==Color.Red.ToArgb() && rendered.GetPixel(395,200).ToArgb()==Color.Red.ToArgb(),"two irregular authoritative positions map correctly across negative monitor coordinates");
                        check(RedComponents(rendered)==2,"exactly two replacement icon regions are rendered for two supplied slots");
                        bool oldPatchRemoved=true;int whiteText=0;
                        foreach(var slot in slots)
                        {
                            Rectangle label=SlotVisuals.Local(slot.Label,bounds);
                            for(int y=label.Top;y<label.Bottom;y++)for(int x=label.Left;x<label.Right;x++)
                            {
                                var color=rendered.GetPixel(x,y);
                                if(color.ToArgb()==Color.DarkSlateGray.ToArgb() || color.ToArgb()==Color.Purple.ToArgb())oldPatchRemoved=false;
                                if(color.R>210 && color.G>210 && color.B>210)whiteText++;
                            }
                        }
                        check(oldPatchRemoved,"original wallpaper patches are not copied behind names");
                        check(whiteText>25,"display-name text including multiline labels is drawn on the new background");
                        check(rendered.GetPixel(120,30).ToArgb()==Color.Blue.ToArgb(),"wallpaper shapes do not become extra desktop icons in geometry mode");
                        rendered.Save(Path.Combine(output,"geometry-label-preservation-preview.png"),ImageFormat.Png);
                    }
                    using(var frame=new Frame(bounds,bounds,new Bitmap(pixels),new List<DesktopSlot>()))
                    using(var rendered=Composer.Render(frame,theme,CancellationToken.None))
                        check(rendered.GetPixel(120,30).ToArgb()==Color.Blue.ToArgb(),"authoritative zero-item desktop does not fall back to guessing");
                }
                using(var frame=MockSource.CreateNamed(new Rectangle(0,0,960,600),new Rectangle(0,0,960,564),6))
                using(var rendered=Composer.Render(frame,theme,CancellationToken.None))
                {
                    check(frame.Slots!=null && frame.Slots.Count==6,"named preview uses six explicit mock slots rather than pixel inference");
                    rendered.Save(Path.Combine(output,"named-preview.png"),ImageFormat.Png);
                }
            }
        }
    }
    private static int RedComponents(Bitmap image)
    {
        int w=image.Width,h=image.Height,count=0;
        var mask=new bool[w*h];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            var c=image.GetPixel(x,y);mask[y*w+x]=c.R>200 && c.G<50 && c.B<50;
        }
        var queue=new int[w*h];
        for(int start=0;start<mask.Length;start++)
        {
            if(!mask[start])continue;
            count++;int head=0,tail=1;queue[0]=start;mask[start]=false;
            while(head<tail)
            {
                int p=queue[head++],x=p%w,y=p/w;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int nx=x+dx,ny=y+dy;
                    if(nx<0 || nx>=w || ny<0 || ny>=h)continue;
                    int n=ny*w+nx;if(mask[n]){mask[n]=false;queue[tail++]=n;}
                }
            }
        }
        return count;
    }
}
