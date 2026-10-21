using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LiveCaptionsNotion.Native;

namespace LiveCaptionsNotion;

public static class HistoryView
{
    sealed class State { public int Readers; public IEnumerable? Pending; public bool HasPending; }
    static readonly ConditionalWeakTable<ItemsControl, State> states = new();
    public static void SetItems(ItemsControl control, IEnumerable? source)
    {
        var state = states.GetOrCreateValue(control);
        if (state.Readers > 0) { state.Pending = source; state.HasPending = true; }
        else control.ItemsSource = source;
    }
    public static void Hold(ItemsControl control) => states.GetOrCreateValue(control).Readers++;
    public static void Release(ItemsControl control)
    {
        var state = states.GetOrCreateValue(control); state.Readers = Math.Max(0, state.Readers - 1);
        if (state.Readers == 0 && state.HasPending)
        { var source = state.Pending; state.Pending = null; state.HasPending = false; control.ItemsSource = source; }
    }
    public static void Attach(object page)
    {
        try
        {
            var grid = (DataGrid)page.GetType().GetField("HistoryDataGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(page)!;
            Configure(grid);
            if (grid.Parent is Grid layout)
            {
                var status = new TextBlock { FontSize = 11, Margin = new Thickness(0,4,0,0), Opacity = .8 };
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(status, layout.RowDefinitions.Count - 1); layout.Children.Add(status);
                var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
                    (_, _) => status.Text = "历史快照 · 重新进入更新  |  " + Runtime.Instance.Status, grid.Dispatcher);
                grid.Loaded += (_, _) => timer.Start(); grid.Unloaded += (_, _) => timer.Stop();
            }
        }
        catch { }
    }
    public static void Configure(DataGrid grid)
    {
        foreach (var (index, side) in new[] { (1, "ja"), (2, "zh") })
        {
            var element = new FrameworkElementFactory(typeof(HistoryTextBox)); element.SetValue(HistoryTextBox.SideProperty, side);
            ((DataGridTemplateColumn)grid.Columns[index]).CellTemplate = new DataTemplate { VisualTree = element };
        }
    }
    public static void AttachCards(object page)
    {
        try
        {
            var root = (FrameworkElement)page;
            var items = (ItemsControl)page.GetType().GetField("LogCardItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(page)!;
            var source = items.ItemsSource; BindingOperations.ClearBinding(items, ItemsControl.ItemsSourceProperty); SetItems(items, source);
            void Refresh()
            {
                var captions = root.DataContext;
                if (captions != null) SetItems(items, (IEnumerable?)captions.GetType().GetProperty("DisplayLogCards")?.GetValue(captions));
            }
            PropertyChangedEventHandler changed = (_, e) => { if (e.PropertyName == "DisplayLogCards") root.Dispatcher.BeginInvoke(Refresh); };
            root.Loaded += (_, _) => { if (root.DataContext is INotifyPropertyChanged n) { n.PropertyChanged -= changed; n.PropertyChanged += changed; } Refresh(); };
            root.Unloaded += (_, _) => { if (root.DataContext is INotifyPropertyChanged n) n.PropertyChanged -= changed; };
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0,0,0,8));
            foreach (var (side, size) in new[] { ("ja", 12.0), ("zh", 16.0) })
            {
                var box = new FrameworkElementFactory(typeof(HistoryTextBox));
                box.SetValue(HistoryTextBox.SideProperty, side); box.SetValue(Control.FontSizeProperty, size);
                box.SetValue(Control.PaddingProperty, new Thickness(8,4,8,4));
                box.SetBinding(Control.ForegroundProperty, new Binding("Foreground") { Source = items });
                stack.AppendChild(box);
            }
            items.ItemTemplate = new DataTemplate { VisualTree = stack };
        }
        catch { }
    }
    public static ItemsControl? Owner(DependencyObject child)
    {
        for (var p = VisualTreeHelper.GetParent(child); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ItemsControl items) return items;
        return null;
    }
}

public sealed class HistoryTextBox : TextBox
{
    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(nameof(Side), typeof(string), typeof(HistoryTextBox), new PropertyMetadata("ja", (d, _) => ((HistoryTextBox)d).Refresh()));
    public string Side { get => (string)GetValue(SideProperty); set => SetValue(SideProperty, value); }
    CaptionPair? pair;
    bool frozen, menuOpen, updating;
    ItemsControl? held;
    public HistoryTextBox()
    {
        IsReadOnly = true; IsReadOnlyCaretVisible = true; TextWrapping = TextWrapping.Wrap;
        BorderThickness = new Thickness(0); Background = Brushes.Transparent; Padding = new Thickness(2);
        SetBinding(ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1) });
        ToolTip = "历史字幕：拖选词语，右键添加到Notion；Esc结束选择";
        DataContextChanged += (_, _) => { if (!frozen) Refresh(); };
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { if (!menuOpen) End(); };
        PreviewMouseLeftButtonDown += (_, _) => Begin();
        PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(() => { if (SelectionLength == 0 && !menuOpen) End(); });
        PreviewMouseRightButtonDown += (_, e) => { Begin();  };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { End(); e.Handled = true; } else Begin(); };
        SelectionChanged += (_, _) => { if (!updating && SelectionLength > 0) Begin(); };
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (!menuOpen) End(); });
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, async (_, e) => { e.Handled = true; await CopySelected(); }, (_, e) => { e.CanExecute = SelectionLength > 0; e.Handled = true; }));
        var menu = new ContextMenu(); var copy = new MenuItem { Header = "复制", InputGestureText = "Ctrl+C" };
        copy.Click += async (_, _) => await CopySelected();
        var save = new MenuItem { Header = "添加到 Notion 日语笔记" };
        save.Click += async (_, _) => {
            try
            {
                var job = Capture(DateTimeOffset.UtcNow, Runtime.Instance.Configuration.Parent);
                if (string.IsNullOrEmpty(job.Parent)) { ToolTip = "请先完成Notion一次性设置"; Feedback.Show((string)ToolTip, true); return; }
                await Runtime.Instance.Enqueue(job); ToolTip = "已加入收藏队列；主字幕窗口显示保存状态";
            }
            catch { ToolTip = "收藏失败，请重新选择后重试"; Feedback.Show((string)ToolTip, true); }
        };
        var setup = new MenuItem { Header = "Notion 一次性设置…" }; setup.Click += (_, _) => SetupWindow.ShowFor(Window.GetWindow(this));
        menu.Items.Add(copy); menu.Items.Add(save); menu.Items.Add(new Separator()); menu.Items.Add(setup);
        menu.Opened += (_, _) => { menuOpen = true; Begin(); copy.IsEnabled = save.IsEnabled = SelectionLength > 0 && pair != null; };
        menu.Closed += (_, _) => { menuOpen = false; Dispatcher.BeginInvoke(End); };
        ContextMenu = menu;
    }
    public void Refresh()
    {
        if (frozen) return;
        var value = DataContext;
        string Read(string name) => (string?)value?.GetType().GetProperty(name)?.GetValue(value) ?? "";
        pair = value == null ? null : new CaptionPair(0, Read("SourceText"), Read("TranslatedText"));
        updating = true; try { Text = pair == null ? "" : Side == "ja" ? pair.Japanese : pair.Chinese; } finally { updating = false; }
    }
    public void Begin()
    {
        if (frozen) return; frozen = true; held = HistoryView.Owner(this); if (held != null) HistoryView.Hold(held);
    }
    public void End()
    {
        if (menuOpen) return;
        frozen = false; updating = true; try { Select(0,0); } finally { updating = false; }
        var previous = held; held = null; if (previous != null) HistoryView.Release(previous); Refresh();
    }
    public NoteJob Capture(DateTimeOffset now, string parent) => NoteJob.Capture(pair ?? throw new InvalidOperationException(), SelectedText, Side, now, parent);
    async Task CopySelected()
    {
        var text = SelectedText; if (text.Length == 0) return;
        try { await SelectedClipboard.Write(text, Window.GetWindow(this)); ToolTip = "已复制"; }
        catch (OperationCanceledException) { }
        catch { ToolTip = "复制失败：剪贴板被占用，请重试"; }
    }
}
