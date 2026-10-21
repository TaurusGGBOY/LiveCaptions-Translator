using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using LiveCaptionsNotion;

static class Program
{
    static int passed;
    static string root = "";
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
    [STAThread] static void Main(string[] args)
    {
        if (args.Length == 2 && args[1] == "--live-enrichment") { root=args[0]; LiveEnrichment().GetAwaiter().GetResult(); return; }
        if (args.Length != 1) throw new ArgumentException("Pass an isolated test directory");
        root = args[0]; Directory.CreateDirectory(root);
        UI(); Core().GetAwaiter().GetResult();
        Console.WriteLine($"TOTAL {passed} checks passed. No real Notion API calls.");
    }
    static NoteJob Job(string selected = "大丈夫", string side = "ja", DateTimeOffset? time = null) => NoteJob.Capture(new(1,"大丈夫ですよ。無理しないで。","没关系，不要勉强。"), selected, side, time ?? DateTimeOffset.Parse("2026-10-21T15:59:59Z"), "test-parent");
    static JsonObject Entry(NoteJob _) => new() { ["term"]="大丈夫", ["meaning"]="没关系", ["romaji"]="daijoubu", ["kana"]="だいじょうぶ", ["dictionary"]="大丈夫", ["exampleJa"]="明日でも大丈夫です。", ["exampleZh"]="明天也可以。", ["review"]="AI整理，待核对" };
    static void UI()
    {
        var window=new Window(); var grid=new StackPanel(); window.Content=grid;
        var ja=new TextBlock(); var zh=new TextBlock();
        grid.Children.Add(new ContentControl{Content=ja}); grid.Children.Add(new ContentControl{Content=zh});
        var view=new SelectionView(window,ja,zh,false);
        view.Receive(new(1,"大丈夫ですよ。","没关系。"));
        Check(view.Japanese.IsReadOnly && view.Chinese.IsReadOnly,"both captions read-only");
        view.Japanese.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(!((MenuItem)view.Japanese.ContextMenu.Items[1]).IsEnabled,"empty-selection context menu disables Notion action");
        view.Japanese.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));view.Resume();
        view.Japanese.Select(0,3);
        view.Japanese.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(((MenuItem)view.Japanese.ContextMenu.Items[1]).IsEnabled,"selected-text context menu enables Notion action");
        view.Receive(new(2,"今日は晴れです。","今天晴天。"));
        Check(view.Frozen && view.Japanese.SelectedText=="大丈夫" && view.Chinese.Text=="没关系。","refresh preserves Japanese selection and paired Chinese");
        var capture=view.Capture(view.Japanese,"ja",DateTimeOffset.UtcNow,"test");
        Check(capture.Japanese=="大丈夫ですよ。"&&capture.Chinese=="没关系。","Japanese capture uses frozen pair");
        view.Japanese.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));view.Resume(); Check(view.Japanese.Text=="今日は晴れです。"&&!view.Frozen,"resume displays latest pair");
        view.Chinese.Select(2,2); view.Receive(new(3,"またね。","再见。"));
        var chinese=view.Capture(view.Chinese,"zh",DateTimeOffset.UtcNow,"test");
        Check(chinese.Selected=="晴天"&&chinese.Japanese=="今日は晴れです。","Chinese selection carries exact Japanese context");
        view.Resume(); view.Receive(new(2,"stale","stale"));
        Check(view.Japanese.Text=="またね。","late translation cannot roll back paired display");
        bool rejected=false; try {view.Capture(view.Chinese,"zh",DateTimeOffset.UtcNow,"test");}catch(ArgumentException){rejected=true;}
        Check(rejected,"empty selection cannot create note");
        var file=Path.Combine(root,"fake-token.dpapi"); Vault.Save(file,"test-only-not-a-credential");
        Check(Vault.Read(file)=="test-only-not-a-credential"&&!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("test-only-not-a-credential"),"Windows current-user DPAPI roundtrip and no plaintext");
        var history = new HistoryTextBox { Side = "zh", DataContext = new HistoryRow("大丈夫です。", "没关系。") };
        history.Refresh(); history.Select(0,3);
        history.DataContext = new HistoryRow("またね。", "再见。");
        var historic = history.Capture(DateTimeOffset.Parse("2026-10-21T16:00:01Z"), "test");
        Check(historic.Selected=="没关系"&&historic.Japanese=="大丈夫です。"&&historic.DateTitle=="2026年10月22日","history recycling preserves selected pair and uses collection date");
        history.End();Check(history.Text=="再见。","history interaction end applies pending record");
        var items = new ItemsControl(); var firstRows = new[]{new HistoryRow("古い", "旧的")}; var newRows = new[]{new HistoryRow("新しい", "新的")};
        HistoryView.SetItems(items, firstRows); HistoryView.Hold(items); HistoryView.SetItems(items,newRows);
        Check(ReferenceEquals(items.ItemsSource,firstRows),"history refresh deferred during interaction");
        HistoryView.Release(items);Check(ReferenceEquals(items.ItemsSource,newRows),"history latest list restored after interaction");
    }
    static JobStore Store(string name)=>new(Path.Combine(root,name));
    static async Task LiveEnrichment()
    {
        foreach(var j in new[]{Job(),Job("没关系","zh"),NoteJob.Capture(new(1,"今日は晴れです。","苹果手机。"),"苹果","zh",DateTimeOffset.UtcNow,"test")})
        {
            var result=await Enrichment.Generate(j);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{j.Side,j.Selected,Entry=result}));
        }
    }
    static async Task Core()
    {
        var before=Job(); var after=Job(time:DateTimeOffset.Parse("2026-10-21T16:00:00Z"));
        Check(before.DateTitle=="2026年10月21日"&&after.DateTitle=="2026年10月22日","Beijing midnight uses capture time");
        var store=Store("existing");store.Add(before);var api=new FakeApi{Existing=true};
        var worker=new NoteWorker(store,api,j=>Task.FromResult(Entry(j)));
        await worker.Step();Check(api.Created==0&&api.Appended==1,"existing date page reused");
        await worker.Step();Check(api.Appended==1,"completed job not duplicated");
        Check(store.Jobs().Single().DateTitle=="2026年10月21日","retry/upload does not recalculate date");
        var s2=Store("new");s2.Add(Job());s2.Add(Job("没关系","zh"));var a2=new FakeApi();
        var w2=new NoteWorker(s2,a2,j=>Task.FromResult(Entry(j)));await w2.Step();await w2.Step();
        Check(a2.Created==1&&a2.Appended==2,"new date created once and later notes appended");
        Check(a2.Saved.SequenceEqual(s2.Jobs().Select(Blocks.Marker)),"multiple notes remain FIFO");
        var s3=Store("offline");s3.Add(Job());var a3=new FakeApi{FailRead=true};var w3=new NoteWorker(s3,a3,j=>Task.FromResult(Entry(j)));
        await w3.Step();Check(s3.Jobs().Single().Phase=="queued"&&a3.Appended==0,"offline read preserves durable queued job");
        a3.FailRead=false;await w3.Step();Check(a3.Appended==1,"network recovery retries queued note");
        var s4=Store("lost-append");s4.Add(Job());var a4=new FakeApi{Existing=true,LoseAppend=true};
        var w4=new NoteWorker(s4,a4,j=>Task.FromResult(Entry(j)));await w4.Step();
        Check(s4.Jobs().Single().Phase=="appending","lost append response persisted as uncertain");
        await new NoteWorker(new JobStore(s4.Root),a4,j=>throw new Exception("must use cached entry")).Step();
        Check(a4.Appended==1&&s4.Jobs().Single().Phase=="done","restart reconciles committed append without duplicate");
        var s5=Store("unknown-append");s5.Add(Job());var a5=new FakeApi{Existing=true,DropAppend=true};var w5=new NoteWorker(s5,a5,j=>Task.FromResult(Entry(j)));
        await w5.Step();await w5.Step();Check(a5.Appended==1&&s5.Jobs().Single().Phase=="appending","uncertain unobserved append never blindly reposted");
        var s6=Store("lost-create");s6.Add(Job());var a6=new FakeApi{LoseCreate=true};var w6=new NoteWorker(s6,a6,j=>Task.FromResult(Entry(j)));
        await w6.Step();await w6.Step();Check(a6.Created==1&&a6.Appended==1,"lost create response reconciles existing date page");
        var s7=Store("concurrent");s7.Add(Job());s7.Add(Job());var a7=new FakeApi{Delay=true};
        var w7=new NoteWorker(s7,a7,j=>Task.FromResult(Entry(j)));var first=w7.Step();bool locked=false;
        try{await new NoteWorker(new JobStore(s7.Root),a7,j=>Task.FromResult(Entry(j))).Step();}catch(IOException){locked=true;}
        s7.Add(Job());await first;await w7.Step();await w7.Step();
        Check(locked&&a7.Created==1&&a7.Appended==3,"concurrent workers excluded while enqueue stays available");
        var s8=Store("rejected");s8.Add(Job());var a8=new FakeApi{Existing=true,RejectAppend=true};var w8=new NoteWorker(s8,a8,j=>Task.FromResult(Entry(j)));
        await w8.Step();Check(s8.Jobs().Single().Phase=="queued","definite rejection remains retryable");a8.RejectAppend=false;await w8.Step();Check(s8.Jobs().Single().Phase=="done","retry after definite rejection succeeds");
        var block=Blocks.Build(new NoteJob{Entry=Entry(Job()),Japanese="original ASR",Chinese="original translation",Selected="word",CollectedAt=DateTimeOffset.UtcNow});
        Check(block["toggle"]!["children"]!.AsArray().Count==6&&block.ToJsonString().Contains("original ASR"),"one toggle contains ordered fields and original transcript");
        var text=new string('日',2001)+"😀";Check(Blocks.Text(Blocks.Rich(text))==text,"long Unicode rich text chunking is lossless");
        var handler=new PagesHandler();using var real=new NotionApi("fake-test-token",handler);var children=await real.Children("test");
        Check(children.Count==2&&handler.Calls==2&&handler.HeaderOk,"official API pagination and authorization headers");
    }
}
public record HistoryRow(string SourceText, string TranslatedText);
sealed class FakeApi:INotesApi
{
    public bool Existing,FailRead,LoseAppend,DropAppend,LoseCreate,Delay,RejectAppend;
    public int Created,Appended; public List<string> Saved=new();readonly List<JsonObject> blocks=new();
    public async Task<List<JsonObject>> Children(string page)
    {
        if(Delay)await Task.Delay(30);if(FailRead)throw new HttpRequestException();
        if(page=="test-parent")return Existing?[new JsonObject{["type"]="child_page",["id"]="date",["child_page"]=new JsonObject{["title"]="2026年10月21日"}}]:[];
        return blocks;
    }
    public Task<string> CreateDate(string parent,string title){Created++;Existing=true;if(LoseCreate){LoseCreate=false;throw new HttpRequestException();}return Task.FromResult("date");}
    public Task Append(string page,JsonObject block)
    {
        if(RejectAppend)throw new RejectedRequest(429);Appended++;
        if(DropAppend)throw new HttpRequestException();
        blocks.Add(block);var title=Blocks.Text(block["toggle"]!["rich_text"]);Saved.Add(title[(title.IndexOf("LCT收藏:",StringComparison.Ordinal))..].TrimEnd('〕'));
        if(LoseAppend){LoseAppend=false;throw new HttpRequestException();}return Task.CompletedTask;
    }
}
sealed class PagesHandler:HttpMessageHandler
{
    public int Calls;public bool HeaderOk=true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Calls++;HeaderOk &= request.Headers.Authorization?.Parameter=="fake-test-token"&&request.Headers.Contains("Notion-Version");
        var body=Calls==1?"{\"results\":[{\"id\":\"a\"}],\"has_more\":true,\"next_cursor\":\"next\"}":"{\"results\":[{\"id\":\"b\"}],\"has_more\":false}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
    }
}
