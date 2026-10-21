using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LiveCaptionsNotion.Native;

class Program
{
    [DllImport("user32.dll",SetLastError=true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    static int passed;
    [STAThread] static void Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.InvokeAsync(async()=>{
            try { await Run(); Console.WriteLine($"TOTAL {passed} clipboard checks passed"); }
            catch(Exception e){Console.WriteLine(e.GetType().Name);Environment.ExitCode=1;}
            finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}
        });
        Dispatcher.Run();
    }
    static Thread Hold(int ms)
    {
        using var ready=new ManualResetEventSlim(); Exception? error=null;
        var thread=new Thread(()=>{
            using var owner=new HwndSource(new HwndSourceParameters("LCT clipboard test"){Width=0,Height=0,WindowStyle=0});
            try{if(!OpenClipboard(owner.Handle))throw new Exception("Test clipboard lock unavailable");ready.Set();Thread.Sleep(ms);}
            catch(Exception e){error=e;ready.Set();}finally{CloseClipboard();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();ready.Wait();if(error!=null)throw error;return thread;
    }
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static async Task Run()
    {
        using var owner=new HwndSource(new HwndSourceParameters("LCT clipboard test owner"){Width=0,Height=0,WindowStyle=0});
        // Read and restore the existing text without printing it. Leave non-text clipboard untouched.
        if(!Clipboard.ContainsText())throw new InvalidOperationException("Clipboard is not text; preserve its existing formats and run manually with a text clipboard.");
        var previous=Clipboard.GetText(); string last="";
        try
        {
            for(int i=0;i<3;i++){last=$"[LCT TEST] 日本語 中文 😀 {i}\r\n二行目";await ClipboardWriter.SetTextAsync(last,owner.Handle);Check(Clipboard.GetText()==last,"Unicode selection exact match "+i);}
            var held=Hold(360);last="[LCT TEST] recovered";await ClipboardWriter.SetTextAsync(last,owner.Handle);held.Join();Check(Clipboard.GetText()==last,"temporary clipboard contention recovers");
            held=Hold(2100);bool failed=false;try{await ClipboardWriter.SetTextAsync("[LCT TEST] must not succeed",owner.Handle);}catch(System.ComponentModel.Win32Exception){failed=true;}held.Join();Check(failed&&Clipboard.GetText()==last,"persistent contention fails honestly and preserves contents");
            held=Hold(250);using var cancel=new CancellationTokenSource(30);bool cancelled=false;try{await ClipboardWriter.SetTextAsync("[LCT TEST] obsolete",owner.Handle,cancel.Token);}catch(OperationCanceledException){cancelled=true;}held.Join();Check(cancelled&&Clipboard.GetText()==last,"cancelled copy cannot overwrite newer selection");
        }
        finally{if(Clipboard.ContainsText()&&Clipboard.GetText()==last)await ClipboardWriter.SetTextAsync(previous,owner.Handle);}
    }
}
