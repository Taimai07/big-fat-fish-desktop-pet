using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("大肥鱼桌宠")]
[assembly: AssemblyProduct("大肥鱼桌宠")]
[assembly: AssemblyVersion("0.7.2.0")]
[assembly: AssemblyFileVersion("0.7.2.0")]

namespace DesktopInteraction
{
    public static class AppIdentity
    {
        public static readonly Icon Icon=LoadIcon();
        private static Icon LoadIcon()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DesktopInteraction.Assets.app.ico"))
            using(var icon=new Icon(stream))return (Icon)icon.Clone();
        }
    }
    public static class DisplayDpi
    {
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        // A hidden app-owned window supplies per-monitor DPI without querying Explorer.
        public static uint Read(Rectangle monitor)
        {
            var window=new NativeWindow();
            try
            {
                window.CreateHandle(new CreateParams {X=monitor.X+monitor.Width/2,Y=monitor.Y+monitor.Height/2,
                    Width=1,Height=1,Style=unchecked((int)0x80000000),ExStyle=0x08000080});
                uint dpi=GetDpiForWindow(window.Handle);
                if(dpi==0)throw new InvalidOperationException("Display DPI unavailable");
                return dpi;
            }
            finally{window.DestroyHandle();}
        }
    }
    public sealed class InputFeedback
    {
        private readonly Action play;
        private readonly Func<long> clock;
        private long last=-1;
        public InputFeedback(Action play=null,Func<long> clock=null)
        {
            this.play=play??(()=>System.Media.SystemSounds.Hand.Play());
            var timer=System.Diagnostics.Stopwatch.StartNew();this.clock=clock??(()=>timer.ElapsedMilliseconds);
        }
        public bool Notify()
        {
            long now=clock();
            if(last>=0 && now-last<120)return false;
            last=now;
            try {play();}catch(InvalidOperationException) { }
            return true;
        }
        public void WindowMessage(int message,IntPtr key)
        {
            if(IsAltKey((Keys)key.ToInt64()))return;
            if((message==0x0104 || message==0x0100) && key.ToInt64()==(long)Keys.F4)return;
            if(message==0x0100 || message==0x0104 || message==0x0201 || message==0x0204 || message==0x0207 ||
               message==0x020B || message==0x020A || message==0x020E)Notify();
        }
        public static bool IsAltKey(Keys keyData)
        {
            Keys key=keyData & Keys.KeyCode;
            return key==Keys.Menu || key==Keys.LMenu || key==Keys.RMenu;
        }
    }
    public static class OwnWindowTransitions
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        public static bool Disable(IntPtr window)
        {
            try { int value=1;return DwmSetWindowAttribute(window,3,ref value,sizeof(int))>=0; }
            catch(DllNotFoundException) {return false;}
            catch(EntryPointNotFoundException) {return false;}
        }
    }
    public sealed class ScreenMockSource : IFrameSource
    {
        public Task<List<Frame>> CaptureOnceAsync(CancellationToken token)
        {
            var result=new List<Frame>();
            try
            {
                foreach(var screen in Screen.AllScreens)
                {
                    token.ThrowIfCancellationRequested();
                    using(var frame=MockSource.CreateNamed(screen.Bounds,screen.WorkingArea,6))
                    {
                        var bitmap=new Bitmap(frame.Pixels);
                        var work=screen.WorkingArea;work.Offset(-screen.Bounds.X,-screen.Bounds.Y);
                        using(var g=Graphics.FromImage(bitmap))
                        foreach(var band in TaskbarPixels.Bands(new Rectangle(Point.Empty,bitmap.Size),work))
                        {
                            g.FillRectangle(Brushes.Black,band);
                            bool horizontal=band.Width>=band.Height;
                            int thickness=horizontal?band.Height:band.Width,size=Math.Max(12,thickness-12);
                            for(int i=0;i<4;i++)
                            {
                                int p=120+i*(size+24);
                                g.FillEllipse(i%2==0?Brushes.Lime:Brushes.Orange,
                                    band.X+(horizontal?p:6),band.Y+(horizontal?6:p),size,size);
                            }
                        }
                        result.Add(new Frame(screen.Bounds,screen.WorkingArea,bitmap,frame.Slots,DisplayDpi.Read(screen.Bounds)));
                    }
                }
                return Task.FromResult(result);
            }
            catch {foreach(var frame in result)frame.Dispose();throw;}
        }
    }
    public sealed class LiveSource : IFrameSource,IDisposable
    {
        private int used;
        private object shell;
        private bool minimized;
        private readonly IDesktopGeometry geometry;
        public LiveSource(IDesktopGeometry geometry=null)
        {
            this.geometry=geometry??new DesktopGeometry();
        }
        public async Task<List<Frame>> CaptureOnceAsync(CancellationToken token)
        {
            if(Interlocked.Exchange(ref used,1)!=0) throw new InvalidOperationException("One-shot collection only");
            token.ThrowIfCancellationRequested();
            var screens=Screen.AllScreens;
            var bounds=screens.Select(s=>s.Bounds).ToArray();
            var works=screens.Select(s=>s.WorkingArea).ToArray();
            var dpis=bounds.Select(DisplayDpi.Read).ToArray();
            long pixels=0;
            foreach(var b in bounds)
            {
                if(b.Width<=0 || b.Height<=0 || b.Width>16384 || b.Height>16384) throw new InvalidOperationException("Display size unsupported");
                pixels+=(long)b.Width*b.Height;
            }
            if(pixels>48000000) throw new InvalidOperationException("Display memory budget exceeded");
            var frames=new List<Frame>();
            try
            {
                shell=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"),true));
                // Documented Shell methods only. No simulated keystrokes or window enumeration.
                shell.GetType().InvokeMember("MinimizeAll",BindingFlags.InvokeMethod,null,shell,null);
                minimized=true;
                await Task.Delay(650,token);
                token.ThrowIfCancellationRequested();
                if(!bounds.SequenceEqual(Screen.AllScreens.Select(s=>s.Bounds))) throw new InvalidOperationException("Displays changed");
                // The bounded UI query runs without displaying a preparation window.
                var layoutTask=Task.Run(()=>geometry.ReadOnce(token),token);
                if(await Task.WhenAny(layoutTask,Task.Delay(4000,token))!=layoutTask)
                {
                    // A timed-out read is never followed by screenshot work. Observe late faults;
                    // the background worker cannot keep the application process resident on exit.
                    var observer=layoutTask.ContinueWith(t=>GC.KeepAlive(t.Exception),TaskContinuationOptions.OnlyOnFaulted);
                    GC.KeepAlive(observer);
                    token.ThrowIfCancellationRequested();throw new DesktopLayoutUnavailableException();
                }
                var slots=await layoutTask;
                token.ThrowIfCancellationRequested();
                var owners=new List<DesktopSlot>[bounds.Length];
                for(int i=0;i<owners.Length;i++)owners[i]=new List<DesktopSlot>();
                foreach(var slot in slots)
                {
                    var center=new Point(slot.Cell.X+slot.Cell.Width/2,slot.Cell.Y+slot.Cell.Height/2);
                    int owner=Array.FindIndex(bounds,b=>b.Contains(center));
                    if(owner<0)throw new DesktopLayoutUnavailableException();
                    owners[owner].Add(slot);
                }
                await Task.Delay(100,token);
                if(!bounds.SequenceEqual(Screen.AllScreens.Select(s=>s.Bounds)))throw new DesktopLayoutUnavailableException();
                await Task.Run(()=>
                {
                    for(int i=0;i<bounds.Length;i++)
                    {
                        token.ThrowIfCancellationRequested();
                        Bitmap image=new Bitmap(bounds[i].Width,bounds[i].Height,PixelFormat.Format32bppArgb);
                        try
                        {
                            using(var g=Graphics.FromImage(image))
                                g.CopyFromScreen(bounds[i].Location,Point.Empty,bounds[i].Size,CopyPixelOperation.SourceCopy);
                            frames.Add(new Frame(bounds[i],works[i],image,owners[i],dpis[i]));
                        }
                        catch { image.Dispose(); throw; }
                    }
                },token);
                return frames;
            }
            catch { foreach(var f in frames) f.Dispose();Dispose();throw; }
        }
        public void Dispose()
        {
            object current=shell;shell=null;
            if(current==null)return;
            try { if(minimized)current.GetType().InvokeMember("UndoMinimizeALL",BindingFlags.InvokeMethod,null,current,null); }
            finally {minimized=false;Marshal.FinalReleaseComObject(current);}
        }
    }

    public sealed class Consent : Form
    {
        public Consent()
        {
            Text="大肥鱼桌宠";Icon=AppIdentity.Icon;
            Font=new Font("Microsoft YaHei UI",10);
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(480,270);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false; MinimizeBox=false; StartPosition=FormStartPosition.CenterScreen;
            var title=new Label { Text="桌面互动模式",Font=new Font(Font.FontFamily,18,FontStyle.Bold),AutoSize=true,Location=new Point(24,24) };
            var body=new Label { Location=new Point(24,82),Size=new Size(432,90),Text=
                "为了适配桌面，本程序将采集桌面画面及图标信息。数据仅在本机内存中使用，不保存、不上传。" };
            var cancel=new Button { Text="取消",DialogResult=DialogResult.Cancel,Location=new Point(210,202),Size=new Size(100,40) };
            var allow=new Button { Text="允许并继续",DialogResult=DialogResult.OK,Location=new Point(326,202),Size=new Size(130,40) };
            Controls.AddRange(new Control[]{title,body,cancel,allow});
            CancelButton=cancel; ActiveControl=cancel;
        }
    }

    public sealed class Overlay : Form
    {
        private readonly Action stop;
        private Bitmap pixels;
        private readonly InputFeedback feedback;
        public bool TransitionsDisabled {get;private set;}
        public Overlay(Frame frame,bool simulation,int index,Action stop,InputFeedback feedback=null)
        {
            this.stop=stop; pixels=frame.Pixels;
            this.feedback=feedback??new InputFeedback();
            Text=simulation ? "大肥鱼桌宠 · 模拟显示器 "+(index+1) : "大肥鱼桌宠 · 显示器 "+(index+1);
            Icon=AppIdentity.Icon;
            AutoScaleMode=AutoScaleMode.None; KeyPreview=true; DoubleBuffered=true;
            StartPosition=FormStartPosition.Manual;
            // Ordinary windows, never topmost. Alt+Tab and Task Manager remain usable.
            TopMost=false; ShowInTaskbar=true;
            if(simulation)
            {
                FormBorderStyle=FormBorderStyle.Sizable;
                var work=Screen.PrimaryScreen.WorkingArea;
                Bounds=new Rectangle(work.X+30+index*55,work.Y+30+index*55,Math.Min(760,work.Width-90),Math.Min(500,work.Height-110));
            }
            else { FormBorderStyle=FormBorderStyle.None; Bounds=frame.Bounds; }
            FormClosed+=(s,e)=>stop();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(pixels!=null) e.Graphics.DrawImage(pixels,ClientRectangle);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);TransitionsDisabled=OwnWindowTransitions.Disable(Handle);
        }
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {
            if(keyData==(Keys.Alt|Keys.F4) || InputFeedback.IsAltKey(keyData))return base.ProcessCmdKey(ref msg,keyData);
            feedback.Notify();
            if(keyData==Keys.Escape)return true;
            return base.ProcessCmdKey(ref msg,keyData);
        }
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);e.Handled=true;
        }
        protected override void WndProc(ref Message m)
        {
            if(feedback!=null)feedback.WindowMessage(m.Msg,m.WParam);
            // Display topology changes or session end: close instead of recapturing.
            if(m.Msg==0x007E || m.Msg==0x0011)
            {
                stop();
                if(m.Msg==0x0011) { m.Result=new IntPtr(1); return; }
            }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing)
        {
            pixels=null; base.Dispose(disposing);
        }
    }

    public sealed class OverlayGroup : ApplicationContext
    {
        private readonly List<Overlay> windows=new List<Overlay>();
        private readonly List<Frame> frames;
        private bool stopping;
        internal static readonly TimeSpan EffectDuration=TimeSpan.FromMinutes(3);
        private readonly System.Windows.Forms.Timer expiry=new System.Windows.Forms.Timer {Interval=250};
        private readonly System.Diagnostics.Stopwatch elapsed= new System.Diagnostics.Stopwatch();
        public int WindowCount { get { return windows.Count; } }
        public Overlay First { get { return windows[0]; } }
        public OverlayGroup(List<Frame> frames,bool simulation,Func<InputFeedback> feedbackFactory=null,Func<TimeSpan> testElapsed=null)
        {
            this.frames=frames;
            try
            {
                if(!simulation && !frames.Select(f=>f.Bounds).SequenceEqual(Screen.AllScreens.Select(s=>s.Bounds)))
                    throw new InvalidOperationException("Displays changed during processing");
                for(int i=0;i<frames.Count;i++) windows.Add(new Overlay(frames[i],simulation,i,Stop,feedbackFactory==null?null:feedbackFactory()));
                // Create hidden handles and set per-window DWM flags before the first visible frame.
                foreach(var window in windows) {IntPtr created=window.Handle;if(created==IntPtr.Zero)throw new InvalidOperationException("Window creation failed");}
                // Monotonic time starts with the first effect window, after consent/preparation.
                elapsed.Start();
                foreach(var window in windows) window.Show();
                if(windows.Count==0) throw new InvalidOperationException("No displays");
                windows[windows.Count-1].Activate();
                expiry.Tick+=(s,e)=>{if((testElapsed==null?elapsed.Elapsed:testElapsed())>=EffectDuration)Stop();};
                expiry.Start();
            }
            catch { Stop(); throw; }
        }
        public void Stop()
        {
            if(stopping) return;
            stopping=true;
            expiry.Stop();expiry.Dispose();elapsed.Stop();
            foreach(var w in windows.ToArray()) { w.Close(); w.Dispose(); }
            foreach(var f in frames) f.Dispose();
            ExitThread();
        }
        protected override void Dispose(bool disposing) { if(disposing) Stop(); base.Dispose(disposing); }
    }

    public sealed class Preparation : ApplicationContext
    {
        private readonly CancellationTokenSource cancel=new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer startup=new System.Windows.Forms.Timer {Interval=1};
        private bool stopped,disposed;
        public List<Frame> Result;
        public LiveSource Live {get;private set;}
        private readonly bool fullScreenMock;
        public Preparation(bool simulation,Theme theme,bool fullScreenMock=false,IFrameSource simulationSource=null)
        {
            this.fullScreenMock=fullScreenMock;
            if(!simulation && simulationSource!=null)throw new ArgumentException("Mock source is only available in simulation");
            // Start on the normal UI message loop, with no preparation form or taskbar entry.
            startup.Tick+=async (s,e)=>{startup.Stop();await Run(simulation,theme,simulationSource);};
            startup.Start();
        }
        private async Task Run(bool simulation,Theme theme,IFrameSource simulationSource)
        {
            try
            {
                IFrameSource source;
                if(simulation)source=simulationSource??(fullScreenMock?(IFrameSource)new ScreenMockSource():new MockSource());
                else {Live=new LiveSource();source=Live;}
                // This context is created only after explicit consent, or for synthetic simulation.
                Result=await Session.PrepareAsync(true,source,theme,cancel.Token);
                if(stopped) { foreach(var f in Result) f.Dispose(); Result=null; }
            }
            catch(OperationCanceledException) { }
            catch(DesktopLayoutUnavailableException)
            {
                if(!stopped)MessageBox.Show("当前 Windows 桌面没有提供完整的图标布局。程序已停止，不会猜测或补出图标。没有保存或上传画面。","桌面互动模式",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception)
            {
                if(!stopped) MessageBox.Show("未能安全准备画面，程序将退出。没有保存或上传截图。","桌面互动模式",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            finally { ExitThread(); }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing && !disposed)
            {
                disposed=true;stopped=true;startup.Stop();startup.Dispose();cancel.Cancel();cancel.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public static class Program
    {
        public static Bitmap ReadAsset(string name)
        {
            if(name!="background.png" && name!="icon.png")throw new ArgumentException("Unknown asset");
            // Embedded PNG streams only. No external folders or temporary extraction.
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DesktopInteraction.Assets."+name))
            using(var image=Image.FromStream(stream,false,true))
            {
                if(image.Width>8192 || image.Height>8192 || (long)image.Width*image.Height>32000000)
                    throw new InvalidOperationException("Asset too large");
                return new Bitmap(image);
            }
        }
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Theme theme=null;
            LiveSource live=null;
            try
            {
                bool windowed=args.Length==1 && args[0]=="--simulate-windowed";
                bool previewLaunch=args.Length==0 && string.Equals(Path.GetFileName(Application.ExecutablePath),"全屏预览.exe",StringComparison.OrdinalIgnoreCase);
                bool simulation=previewLaunch || windowed || (args.Length==1 && args[0]=="--simulate");
                if(args.Length>0 && !simulation) return;
                Bitmap background=null,icon=null;
                try { background=ReadAsset("background.png"); icon=ReadAsset("icon.png"); theme=new Theme(background,icon); }
                catch { if(background!=null) background.Dispose(); if(icon!=null) icon.Dispose(); throw; }
                if(!simulation && !theme.Ready)
                {
                    var choice=MessageBox.Show("原参考素材尚未接入，真实桌面模式暂不可用。\r\n\r\n是否打开模拟演示？模拟模式不会显示或截取真实桌面。","素材待接入",MessageBoxButtons.OKCancel,MessageBoxIcon.Information,MessageBoxDefaultButton.Button2);
                    if(choice!=DialogResult.OK) return;
                    simulation=true;
                }
                if(!simulation)
                    using(var consent=new Consent()) if(consent.ShowDialog()!=DialogResult.OK) return;
                List<Frame> frames;
                using(var preparing=new Preparation(simulation,theme,simulation && !windowed))
                {
                    Application.Run(preparing);frames=preparing.Result;live=preparing.Live;
                }
                if(frames!=null && frames.Count>0)
                    using(var group=new OverlayGroup(frames,windowed)) Application.Run(group);
            }
            catch(Exception)
            {
                MessageBox.Show("程序已停止。请检查程序素材与显示设置。","桌面互动模式",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            finally
            {
                try {if(live!=null)live.Dispose();}
                catch(Exception) {MessageBox.Show("程序已退出，部分窗口可能需要手动恢复。","桌面互动模式");}
                if(theme!=null) theme.Dispose();
            }
        }
    }
}
