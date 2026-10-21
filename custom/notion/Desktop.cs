using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LiveCaptionsNotion.Native;

namespace LiveCaptionsNotion;

// These three entry points are injected into the retained personalized assembly.
// They never throw back into translation or UI construction.
public static class Hooks
{
    sealed class Stamp { public long Value; }
    static readonly ConditionalWeakTable<object, Stamp> stamps = new();
    static readonly object sync = new();
    static long sequence;
    static CaptionPair? latest;
    public static event Action<CaptionPair>? Published;
    public static CaptionPair? Latest { get { lock (sync) return latest; } }
    public static void Register(object task)
    {
        try { stamps.GetValue(task, _ => new Stamp { Value = Interlocked.Increment(ref sequence) }); } catch { }
    }
    public static void Publish(object task)
    {
        try
        {
            var type = task.GetType();
            var cts = (CancellationTokenSource)type.GetProperty("CTS")!.GetValue(task)!;
            if (cts.IsCancellationRequested) return;
            var source = (string)type.GetProperty("OriginalText")!.GetValue(task)!;
            var pending = (Task<(string, bool)>)type.GetProperty("Task")!.GetValue(task)!;
            if (!pending.IsCompletedSuccessfully || string.IsNullOrWhiteSpace(pending.Result.Item1) || pending.Result.Item1.Contains("[ERROR]")) return;
            var stamp = stamps.GetValue(task, _ => new Stamp { Value = Interlocked.Increment(ref sequence) });
            var pair = new CaptionPair(stamp.Value, source, pending.Result.Item1);
            lock (sync) { if (latest != null && latest.Sequence >= pair.Sequence) return; latest = pair; }
            Published?.Invoke(pair);
        }
        catch { }
    }
    public static void Attach(object page)
    {
        try
        {
            var root = (FrameworkElement)page;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var ja = (TextBlock)page.GetType().GetField("OriginalCaption", flags)!.GetValue(page)!;
            var zh = (TextBlock)page.GetType().GetField("TranslatedCaption", flags)!.GetValue(page)!;
            _ = new SelectionView(root, ja, zh, true);
            HistoryView.AttachCards(page);
        }
        catch { /* Preserve the original UI if the retained installation is incompatible. */ }
    }
    public static void AttachHistory(object page) => HistoryView.Attach(page);
}

public static class SelectedClipboard
{
    static CancellationTokenSource? pending;
    public static async Task Write(string text, Window window)
    {
        pending?.Cancel(); using var request = new CancellationTokenSource(); pending = request;
        try { await ClipboardWriter.SetTextAsync(text, new WindowInteropHelper(window).Handle, request.Token); }
        finally { if (ReferenceEquals(pending, request)) pending = null; }
    }
}

public sealed class SelectionView
{
    readonly FrameworkElement owner;
    public TextBox Japanese { get; }
    public TextBox Chinese { get; }
    public CaptionPair? Displayed { get; private set; }
    CaptionPair? pending;
    public bool Frozen { get; private set; }
    bool menuOpen;
    bool updating;
    readonly TextBlock status = new() { FontSize = 11, Opacity = .8, TextWrapping = TextWrapping.Wrap };
    readonly bool useService;
    public SelectionView(FrameworkElement owner, TextBlock ja, TextBlock zh, bool useService)
    {
        this.owner = owner; this.useService = useService;
        Japanese = Replace(ja, "ja"); Chinese = Replace(zh, "zh");
        owner.Loaded += (_, _) => { Hooks.Published -= OnPair; Hooks.Published += OnPair; if (Hooks.Latest is { } pair) Receive(pair); };
        owner.Unloaded += (_, _) => Hooks.Published -= OnPair;
        if (useService)
        {
            _ = Runtime.Instance;
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => {
                status.Text = (Frozen ? "选区已暂停显示 · Esc恢复  |  " : "") + Runtime.Instance.Status;
            }, owner.Dispatcher);
            owner.Unloaded += (_, _) => timer.Stop(); owner.Loaded += (_, _) => timer.Start();
        }
    }
    void OnPair(CaptionPair pair) => owner.Dispatcher.BeginInvoke(() => Receive(pair));
    public void Receive(CaptionPair pair)
    {
        if (pending != null && pair.Sequence <= pending.Sequence) return;
        pending = pair;
        if (!Frozen) Display(pair);
    }
    void Display(CaptionPair pair)
    {
        updating = true;
        try { Displayed = pair; Japanese.Text = pair.Japanese; Chinese.Text = pair.Chinese; }
        finally { updating = false; }
    }
    public void Freeze() { Frozen = true; }
    public void Resume()
    {
        if (menuOpen) return;
        Frozen = false; updating = true;
        try { Japanese.Select(0, 0); Chinese.Select(0, 0); } finally { updating = false; }
        if (pending != null) Display(pending);
    }
    public NoteJob Capture(TextBox box, string side, DateTimeOffset now, string parent)
    {
        if (Displayed == null) throw new InvalidOperationException("No paired translation yet");
        return NoteJob.Capture(Displayed, box.SelectedText, side, now, parent);
    }
    TextBox Replace(TextBlock old, string side)
    {
        var box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "等待对应的翻译字幕…", ToolTip = "拖选文字，Ctrl+C复制，右键收藏；Esc恢复最新字幕" };
        box.SetBinding(Control.FontSizeProperty, new Binding("FontSize") { Source = old });
        box.SetBinding(Control.ForegroundProperty, new Binding("Foreground") { Source = old });
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (old.Parent is ContentControl content) content.Content = grid;
        else if (old.Parent is Panel panel) { var index = panel.Children.IndexOf(old); panel.Children.Remove(old); panel.Children.Insert(index, grid); }
        else throw new InvalidOperationException("Unsupported caption container");
        old.Visibility = Visibility.Collapsed; grid.Children.Add(old); grid.Children.Add(box);
        if (side == "zh") { Grid.SetRow(status, 1); grid.Children.Add(status); }
        box.PreviewMouseLeftButtonDown += (_, _) => Freeze();
        box.PreviewMouseRightButtonDown += (_, e) => { Freeze(); if (box.SelectionLength > 0) e.Handled = true; };
        box.PreviewMouseLeftButtonUp += (_, _) => owner.Dispatcher.BeginInvoke(() => { if (!menuOpen && box.SelectionLength == 0) Resume(); });
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Resume(); e.Handled = true; } else Freeze(); };
        box.SelectionChanged += (_, _) => { if (!updating && box.SelectionLength > 0) Freeze(); };
        box.LostKeyboardFocus += (_, _) => owner.Dispatcher.BeginInvoke(() => { if (!menuOpen && !Japanese.IsKeyboardFocusWithin && !Chinese.IsKeyboardFocusWithin) Resume(); });
        box.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, async (_, e) => { e.Handled = true; await Copy(box); }, (_, e) => { e.CanExecute = box.SelectionLength > 0; e.Handled = true; }));
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制", InputGestureText = "Ctrl+C" };
        copy.Click += async (_, _) => await Copy(box);
        var save = new MenuItem { Header = "添加到 Notion 日语笔记" };
        save.Click += async (_, _) =>
        {
            if (!useService) return;
            // Capture synchronously before closing the context menu or starting background work.
            try
            {
                var job = Capture(box, side, DateTimeOffset.UtcNow, Runtime.Instance.Configuration.Parent);
                if (string.IsNullOrWhiteSpace(job.Parent)) { status.Text = "请先在右键菜单中配置Notion"; return; }
                status.Text = "正在保存到本机队列";
                await Runtime.Instance.Enqueue(job);
                status.Text = "已加入收藏队列";
            }
            catch { status.Text = "本机收藏失败，请重试（未报告保存成功）"; }
        };
        var setup = new MenuItem { Header = "Notion 一次性设置…" };
        setup.Click += (_, _) => { if (useService) SetupWindow.ShowFor(Window.GetWindow(owner)); };
        var resume = new MenuItem { Header = "恢复最新字幕", InputGestureText = "Esc" }; resume.Click += (_, _) => { menuOpen = false; Resume(); };
        menu.Items.Add(copy); menu.Items.Add(save); menu.Items.Add(new Separator()); menu.Items.Add(setup); menu.Items.Add(resume);
        menu.Opened += (_, _) => { menuOpen = true; Freeze(); copy.IsEnabled = save.IsEnabled = box.SelectionLength > 0 && Displayed != null; };
        menu.Closed += (_, _) => { menuOpen = false; owner.Dispatcher.BeginInvoke(Resume); };
        box.ContextMenu = menu;
        return box;
    }
    async Task Copy(TextBox box)
    {
        var text = box.SelectedText;
        if (string.IsNullOrEmpty(text)) return;
        try { await SelectedClipboard.Write(text, Window.GetWindow(owner)); status.Text = "已复制"; }
        catch (OperationCanceledException) { }
        catch { status.Text = "复制失败：剪贴板仍被占用，请重试"; }
    }
}

public sealed class Runtime
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptionsTranslator", "Notion");
    static readonly Lazy<Runtime> instance = new(() => new Runtime());
    public static Runtime Instance => instance.Value;
    public JobStore Store { get; } = new(Path.Combine(Root, "queue"));
    public Settings Configuration { get; private set; } = new();
    public string Status { get; private set; } = "Notion未配置 · 右键设置";
    readonly object enqueueSync = new();
    Task enqueueTail = Task.CompletedTask;
    Runtime()
    {
        try { Configuration = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new(); } catch { }
        _ = Task.Run(Loop);
    }
    public Task Enqueue(NoteJob job)
    {
        lock (enqueueSync)
        {
            // Chain click handlers in invocation order even when the thread pool is busy.
            return enqueueTail = enqueueTail.ContinueWith(async _ => {
                for (int i=0;;i++) { try { Store.Add(job); return; } catch (IOException) when(i<20) { await Task.Delay(25); } }
            }, TaskScheduler.Default).Unwrap();
        }
    }
    public void Configure(string parent, string token)
    {
        if (!Guid.TryParse(parent, out var id)) throw new ArgumentException("Invalid page ID");
        if (!string.IsNullOrWhiteSpace(token)) Vault.Save(Path.Combine(Root, "token.dpapi"), token.Trim());
        Configuration = new Settings { Parent = id.ToString() };
        var path = Path.Combine(Root, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Configuration)); File.Move(path + ".tmp", path, true);
    }
    async Task Loop()
    {
        while (true)
        {
            bool progressed = false;
            try
            {
                if (!File.Exists(Path.Combine(Root, "token.dpapi"))) Status = "待授权 · 收藏保留在本机";
                else
                {
                    using var api = new NotionApi(Vault.Read(Path.Combine(Root, "token.dpapi")));
                    progressed = await new NoteWorker(Store, api, Enrichment.Generate).Step();
                    var jobs = Store.Jobs().ToList(); var head = jobs.FirstOrDefault(j => j.Phase != "done");
                    Status = head == null ? (jobs.Count > 0 ? "已保存 · " + jobs.Count + "条" : "Notion已配置") : head.Status + " · 待处理" + jobs.Count(j => j.Phase != "done");
                }
            }
            catch (IOException) { Status = "收藏队列忙，稍后重试"; }
            catch { Status = "待重试 · 请检查本机授权或队列"; }
            await Task.Delay(progressed ? 750 : 15000);
        }
    }
}

public static class Vault
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, "LiveCaptions Notion", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(bytes); }
    }
    public static void Save(string path, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", Transform(Encoding.UTF8.GetBytes(token), true)); File.Move(path + ".tmp", path, true);
    }
    public static string Read(string path) { var bytes = Transform(File.ReadAllBytes(path), false); try { return Encoding.UTF8.GetString(bytes); } finally { Array.Clear(bytes); } }
}
public static class SetupWindow
{
    public static void ShowFor(Window owner)
    {
        var runtime = Runtime.Instance;
        var window = new Window { Title = "Notion 日语笔记 · 一次性设置", Width = 540, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = "在Notion创建内部连接，启用读取、插入和更新内容；\n将‘大小姐直播间’页面连接给它。凭据仅在本机输入，\n使用Windows当前用户加密；无需每次登录。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "父页面ID（只填页面ID；测试时换成标注的测试父页）", Margin = new Thickness(0,15,0,5) });
        var parent = new TextBox { Text = runtime.Configuration.Parent }; panel.Children.Add(parent);
        panel.Children.Add(new TextBlock { Text = "内部连接令牌（已有授权时可留空）", Margin = new Thickness(0,12,0,5) });
        var token = new PasswordBox(); panel.Children.Add(token);
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,0) }; panel.Children.Add(result);
        var save = new Button { Content = "保存并检查页面访问", Margin = new Thickness(0,12,0,0), Padding = new Thickness(8) }; panel.Children.Add(save);
        save.Click += async (_, _) => {
            save.IsEnabled = false;
            try
            {
                if (!Guid.TryParse(parent.Text.Trim(), out var id)) throw new ArgumentException();
                var secret = string.IsNullOrWhiteSpace(token.Password) ? Vault.Read(Path.Combine(Runtime.Root, "token.dpapi")) : token.Password.Trim();
                using var api = new NotionApi(secret);
                await api.Children(id.ToString()); // Read-only authorization check; no test writes to the real parent.
                runtime.Configure(id.ToString(), secret); token.Clear(); result.Text = "授权已保存。可以关闭窗口，后续收藏在后台自动保存。";
            }
            catch { result.Text = "检查失败：确认页面ID、令牌和页面连接。凭据不会显示或记录到日志。"; }
            finally { save.IsEnabled = true; }
        };
        window.ShowDialog();
    }
}
