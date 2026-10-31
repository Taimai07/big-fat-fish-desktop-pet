using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DesktopInteraction;

public static class Tests
{
    static int passed;
    static void Check(bool value,string label) { if(!value) throw new Exception(label); passed++; Console.WriteLine("PASS "+label); }
    sealed class FakeSource : IFrameSource
    {
        public int Calls;
        public List<Frame> Frames;
        public bool BreakSecond;
        public Task<List<Frame>> CaptureOnceAsync(CancellationToken token)
        {
            Calls++;
            Frames=new List<Frame>{MockSource.Create(new Rectangle(-960,0,960,600),6),MockSource.Create(new Rectangle(0,-200,800,600),3)};
            if(BreakSecond) Frames[1].Pixels.Dispose();
            return Task.FromResult(Frames);
        }
    }
    static async Task PipelineTests(string output)
    {
        using(var theme=new Theme(null,null))
        {
            var denied=new FakeSource();
            var none=await Session.PrepareAsync(false,denied,theme,CancellationToken.None);
            Check(denied.Calls==0 && none.Count==0,"denial: zero capture calls");
            var cancelled=new FakeSource();
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await Session.PrepareAsync(true,cancelled,theme,cancel.Token); throw new Exception("Expected cancellation"); }
                catch(OperationCanceledException) { Check(cancelled.Calls==0,"cancel before capture: zero calls"); }
            }
            var source=new FakeSource();
            var prepared=await Session.PrepareAsync(true,source,theme,CancellationToken.None);
            try
            {
                Check(source.Calls==1 && prepared.Count==2,"one acquisition with two independent monitor frames");
                Check(prepared[0].Bounds.X==-960 && prepared[1].Bounds.Y==-200,"negative monitor coordinates preserved");
                Check(source.Frames.TrueForAll(f=>f.Pixels==null),"original pixel buffers disposed after composition");
                Check(prepared[0].Pixels.GetPixel(10,590).ToArgb()==Color.Black.ToArgb(),"taskbar pixels preserved");
                // Only deterministic synthetic content is exported. Production has no export method.
                prepared[0].Pixels.Save(Path.Combine(output,"synthetic-preview.png"),ImageFormat.Png);
            }
            finally { foreach(var frame in prepared) frame.Dispose(); }
            var broken=new FakeSource { BreakSecond=true };
            bool failed=false;
            try { await Session.PrepareAsync(true,broken,theme,CancellationToken.None); }
            catch(ArgumentException) { failed=true; }
            Check(failed && broken.Frames.TrueForAll(f=>f.Pixels==null),"render failure releases every source frame");
        }
        using(var frame=MockSource.Create(new Rectangle(0,0,960,600),6))
        {
            var icons=PixelIcons.Find(frame.Pixels,new Rectangle(0,0,960,564),CancellationToken.None);
            Check(icons.Count==6,"six synthetic icons detected, label bars excluded");
            for(int i=0;i<6;i++) Check(icons.Exists(r=>r.Contains(44+(i/4)*100,44+(i%4)*108)),"synthetic icon center "+i);
        }
        using(var busy=new Bitmap(400,300))
        {
            using(var g=Graphics.FromImage(busy))
                for(int y=0;y<300;y+=10) for(int x=0;x<400;x+=10)
                    using(var brush=new SolidBrush(Color.FromArgb((x*7)%256,(y*11)%256,((x+y)*3)%256))) g.FillRectangle(brush,x,y,10,10);
            Check(PixelIcons.Find(busy,new Rectangle(0,0,400,300),CancellationToken.None).Count==0,"complex wallpaper rejected conservatively");
        }
        using(var blank=new Bitmap(600,400))
        {
            using(var g=Graphics.FromImage(blank)) g.Clear(Color.DarkSlateGray);
            Check(PixelIcons.Find(blank,new Rectangle(0,0,600,400),CancellationToken.None).Count==0,"empty desktop: no invented icons");
        }
        using(var bg=new Bitmap(960,600))
        using(var icon=new Bitmap(40,40))
        {
            using(var g=Graphics.FromImage(bg)) g.Clear(Color.White);
            using(var g=Graphics.FromImage(icon)) g.Clear(Color.Red);
            using(var theme=new Theme(new Bitmap(bg),new Bitmap(icon)))
            using(var frame=MockSource.Create(new Rectangle(0,0,960,600),6))
            using(var result=Composer.Render(frame,theme,CancellationToken.None))
            {
                Check(result.GetPixel(44,44).ToArgb()==Color.Red.ToArgb(),"replaceable icon asset rendered at pixel candidate");
                Check(result.GetPixel(500,300).ToArgb()==Color.White.ToArgb(),"replaceable background asset rendered");
            }
        }
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
    static void CloseGroup(string mode)
    {
        using(var theme=new Theme(null,null))
        {
            var frames=Task.Run(()=>Session.PrepareAsync(true,new MockSource(),theme,CancellationToken.None)).GetAwaiter().GetResult();
            using(var group=new OverlayGroup(frames,true,()=>new InputFeedback(()=>{},()=>0)))
            using(var timer=new System.Windows.Forms.Timer { Interval=150 })
            {
                timer.Tick+=(s,e)=>
                {
                    timer.Stop();
                    Check(group.WindowCount==2,"two simulated windows: "+mode);
                    Check(!group.First.TopMost,"ordinary z-order: "+mode);
                    if(mode=="Esc")
                    {
                        object[] call={Message.Create(group.First.Handle,0,IntPtr.Zero,IntPtr.Zero),Keys.Escape};
                        typeof(Overlay).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(group.First,call);
                        Check(Application.OpenForms.Count==2,"Esc leaves both effect windows open");
                        group.First.Close();
                    }
                    else if(mode=="AltF4") SendMessage(group.First.Handle,0x0112,new IntPtr(0xF060),IntPtr.Zero);
                    else group.First.Close();
                };
                timer.Start(); Application.Run(group);
                string closeResult=mode=="Esc"?"normal close after Esc":mode;
                Check(frames.TrueForAll(f=>f.Pixels==null),closeResult+": all monitor buffers released");
                Check(Application.OpenForms.Count==0,closeResult+": all windows closed");
            }
        }
    }
    static void ConsentTests(bool allow)
    {
        using(var form=new Consent())
        using(var timer=new System.Windows.Forms.Timer { Interval=100 })
        {
            timer.Tick+=(s,e)=>
            {
                timer.Stop();
                foreach(Control c in form.Controls)
                {
                    var button=c as Button;
                    if(button!=null && button.DialogResult==(allow?DialogResult.OK:DialogResult.Cancel)) { button.PerformClick(); break; }
                }
            };
            timer.Start();
            Check(form.ShowDialog()==(allow?DialogResult.OK:DialogResult.Cancel),"consent "+(allow?"allow":"deny")+" button result");
        }
    }
    sealed class SlowMockSource : IFrameSource
    {
        public int BeforeThread,AfterThread;
        public async Task<List<Frame>> CaptureOnceAsync(CancellationToken token)
        {
            BeforeThread=Thread.CurrentThread.ManagedThreadId;
            await Task.Delay(160,token);
            AfterThread=Thread.CurrentThread.ManagedThreadId;
            return await new MockSource().CaptureOnceAsync(token);
        }
    }
    static void PreparationTest()
    {
        var source=new SlowMockSource();int samples=0,visibleForms=0;
        int uiThread=Thread.CurrentThread.ManagedThreadId;
        using(var theme=new Theme(null,null))
        using(var preparing=new Preparation(true,theme,false,source))
        using(var observer=new System.Windows.Forms.Timer {Interval=10})
        {
            observer.Tick+=(s,e)=>{samples++;visibleForms=Math.Max(visibleForms,Application.OpenForms.Count);};
            observer.Start();
            Application.Run(preparing);
            Check(preparing.Result!=null && preparing.Result.Count==2,"simulation preparation context completes with two frames");
            Check(samples>0 && visibleForms==0,"preparation displays no forms throughout delayed mock collection");
            Check(source.BeforeThread==uiThread && source.AfterThread==uiThread,"invisible preparation preserves normal UI-thread async continuations");
            foreach(var f in preparing.Result) f.Dispose();
        }
        Check(Application.OpenForms.Count==0,"preparation leaves no visible or resident forms");
    }
    static void RenderConsent(string output)
    {
        using(var form=new Consent())
        {
            form.Show(); Application.DoEvents();
            foreach(Control control in form.Controls)
            {
                var label=control as Label;
                if(label!=null && !label.AutoSize)
                {
                    var required=TextRenderer.MeasureText(label.Text,label.Font,new Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl);
                    Check(required.Height<=label.Height,"consent disclosure text fits visible label");
                }
            }
            using(var bitmap=new Bitmap(form.Width,form.Height))
            {
                form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                bitmap.Save(Path.Combine(output,"consent-ui.png"),ImageFormat.Png);
            }
            form.Hide();
        }
    }
    static void ReferenceAssetsTest(string output)
    {
        string assets=Path.GetFullPath(Path.Combine(output,"..","assets"));
        using(var theme=new Theme(new Bitmap(Path.Combine(assets,"background.png")),new Bitmap(Path.Combine(assets,"icon.png"))))
        using(var frame=MockSource.Create(new Rectangle(0,0,960,600),6))
        using(var rendered=Composer.Render(frame,theme,CancellationToken.None))
        {
            Check(theme.Ready && theme.Background.Width==869 && theme.Background.Height==912,"reference-derived background dimensions");
            bool transparent=false,opaque=false;
            for(int y=0;y<theme.Icon.Height;y++) for(int x=0;x<theme.Icon.Width;x++)
            {
                byte alpha=theme.Icon.GetPixel(x,y).A;
                if(alpha==0) transparent=true;
                if(alpha==255) opaque=true;
            }
            Check(transparent && opaque,"reference icon has transparent background and opaque subject");
            Check(rendered.GetPixel(10,300).B>rendered.GetPixel(10,300).R,"reference blue background extends to left edge");
            int artworkLeft=960-(int)(theme.Background.Width*(564.0/theme.Background.Height));
            Check(Math.Abs(rendered.GetPixel(artworkLeft,300).B-rendered.GetPixel(artworkLeft-1,300).B)<=2,"background boundary has no resampling brightness seam");
            rendered.Save(Path.Combine(output,"reference-theme-preview.png"),ImageFormat.Png);
            using(var named=MockSource.CreateNamed(new Rectangle(0,0,960,600),new Rectangle(0,0,960,564),6))
            using(var namedPreview=Composer.Render(named,theme,CancellationToken.None))
                namedPreview.Save(Path.Combine(output,"named-reference-theme-preview.png"),ImageFormat.Png);
        }
    }
    static void FullScreenTest()
    {
        var frames=new ScreenMockSource().CaptureOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        using(var group=new OverlayGroup(frames,false,()=>new InputFeedback(()=>{},()=>0)))
        using(var timer=new System.Windows.Forms.Timer {Interval=150})
        {
            timer.Tick+=(s,e)=>
            {
                timer.Stop();
                Check(group.WindowCount==Screen.AllScreens.Length,"fullscreen simulation covers every connected display");
                Check(group.First.FormBorderStyle==FormBorderStyle.None,"fullscreen overlay has no application border");
                Check(group.First.Bounds==Screen.AllScreens[0].Bounds && group.First.ClientSize==Screen.AllScreens[0].Bounds.Size,"fullscreen bounds include taskbar region");
                Check(group.First.TransitionsDisabled,"DWM accepted disabling transitions for this application window");
                Check(!group.First.TopMost,"fullscreen does not force topmost z-order");
                Check(group.First.Controls.Count==0,"effect screen contains no clickable exit button");
                group.Stop();
            };
            timer.Start();Application.Run(group);
            Check(Application.OpenForms.Count==0,"fullscreen closes without resident windows");
        }
    }
    static void SoundTests()
    {
        long time=0;int plays=0;
        var sound=new InputFeedback(()=>plays++,()=>time);
        sound.WindowMessage(0x0200,IntPtr.Zero);
        Check(plays==0,"mouse movement produces no sound");
        sound.WindowMessage(0x0201,IntPtr.Zero);
        Check(plays==1,"mouse click requests the system error sound");
        time+=200;sound.WindowMessage(0x020A,IntPtr.Zero);
        Check(plays==2,"mouse wheel requests the system error sound");
        time+=200;sound.WindowMessage(0x0100,new IntPtr((int)Keys.A));
        Check(plays==3,"keyboard event requests the system error sound");
        sound.WindowMessage(0x0100,new IntPtr((int)Keys.A));
        Check(plays==3,"duplicate or rapid events do not stack sounds");
        foreach(Keys alt in new[]{Keys.Menu,Keys.LMenu,Keys.RMenu})
        {
            time+=200;sound.WindowMessage(0x0104,new IntPtr((int)alt));
            Check(plays==3,"Alt key down stays silent: "+alt);
            time+=50;sound.WindowMessage(0x0104,new IntPtr((int)Keys.F4));
            Check(plays==3,"Alt then F4 adds no sound: "+alt);
            sound.WindowMessage(0x0105,new IntPtr((int)Keys.F4));
            sound.WindowMessage(0x0105,new IntPtr((int)alt));
        }
        var frames=new MockSource().CaptureOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        using(var overlay=new Overlay(frames[0],true,0,()=>{},sound))
        {
            overlay.Show();
            foreach(Keys alt in new[]{Keys.Menu,Keys.LMenu,Keys.RMenu})
            {
                time+=200;
                var altMessage=Message.Create(overlay.Handle,0x0104,new IntPtr((int)alt),IntPtr.Zero);
                object[] altCall={altMessage,alt|Keys.Alt};
                typeof(Overlay).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(overlay,altCall);
                Check(plays==3,"command processing leaves Alt silent: "+alt);
            }
            time+=200;
            var message=Message.Create(overlay.Handle,0,IntPtr.Zero,IntPtr.Zero);
            object[] call={message,Keys.Escape};
            typeof(Overlay).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(overlay,call);
            Check(plays==4 && !overlay.IsDisposed,"Esc plays feedback without exiting the effect");
            overlay.Close();
        }
        foreach(var frame in frames)frame.Dispose();
    }
    static void AutomaticExitTest()
    {
        Check(OverlayGroup.EffectDuration==TimeSpan.FromMinutes(3),"effect timeout is exactly three minutes");
        var frames=new MockSource().CaptureOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        TimeSpan elapsed=TimeSpan.FromSeconds(179);
        using(var group=new OverlayGroup(frames,true,()=>new InputFeedback(()=>{}),()=>elapsed))
        using(var advance=new System.Windows.Forms.Timer {Interval=350})
        using(var watchdog=new System.Windows.Forms.Timer {Interval=3000})
        {
            bool boundaryReached=false,timedOut=false;
            advance.Tick+=(s,e)=>
            {
                Check(!group.First.IsDisposed && Application.OpenForms.Count==2,"all effect windows remain open before three minutes");
                boundaryReached=true;elapsed=TimeSpan.FromMinutes(3);advance.Stop();
            };
            watchdog.Tick+=(s,e)=>{timedOut=true;group.Stop();};
            advance.Start();watchdog.Start();Application.Run(group);
            Check(boundaryReached && !timedOut,"timer automatically closes the group at the three-minute boundary");
            Check(frames.TrueForAll(f=>f.Pixels==null) && Application.OpenForms.Count==0,"automatic exit disposes every monitor buffer and window");
            group.Stop();
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            PipelineTests(args[0]).GetAwaiter().GetResult();
            DetectionCases.Run(args[0],Check);
            GeometryCases.Run(args[0],Check);
            FontCases.Run(args[0],Check);
            SoundTests();
            AutomaticExitTest();
            ConsentTests(false); ConsentTests(true);
            PreparationTest(); RenderConsent(args[0]);
            ReferenceAssetsTest(args[0]);
            CloseGroup("Esc"); CloseGroup("AltF4"); CloseGroup("normal close");
            FullScreenTest();
            Console.WriteLine("TOTAL "+passed+" checks passed; no live capture, no shell minimization.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
