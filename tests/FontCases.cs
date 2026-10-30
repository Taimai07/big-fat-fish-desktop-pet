using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DesktopInteraction;

public static class FontCases
{
    public static void Run(string output,Action<bool,string> check)
    {
        using(var small=new WindowsIconLabels(96))
        using(var medium=new WindowsIconLabels(144))
        using(var large=new WindowsIconLabels(192))
        {
            check(!string.IsNullOrEmpty(small.FontFace) && small.FontHeight>0,"Windows returns an actual icon-title font");
            check(Math.Abs(large.FontHeight-small.FontHeight*2)<=1 && Math.Abs(medium.FontHeight-small.FontHeight*1.5)<=1,
                "icon-title font follows 100%, 150% and 200% Windows DPI metrics");
            using(var a=new Bitmap(180,130))
            using(var b=new Bitmap(180,130))
            using(var c=new Bitmap(180,130))
            {
                using(var g=Graphics.FromImage(a)){g.Clear(Color.RoyalBlue);small.Draw(g,"应用 A&B",new Rectangle(10,10,160,45));}
                using(var g=Graphics.FromImage(b)){g.Clear(Color.RoyalBlue);small.Draw(g,"应用 A&B",new Rectangle(10,10,160,110));}
                using(var g=Graphics.FromImage(c)){g.Clear(Color.RoyalBlue);small.Draw(g,"应用 AB",new Rectangle(10,10,160,45));}
                bool identical=true,different=false,clipped=true;
                for(int y=0;y<130;y++)for(int x=0;x<180;x++)
                {
                    if(a.GetPixel(x,y)!=b.GetPixel(x,y))identical=false;
                    if(a.GetPixel(x,y)!=c.GetPixel(x,y))different=true;
                    if((x<10 || x>=170 || y<10 || y>=55) && a.GetPixel(x,y).ToArgb()!=Color.RoyalBlue.ToArgb())clipped=false;
                }
                check(identical,"changing a label box height does not enlarge its font");
                check(different,"literal ampersands in application names remain visible");
                check(clipped,"font and shadow remain within the supplied label region");
            }
            using(var sheet=new Bitmap(660,300))
            {
                using(var g=Graphics.FromImage(sheet))
                {
                    g.Clear(Color.FromArgb(43,108,178));
                    int column=0;
                    foreach(var style in new[]{small,medium,large})
                    {
                        int x=column++*220;
                        style.Draw(g,"微信 QQ",new Rectangle(x+12,25,196,65));
                        style.Draw(g,"Windows Media Player",new Rectangle(x+12,100,196,85));
                        style.Draw(g,"一个很长的应用程序名称 A&B 2026",new Rectangle(x+12,195,196,85));
                    }
                }
                sheet.Save(Path.Combine(output,"windows-font-preview.png"),ImageFormat.Png);
            }
        }
        foreach(var screen in System.Windows.Forms.Screen.AllScreens)
            check(DisplayDpi.Read(screen.Bounds)>=96,"hidden app-owned window reports monitor DPI without a capture");
        using(var background=Program.ReadAsset("background.png"))
        using(var icon=Program.ReadAsset("icon.png"))
            check(background.Width==869 && icon.Width==66,"both theme assets load directly from embedded assembly streams");
    }
}
