using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopInteraction
{
    // Only pixel buffers and monitor geometry cross this boundary. No shell items or filenames.
    public sealed class Frame : IDisposable
    {
        public readonly Rectangle Bounds;
        public readonly Rectangle Work;
        public Bitmap Pixels { get; private set; }
        public readonly List<DesktopSlot> Slots;
        public readonly uint Dpi;
        public Frame(Rectangle bounds, Rectangle work, Bitmap pixels,List<DesktopSlot> slots=null,uint dpi=96)
        {
            if (pixels.Width != bounds.Width || pixels.Height != bounds.Height)
                throw new ArgumentException("Frame size mismatch");
            Bounds = bounds; Work = Rectangle.Intersect(bounds, work); Pixels = pixels;Slots=slots;Dpi=dpi;
        }
        public void Dispose() { if (Pixels != null) { Pixels.Dispose(); Pixels = null; } }
    }

    public interface IFrameSource
    {
        Task<List<Frame>> CaptureOnceAsync(CancellationToken token);
    }

    public sealed class Theme : IDisposable
    {
        public Bitmap Background { get; private set; }
        public Bitmap Icon { get; private set; }
        public bool Ready { get { return Background != null && Icon != null; } }
        public Theme(Bitmap background, Bitmap icon) { Background = background; Icon = icon; }
        public void Dispose()
        {
            if (Background != null) Background.Dispose();
            if (Icon != null) Icon.Dispose();
            Background = null; Icon = null;
        }
    }

    public static class PixelIcons
    {
        // Pixel geometry only: text-like light strokes anchor icons on nonuniform wallpaper.
        // Glyphs are never decoded. Sparse or visually ambiguous scenes may still be missed.
        public static List<Rectangle> Find(Bitmap input, Rectangle area, CancellationToken token)
        {
            var labeled=LabelGeometry.Find(input,area,token);
            var simple=FindSimple(input,area,token);
            foreach(var box in simple)
            {
                bool duplicate=false;
                foreach(var existing in labeled)
                    if(Math.Abs(existing.X+existing.Width/2-box.X-box.Width/2)<Math.Max(existing.Width,box.Width)*0.6 &&
                       Math.Abs(existing.Y+existing.Height/2-box.Y-box.Height/2)<Math.Max(existing.Height,box.Height)*0.6) { duplicate=true; break; }
                if(!duplicate) labeled.Add(box);
            }
            return labeled;
        }
        private static List<Rectangle> FindSimple(Bitmap input, Rectangle area, CancellationToken token)
        {
            var found = new List<Rectangle>();
            area = Rectangle.Intersect(new Rectangle(Point.Empty, input.Size), area);
            if (area.Width < 16 || area.Height < 16) return found;
            double scale = Math.Min(1.0, 960.0 / Math.Max(area.Width, area.Height));
            int w = Math.Max(1, (int)(area.Width * scale));
            int h = Math.Max(1, (int)(area.Height * scale));
            using (var small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImage(input, new Rectangle(0, 0, w, h), area, GraphicsUnit.Pixel);
                }
                BitmapData data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] bytes = new byte[data.Stride * h];
                try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
                finally { small.UnlockBits(data); }
                int[] colors = new int[32768];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * data.Stride + x * 4;
                        colors[((bytes[i+2] >> 3) << 10) | ((bytes[i+1] >> 3) << 5) | (bytes[i] >> 3)]++;
                    }
                int mode = 0;
                for (int i = 1; i < colors.Length; i++) if (colors[i] > colors[mode]) mode = i;
                if (colors[mode] < w * h * 0.55) return found;
                int r = ((mode >> 10) & 31) * 8 + 4, green = ((mode >> 5) & 31) * 8 + 4, b = (mode & 31) * 8 + 4;
                bool[] mask = new bool[w * h];
                for (int y = 0; y < h; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * data.Stride + x * 4;
                        mask[y*w+x] = Math.Abs(bytes[i+2]-r) + Math.Abs(bytes[i+1]-green) + Math.Abs(bytes[i]-b) > 100;
                    }
                }
                // Each pixel enters the queue once. Bounded to <= 960 x 960.
                int[] queue = new int[w*h];
                for (int start = 0; start < mask.Length; start++)
                {
                    if ((start & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (!mask[start]) continue;
                    int head = 0, tail = 1, minX = start%w, maxX = minX, minY = start/w, maxY = minY;
                    queue[0] = start; mask[start] = false;
                    while (head < tail)
                    {
                        if ((head & 4095) == 0) token.ThrowIfCancellationRequested();
                        int p = queue[head++], x = p%w, y = p/w;
                        minX = Math.Min(minX,x); maxX = Math.Max(maxX,x); minY = Math.Min(minY,y); maxY = Math.Max(maxY,y);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx=x+dx, ny=y+dy;
                                if (nx<0 || nx>=w || ny<0 || ny>=h) continue;
                                int n=ny*w+nx;
                                if (mask[n]) { mask[n]=false; queue[tail++]=n; }
                            }
                    }
                    double width=(maxX-minX+1)/scale, height=(maxY-minY+1)/scale;
                    // Supports common 24..128 physical-pixel icons. Not a general desktop parser.
                    if (width<24 || width>128 || height<24 || height>128 || width/height<0.5 || width/height>2.0) continue;
                    if (tail < (maxX-minX+1)*(maxY-minY+1)*0.18) continue;
                    found.Add(new Rectangle(area.X+(int)(minX/scale), area.Y+(int)(minY/scale), (int)Math.Ceiling(width), (int)Math.Ceiling(height)));
                    if (found.Count>200) return new List<Rectangle>();
                }
            }
            return found;
        }
    }

    public static class LabelGeometry
    {
        // Local contrast + horizontal glyph grouping. No OCR or grid-only fabricated positions.
        public static List<Rectangle> Find(Bitmap input,Rectangle area,CancellationToken token)
        {
            var result=new List<Rectangle>();
            area=Rectangle.Intersect(new Rectangle(Point.Empty,input.Size),area);
            if(area.Width<32 || area.Height<32) return result;
            double scale=Math.Min(1.0,1280.0/Math.Max(area.Width,area.Height));
            int w=Math.Max(1,(int)(area.Width*scale)),h=Math.Max(1,(int)(area.Height*scale));
            byte[] rgb;
            using(var small=new Bitmap(w,h,PixelFormat.Format32bppArgb))
            {
                using(var g=Graphics.FromImage(small)) g.DrawImage(input,new Rectangle(0,0,w,h),area,GraphicsUnit.Pixel);
                var data=small.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                try { rgb=new byte[w*h*4]; Marshal.Copy(data.Scan0,rgb,0,rgb.Length); }
                finally { small.UnlockBits(data); }
            }
            var strokes=new bool[w*h];
            for(int y=3;y<h-3;y++)
            {
                token.ThrowIfCancellationRequested();
                for(int x=3;x<w-3;x++)
                {
                    int p=(y*w+x)*4;
                    int low=Math.Min(rgb[p],Math.Min(rgb[p+1],rgb[p+2])),high=Math.Max(rgb[p],Math.Max(rgb[p+1],rgb[p+2]));
                    if(low<155 || high-low>68) continue;
                    int light=(rgb[p]+rgb[p+1]+rgb[p+2])/3;
                    bool darkNeighbor=false;
                    for(int dy=-2;dy<=2 && !darkNeighbor;dy++) for(int dx=-2;dx<=2;dx++)
                    {
                        int n=((y+dy)*w+x+dx)*4;
                        if(light-(rgb[n]+rgb[n+1]+rgb[n+2])/3>65) { darkNeighbor=true; break; }
                    }
                    strokes[y*w+x]=darkNeighbor;
                }
            }
            // Join glyphs along a line, while retaining the measured undilated stroke bounds.
            var joined=new bool[w*h];
            int radius=3;
            for(int y=2;y<h-2;y++) for(int x=radius;x<w-radius;x++) if(strokes[y*w+x])
                for(int dy=-1;dy<=1;dy++) for(int dx=-radius;dx<=radius;dx++) joined[(y+dy)*w+x+dx]=true;
            var labels=new List<Rectangle>();
            int[] queue=new int[w*h];
            for(int start=0;start<joined.Length;start++)
            {
                if((start&4095)==0) token.ThrowIfCancellationRequested();
                if(!joined[start]) continue;
                int head=0,tail=1,minX=w,minY=h,maxX=-1,maxY=-1,count=0;
                queue[0]=start;joined[start]=false;
                while(head<tail)
                {
                    if((head&4095)==0)token.ThrowIfCancellationRequested();
                    int p=queue[head++],x=p%w,y=p/w;
                    if(strokes[p]) { minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);count++; }
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=x+dx,ny=y+dy;
                        if(nx<0 || nx>=w || ny<0 || ny>=h)continue;
                        int n=ny*w+nx;
                        if(joined[n]){joined[n]=false;queue[tail++]=n;}
                    }
                }
                int bw=maxX-minX+1,bh=maxY-minY+1;
                if(bw<12 || bw>220 || bh<6 || bh>30 || count<8)continue;
                double density=(double)count/(bw*bh);
                if(density<0.10 || density>0.74 || bw<bh*1.05)continue;
                labels.Add(new Rectangle(minX,minY,bw,bh));
            }
            labels.Sort((a,b)=>a.Y.CompareTo(b.Y));
            var candidates=new List<Rectangle>();
            var scores=new Dictionary<Rectangle,double>();
            foreach(var label in labels)
            {
                int cx=label.X+label.Width/2;
                bool secondLine=false;
                foreach(var prior in labels)
                {
                    if(prior.Y>=label.Y)break;
                    if(label.Y-prior.Bottom>=0 && label.Y-prior.Bottom<Math.Max(2,Math.Min(prior.Height,label.Height)*0.35) &&
                       Math.Abs(cx-prior.X-prior.Width/2)<Math.Max(8,label.Height)) { secondLine=true;break; }
                }
                if(secondLine)continue;
                int size=(int)Math.Round(Math.Max(24,Math.Min(128,label.Height*4.0)));
                int gap=Math.Max(2,(int)Math.Round(label.Height*0.35));
                Rectangle patch=Rectangle.Intersect(new Rectangle(0,0,w,h),new Rectangle(cx-size/2,label.Y-size-gap,size,size));
                if(patch.Width<size*0.8 || patch.Height<size*0.8)continue;
                int edges=0,valid=0;
                int[] edgeRows=new int[patch.Height],edgeCols=new int[patch.Width];
                for(int y=patch.Top+1;y<patch.Bottom-1;y++)for(int x=patch.Left+1;x<patch.Right-1;x++)
                {
                    int p=(y*w+x)*4,l=p-4,u=p-w*4;
                    int gradient=0;
                    for(int c=0;c<3;c++) gradient+=Math.Abs(rgb[p+c]-rgb[l+c])+Math.Abs(rgb[p+c]-rgb[u+c]);
                    if(gradient>95){edges++;edgeRows[y-patch.Top]++;edgeCols[x-patch.Left]++;}
                    valid++;
                }
                if(edges<valid*0.045)continue;
                int activeRows=0,activeCols=0;
                foreach(int count in edgeRows)if(count>=Math.Max(2,patch.Width*0.035))activeRows++;
                foreach(int count in edgeCols)if(count>=Math.Max(2,patch.Height*0.035))activeCols++;
                if(activeRows<patch.Height*0.58 || activeCols<patch.Width*0.42)continue;
                candidates.Add(patch);
                scores[patch]=(double)edges/valid+(double)activeRows/patch.Height*0.25;
            }
            candidates.Sort((a,b)=>scores[b].CompareTo(scores[a]));
            foreach(var patch in candidates)
            {
                int cx=patch.X+patch.Width/2,cy=patch.Y+patch.Height/2;
                int aligned=0;
                foreach(var other in candidates)
                    if(Math.Abs(cx-other.X-other.Width/2)<Math.Max(10,Math.Min(patch.Width,other.Width)*0.22) &&
                       Math.Abs(cy-other.Y-other.Height/2)>patch.Height*0.8)aligned++;
                if(aligned==0)continue;
                var box=new Rectangle(area.X+(int)Math.Round(patch.X/scale),area.Y+(int)Math.Round(patch.Y/scale),
                    (int)Math.Round(patch.Width/scale),(int)Math.Round(patch.Height/scale));
                bool duplicate=false;
                foreach(var prior in result)
                    if(Math.Abs(prior.X+prior.Width/2-box.X-box.Width/2)<Math.Max(prior.Width,box.Width)*0.65 &&
                       Math.Abs(prior.Y+prior.Height/2-box.Y-box.Height/2)<Math.Max(prior.Height,box.Height)*1.15) {duplicate=true;break;}
                if(!duplicate)result.Add(box);
                if(result.Count>250)return new List<Rectangle>();
            }
            return RefineAligned(result,rgb,strokes,w,h,area,scale,token);
        }
        private sealed class AxisGroup
        {
            public readonly List<int> Values=new List<int>();
            public int Center {get {var values=Values.OrderBy(x=>x).ToArray();return values[values.Length/2];}}
        }
        private static List<AxisGroup> Groups(IEnumerable<int> positions,int tolerance)
        {
            var groups=new List<AxisGroup>();
            foreach(int position in positions.OrderBy(x=>x))
            {
                AxisGroup group=groups.Count==0?null:groups[groups.Count-1];
                if(group==null || Math.Abs(group.Center-position)>tolerance){group=new AxisGroup();groups.Add(group);}
                group.Values.Add(position);
            }
            return groups.Where(g=>g.Values.Count>=2).ToList();
        }
        private static List<Rectangle> RefineAligned(List<Rectangle> measured,byte[] rgb,bool[] strokes,int w,int h,Rectangle area,double scale,CancellationToken token)
        {
            if(measured.Count<6)return measured;
            int[] widths=measured.Select(r=>(int)Math.Round(r.Width*scale)).OrderBy(x=>x).ToArray();
            int size=widths[widths.Length/2];
            var columns=Groups(measured.Select(r=>(int)Math.Round((r.X-area.X+r.Width/2)*scale)),(int)(size*0.65));
            var rows=Groups(measured.Select(r=>(int)Math.Round((r.Y-area.Y+r.Height/2)*scale)),(int)(size*0.65));
            if(columns.Count<2 || rows.Count<3)return measured;
            // Repeated spacing may propose a missing row, but each cell is still validated below.
            var steps=new List<int>();
            for(int i=1;i<rows.Count;i++)
            {
                int distance=rows[i].Center-rows[i-1].Center;
                if(distance>size*1.25 && distance<size*3)steps.Add(distance);
            }
            if(steps.Count>=3)
            {
                steps.Sort();int spacing=steps[(steps.Count-1)/2];
                var expanded=new List<AxisGroup>();
                for(int i=0;i<rows.Count;i++)
                {
                    if(i>0)
                    {
                        int distance=rows[i].Center-rows[i-1].Center,n=(int)Math.Round((double)distance/spacing);
                        if(n>=2 && n<=4 && Math.Abs(distance-n*spacing)<spacing*0.30)
                            for(int j=1;j<n;j++){var inferred=new AxisGroup();inferred.Values.Add(rows[i-1].Center+distance*j/n);expanded.Add(inferred);}
                    }
                    expanded.Add(rows[i]);
                }
                rows=expanded;
            }
            var refined=new List<Rectangle>();
            foreach(var row in rows)foreach(var col in columns)
            {
                token.ThrowIfCancellationRequested();
                int cx=col.Center,cy=row.Center;
                var patch=Rectangle.Intersect(new Rectangle(1,1,w-2,h-2),new Rectangle(cx-size/2,cy-size/2,size,size));
                if(patch.Width<size*0.9 || patch.Height<size*0.9)continue;
                int edges=0,activeRows=0;
                for(int y=patch.Top;y<patch.Bottom;y++)
                {
                    int count=0;
                    for(int x=patch.Left;x<patch.Right;x++)
                    {
                        int p=(y*w+x)*4,gradient=0;
                        for(int c=0;c<3;c++)gradient+=Math.Abs(rgb[p+c]-rgb[p-4+c])+Math.Abs(rgb[p+c]-rgb[p-w*4+c]);
                        if(gradient>95){count++;edges++;}
                    }
                    if(count>=Math.Max(2,size*0.035))activeRows++;
                }
                int labelStrokes=0;
                for(int y=Math.Max(0,patch.Bottom-(int)(size*0.25));y<Math.Min(h,patch.Bottom+(int)(size*0.8));y++)
                    for(int x=Math.Max(0,cx-size);x<Math.Min(w,cx+size);x++)if(strokes[y*w+x])labelStrokes++;
                // Every inferred cell needs its own measured icon detail AND label strokes.
                // This never emits an unobserved empty grid cell.
                if(edges<patch.Width*patch.Height*0.045 || activeRows<size*0.58 || labelStrokes<size*0.4)continue;
                refined.Add(new Rectangle(area.X+(int)Math.Round(patch.X/scale),area.Y+(int)Math.Round(patch.Y/scale),
                    (int)Math.Round(patch.Width/scale),(int)Math.Round(patch.Height/scale)));
            }
            return refined.Count>=measured.Count*0.7?refined:measured;
        }
    }

    public static class TaskbarPixels
    {
        public static List<Rectangle> Find(Bitmap input,Rectangle band,CancellationToken token)
        {
            var result=new List<Rectangle>();
            band=Rectangle.Intersect(new Rectangle(Point.Empty,input.Size),band);
            bool horizontal=band.Width>=band.Height;
            int length=horizontal?band.Width:band.Height,thickness=horizontal?band.Height:band.Width;
            if(thickness<16 || thickness>160 || length<thickness*5)return result;
            int start=Math.Min(length/4,Math.Max(55,(int)(thickness*2.8))),end=length-Math.Min(length/4,Math.Max(110,thickness*4));
            var mask=new bool[length*thickness];
            Func<int,int,Color> pixel=(x,y)=>input.GetPixel(band.X+(horizontal?x:y),band.Y+(horizontal?y:x));
            for(int y=4;y<thickness-4;y++)
            {
                token.ThrowIfCancellationRequested();
                int[] histogram=new int[32768];
                for(int x=start;x<end;x++)
                {
                    Color c=pixel(x,y);
                    histogram[((c.R>>3)<<10)|((c.G>>3)<<5)|(c.B>>3)]++;
                }
                int mode=0;
                for(int i=1;i<histogram.Length;i++)if(histogram[i]>histogram[mode])mode=i;
                int r=((mode>>10)&31)*8+4,green=((mode>>5)&31)*8+4,b=(mode&31)*8+4;
                for(int x=start;x<end;x++)
                {
                    Color c=pixel(x,y);
                    int diff=Math.Abs(c.R-r)+Math.Abs(c.G-green)+Math.Abs(c.B-b);
                    mask[y*length+x]=diff>72;
                }
            }
            var joined=(bool[])mask.Clone();
            for(int y=1;y<thickness-1;y++)for(int x=start+1;x<end-1;x++)if(mask[y*length+x])
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)joined[(y+dy)*length+x+dx]=true;
            int[] queue=new int[joined.Length];
            for(int s=0;s<joined.Length;s++)
            {
                if(!joined[s])continue;
                int head=0,tail=1,minX=s%length,maxX=minX,minY=s/length,maxY=minY;
                queue[0]=s;joined[s]=false;
                while(head<tail)
                {
                    int p=queue[head++],x=p%length,y=p/length;
                    minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int nx=x+dx,ny=y+dy;
                        if(nx<0 || nx>=length || ny<0 || ny>=thickness)continue;
                        int n=ny*length+nx;if(joined[n]){joined[n]=false;queue[tail++]=n;}
                    }
                }
                int width=maxX-minX+1,height=maxY-minY+1;
                if(width<thickness*0.25 || width>thickness*1.7 || height<thickness*0.3 || height>thickness*0.94)continue;
                if((double)width/height<0.4 || (double)width/height>2.2)continue;
                int size=Math.Min(thickness-8,Math.Max(width,height));
                int center=(minX+maxX)/2;
                result.Add(horizontal?new Rectangle(band.X+center-size/2,band.Y+(thickness-size)/2,size,size):
                    new Rectangle(band.X+(thickness-size)/2,band.Y+center-size/2,size,size));
            }
            return result;
        }
        public static List<Rectangle> Bands(Rectangle full,Rectangle work)
        {
            var list=new List<Rectangle>();
            if(work.Top>full.Top)list.Add(Rectangle.FromLTRB(full.Left,full.Top,full.Right,work.Top));
            if(work.Bottom<full.Bottom)list.Add(Rectangle.FromLTRB(full.Left,work.Bottom,full.Right,full.Bottom));
            if(work.Left>full.Left)list.Add(Rectangle.FromLTRB(full.Left,work.Top,work.Left,work.Bottom));
            if(work.Right<full.Right)list.Add(Rectangle.FromLTRB(work.Right,work.Top,full.Right,work.Bottom));
            return list;
        }
    }

    public static class Composer
    {
        public static Bitmap Render(Frame frame, Theme theme, CancellationToken token)
        {
            Rectangle localWork = frame.Work;
            localWork.Offset(-frame.Bounds.X,-frame.Bounds.Y);
            List<Rectangle> icons = frame.Slots==null?PixelIcons.Find(frame.Pixels,localWork,token):new List<Rectangle>();
            var result = new Bitmap(frame.Bounds.Width,frame.Bounds.Height,PixelFormat.Format32bppArgb);
            try
            {
                using (var g=Graphics.FromImage(result))
                {
                    g.Clear(Color.FromArgb(235,235,235));
                    if (theme.Ready)
                    {
                        // Reference places its character at the lower right. Preserve the artwork;
                        // extend its existing left-edge blue pixels across the empty desktop area.
                        g.Clear(theme.Background.GetPixel(0,0));
                        double s=Math.Min((double)localWork.Width/theme.Background.Width,(double)localWork.Height/theme.Background.Height);
                        int dw=(int)(theme.Background.Width*s), dh=(int)(theme.Background.Height*s);
                        int dy=localWork.Bottom-dh;
                        g.InterpolationMode=InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode=PixelOffsetMode.Half;
                        g.DrawImage(theme.Background,new Rectangle(localWork.X,dy,localWork.Width,dh),new Rectangle(0,0,1,theme.Background.Height),GraphicsUnit.Pixel);
                        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                        using(var attributes=new ImageAttributes())
                        {
                            attributes.SetWrapMode(WrapMode.TileFlipXY);
                            g.DrawImage(theme.Background,new Rectangle(localWork.Right-dw,dy,dw,dh),0,0,theme.Background.Width,theme.Background.Height,GraphicsUnit.Pixel,attributes);
                        }
                    }
                    else
                    {
                        using(var font=new Font("Microsoft YaHei UI",16))
                            g.DrawString("模拟画面 · 原参考素材待接入",font,Brushes.DimGray,150,75);
                    }
                    // Preserve only pixels outside working area (taskbar/appbar) on the visual copy.
                    using(var region=new Region(new Rectangle(Point.Empty,result.Size)))
                    {
                        region.Exclude(localWork); g.SetClip(region,CombineMode.Replace);
                        g.DrawImageUnscaled(frame.Pixels,0,0); g.ResetClip();
                    }
                    if(theme.Ready)
                    {
                        foreach(var band in TaskbarPixels.Bands(new Rectangle(Point.Empty,result.Size),localWork))
                        foreach(var box in TaskbarPixels.Find(frame.Pixels,band,token))
                        {
                            // Replace only the visual copy; infer the bar color from adjacent pixels.
                            for(int y=box.Top;y<box.Bottom;y++)
                            {
                                Color left=frame.Pixels.GetPixel(Math.Max(band.Left,box.Left-2),y);
                                Color right=frame.Pixels.GetPixel(Math.Min(band.Right-1,box.Right+2),y);
                                using(var pen=new Pen(Color.FromArgb((left.R+right.R)/2,(left.G+right.G)/2,(left.B+right.B)/2)))
                                    g.DrawLine(pen,box.Left,y,box.Right-1,y);
                            }
                            DrawIcon(g,theme.Icon,box);
                        }
                    }
                    if(frame.Slots!=null)
                    {
                        using(var labels=new WindowsIconLabels(frame.Dpi))
                            foreach(var slot in frame.Slots){token.ThrowIfCancellationRequested();SlotVisuals.Draw(g,frame,theme,slot,labels);}
                    }
                    foreach(var box in icons)
                    {
                        token.ThrowIfCancellationRequested();
                        if(theme.Ready)
                        {
                            DrawIcon(g,theme.Icon,box);
                        }
                        else { g.FillRectangle(Brushes.White,box); g.DrawRectangle(Pens.Gray,box); }
                    }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        private static void DrawIcon(Graphics g,Bitmap icon,Rectangle box)
        {
            double scale=Math.Min((double)box.Width/icon.Width,(double)box.Height/icon.Height);
            int w=Math.Max(1,(int)Math.Round(icon.Width*scale)),h=Math.Max(1,(int)Math.Round(icon.Height*scale));
            g.DrawImage(icon,new Rectangle(box.X+(box.Width-w)/2,box.Y+(box.Height-h)/2,w,h));
        }
    }

    public static class Session
    {
        // The sole collection entrypoint: denial never calls the source.
        public static async Task<List<Frame>> PrepareAsync(bool allowed,IFrameSource source,Theme theme,CancellationToken token)
        {
            var rendered=new List<Frame>();
            if(!allowed) return rendered;
            token.ThrowIfCancellationRequested();
            List<Frame> originals=await source.CaptureOnceAsync(token);
            try
            {
                foreach(var f in originals)
                {
                    token.ThrowIfCancellationRequested();
                    Bitmap image=await Task.Run(()=>Composer.Render(f,theme,token),token);
                    rendered.Add(new Frame(f.Bounds,f.Work,image,null,f.Dpi));
                }
                return rendered;
            }
            catch { foreach(var f in rendered) f.Dispose(); throw; }
            finally { foreach(var f in originals) f.Dispose(); }
        }
    }

    public sealed class MockSource : IFrameSource
    {
        public int Calls;
        public Task<List<Frame>> CaptureOnceAsync(CancellationToken token)
        {
            Calls++;
            var frames=new List<Frame>();
            try
            {
                frames.Add(Create(new Rectangle(0,0,960,600),6));
                token.ThrowIfCancellationRequested();
                frames.Add(Create(new Rectangle(-800,40,800,600),3));
                return Task.FromResult(frames);
            }
            catch { foreach(var f in frames) f.Dispose(); throw; }
        }
        public static Frame Create(Rectangle bounds,int count)
        {
            var bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(45,55,65));
                for(int i=0;i<count;i++)
                {
                    int x=24+(i/4)*100,y=24+(i%4)*108;
                    g.FillRectangle(Brushes.Gold,x,y,40,40);
                    // Synthetic label; not a real filename.
                    g.FillRectangle(Brushes.White,x,y+48,40,3);
                }
                g.FillRectangle(Brushes.Black,0,bounds.Height-36,bounds.Width,36);
                for(int i=0;i<4;i++)g.FillEllipse(i%2==0?Brushes.Lime:Brushes.Orange,120+i*52,bounds.Height-30,24,24);
            }
            return new Frame(bounds,new Rectangle(bounds.X,bounds.Y,bounds.Width,bounds.Height-36),bitmap);
        }
        public static Frame CreateNamed(Rectangle bounds,Rectangle work,int count)
        {
            using(var source=Create(bounds,count))
            {
                var bitmap=new Bitmap(source.Pixels);
                var slots=new List<DesktopSlot>();
                using(var g=Graphics.FromImage(bitmap))
                using(var font=new Font("Microsoft YaHei UI",10))
                using(var clear=new SolidBrush(Color.FromArgb(45,55,65)))
                {
                    for(int i=0;i<count;i++)
                    {
                        int x=24+(i/4)*100,y=24+(i%4)*108;
                        var label=new Rectangle(x-10,y+44,80,26);
                        g.FillRectangle(clear,label);
                        g.DrawString("示例 "+(i+1),font,Brushes.White,label.X+4,label.Y+2);
                        slots.Add(new DesktopSlot(new Rectangle(bounds.X+x-10,bounds.Y+y,80,70),
                            new Rectangle(bounds.X+x,bounds.Y+y,40,40),
                            new Rectangle(bounds.X+label.X,bounds.Y+label.Y,label.Width,label.Height),"示例 "+(i+1)));
                    }
                }
                return new Frame(bounds,work,bitmap,slots);
            }
        }
    }
}
