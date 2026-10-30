using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using DesktopInteraction;

public static class DetectionCases
{
    public static void Run(string output,Action<bool,string> check)
    {
        // Bright multi-color wallpaper has no global 55% dominant background color.
        using(var image=new Bitmap(1280,800))
        using(var font=new Font("Microsoft YaHei UI",10))
        using(var outline=new Pen(Color.FromArgb(25,25,25),3))
        {
            var targets=new List<Point>();
            using(var g=Graphics.FromImage(image))
            {
                using(var gradient=new LinearGradientBrush(new Rectangle(0,0,1280,800),Color.DarkMagenta,Color.DarkCyan,20f))
                    g.FillRectangle(gradient,0,0,1280,800);
                using(var brush=new SolidBrush(Color.FromArgb(70,180,80)))g.FillEllipse(brush,450,150,650,550);
                for(int col=0;col<3;col++)for(int row=0;row<5;row++)
                {
                    int x=26+col*115,y=22+row*135;
                    g.FillRectangle(Brushes.Gold,x,y,48,48);
                    g.DrawRectangle(Pens.White,x+6,y+6,34,34);
                    string label="示例 "+(col*5+row+1);
                    var size=g.MeasureString(label,font);
                    float tx=x+24-size.Width/2,ty=y+54;
                    // Windows-like shadow, not OCR-readable data from a real desktop.
                    g.DrawString(label,font,Brushes.Black,tx+1,ty+1);
                    g.DrawString(label,font,Brushes.White,tx,ty);
                    targets.Add(new Point(x+24,y+24));
                }
            }
            var detected=PixelIcons.Find(image,new Rectangle(0,0,1280,800),CancellationToken.None);
            int hits=0;
            foreach(var target in targets)if(detected.Exists(r=>r.Contains(target)))hits++;
            Console.WriteLine("DETECTION gradient fixture hits="+hits+"/"+targets.Count+" candidates="+detected.Count);
            Console.WriteLine("DETECTION gradient label candidates="+LabelGeometry.Find(image,new Rectangle(0,0,1280,800),CancellationToken.None).Count);
            check(hits>=14,"gradient wallpaper: at least 14 of 15 measured icons located");
            check(detected.Count<=18,"gradient wallpaper: bounded false candidates");
            using(var missing=new Bitmap(image))
            {
                using(var g=Graphics.FromImage(missing))
                using(var gradient=new LinearGradientBrush(new Rectangle(0,0,1280,800),Color.DarkMagenta,Color.DarkCyan,20f))
                {
                    g.SetClip(new Rectangle(120,280,100,100));g.FillRectangle(gradient,0,0,1280,800);
                }
                var sparse=PixelIcons.Find(missing,new Rectangle(0,0,1280,800),CancellationToken.None);
                check(!sparse.Exists(r=>r.Contains(new Point(165,316))),"inferred grid never fills an empty cell without pixel evidence");
            }
            using(var g=Graphics.FromImage(image))foreach(var box in detected)g.DrawRectangle(Pens.Lime,box);
            image.Save(Path.Combine(output,"detection-gradient-fixture.png"));
        }
        string reference=Path.GetFullPath(Path.Combine(output,"..","..","reference","user-reference-original.png"));
        if(!File.Exists(reference))Console.WriteLine("SKIP private reference comparison: original screenshot is not distributed in the public repository.");
        if(File.Exists(reference))
        using(var image=new Bitmap(reference))
        {
            var detected=PixelIcons.Find(image,new Rectangle(0,0,1269,912),CancellationToken.None);
            int hits=0,total=0;
            for(int col=0;col<4;col++)for(int row=0;row<(col==3?2:9);row++)
            {
                total++;
                var point=new Point(48+col*97,33+row*103);
                if(detected.Exists(r=>r.Contains(point)))hits++;
            }
            Console.WriteLine("DETECTION provided reference hits="+hits+"/"+total+" candidates="+detected.Count);
            var labelOnly=LabelGeometry.Find(image,new Rectangle(0,0,1269,912),CancellationToken.None);
            Console.WriteLine("DETECTION provided reference label candidates="+labelOnly.Count);
            using(var preview=new Bitmap(image))
            {
                using(var g=Graphics.FromImage(preview))foreach(var box in labelOnly)g.DrawRectangle(Pens.Lime,box);
                preview.Save(Path.Combine(output,"detection-provided-reference-labels.png"));
            }
            check(hits>=26,"provided reference: at least 26 of 29 icon centers located");
            check(detected.Count<=34,"provided reference: bounded false candidates");
            var bar=TaskbarPixels.Find(image,new Rectangle(0,912,1269,40),CancellationToken.None);
            Console.WriteLine("DETECTION provided reference taskbar candidates="+bar.Count);
            check(bar.Count>=3 && bar.Count<=5,"provided reference: taskbar small icons found from pixels");
            using(var g=Graphics.FromImage(image))
            {
                foreach(var box in detected)g.DrawRectangle(Pens.Lime,box);
                foreach(var box in bar)g.DrawRectangle(Pens.Red,box);
            }
            image.Save(Path.Combine(output,"detection-provided-reference.png"));
            string assets=Path.GetFullPath(Path.Combine(output,"..","assets"));
            using(var theme=new Theme(new Bitmap(Path.Combine(assets,"background.png")),new Bitmap(Path.Combine(assets,"icon.png"))))
            using(var original=new Bitmap(reference))
            using(var frame=new Frame(new Rectangle(0,0,1269,952),new Rectangle(0,0,1269,912),new Bitmap(original)))
            using(var rendered=Composer.Render(frame,theme,CancellationToken.None))
                rendered.Save(Path.Combine(output,"reference-transformed-preview.png"));
            using(var scaled=new Bitmap(1904,1368))
            using(var source=new Bitmap(reference))
            {
                using(var g=Graphics.FromImage(scaled))g.DrawImage(source,new Rectangle(0,0,1904,1368),new Rectangle(0,0,1269,912),GraphicsUnit.Pixel);
                var scaledBoxes=PixelIcons.Find(scaled,new Rectangle(0,0,1904,1368),CancellationToken.None);
                int scaledHits=0;
                for(int col=0;col<4;col++)for(int row=0;row<(col==3?2:9);row++)
                    if(scaledBoxes.Exists(r=>r.Contains(new Point((int)((48+col*97)*1.5),(int)((33+row*103)*1.5)))))scaledHits++;
                Console.WriteLine("DETECTION scaled reference hits="+scaledHits+"/29 candidates="+scaledBoxes.Count);
                using(var g=Graphics.FromImage(scaled))foreach(var box in scaledBoxes)g.DrawRectangle(Pens.Lime,box);
                scaled.Save(Path.Combine(output,"detection-reference-150percent.png"));
                check(scaledHits>=26 && scaledBoxes.Count<=34,"provided reference at 150% pixel scale remains bounded");
            }
        }
        using(var image=new Bitmap(960,600))
        {
            using(var g=Graphics.FromImage(image))
            {
                g.Clear(Color.DarkSlateBlue);
                g.FillRectangle(Brushes.Black,0,556,960,44);
                for(int i=0;i<4;i++)g.FillEllipse(i%2==0?Brushes.Lime:Brushes.Orange,120+i*56,563,28,28);
            }
            var bands=TaskbarPixels.Bands(new Rectangle(0,0,960,600),new Rectangle(0,0,960,556));
            var boxes=TaskbarPixels.Find(image,bands[0],CancellationToken.None);
            check(boxes.Count==4,"four simulated taskbar icons located");
            using(var bg=new Bitmap(40,40))
            using(var icon=new Bitmap(32,32))
            {
                using(var g=Graphics.FromImage(bg))g.Clear(Color.Blue);
                using(var g=Graphics.FromImage(icon))g.Clear(Color.Red);
                using(var theme=new Theme(new Bitmap(bg),new Bitmap(icon)))
                using(var frame=new Frame(new Rectangle(0,0,960,600),new Rectangle(0,0,960,556),new Bitmap(image)))
                using(var result=Composer.Render(frame,theme,CancellationToken.None))
                {
                    check(result.GetPixel(134,577).R>200 && result.GetPixel(134,577).G<20,"taskbar icon changed on visual copy");
                    check(frame.Pixels.GetPixel(134,577).G>200,"taskbar source frame remains unchanged");
                    check(result.GetPixel(10,580).ToArgb()==Color.Black.ToArgb(),"taskbar end region preserved");
                }
            }
        }
        using(var image=new Bitmap(640,700))
        {
            using(var g=Graphics.FromImage(image))
            {
                g.Clear(Color.DarkSlateBlue);g.FillRectangle(Brushes.Black,0,0,44,700);
                for(int i=0;i<4;i++)g.FillEllipse(Brushes.White,8,140+i*64,28,28);
            }
            var bands=TaskbarPixels.Bands(new Rectangle(0,0,640,700),new Rectangle(44,0,596,700));
            check(bands.Count==1 && TaskbarPixels.Find(image,bands[0],CancellationToken.None).Count==4,"vertical taskbar: four monochrome icons found from pixels");
        }
    }
}
