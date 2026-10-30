using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;

namespace Huanying;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "Huanying-crash.log");

    public App()
    {
        this.InitializeComponent();

        // 全局异常日志，便于定位 XAML 运行时错误
        this.UnhandledException += (s, e) =>
        {
            Log($"{e.Message}\n{e.Exception}");
        };
    }

    internal static void Log(string text)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch { /* 忽略日志写失败 */ }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();

            // 冒烟自检：设置 HY_SMOKE=1 时逐个实例化页面，验证 XAML 能否正常解析
            if (Environment.GetEnvironmentVariable("HY_SMOKE") == "1")
            {
                RunSmokeTest();
            }

            // 接口自检：设置 HY_APITEST=1 时请求一次 VOD 接口，把结果写进日志
            if (Environment.GetEnvironmentVariable("HY_APITEST") == "1")
            {
                _ = RunApiSelfTestAsync();
            }

            // 通道探测：HY_APITEST=2 时逐个尝试 TLS 握手变体
            if (Environment.GetEnvironmentVariable("HY_APITEST") == "2")
            {
                _ = RunProbeAsync();
            }

            // 聚合搜索自检：HY_AGGTEST=关键词 时并行查全部已配置源，把合并去重结果写进日志
            var aggKeyword = Environment.GetEnvironmentVariable("HY_AGGTEST");
            if (!string.IsNullOrWhiteSpace(aggKeyword))
            {
                _ = RunAggregateSelfTestAsync(aggKeyword);
            }

            // 跳过片头片尾存储自检：HY_SKIPTEST=1
            if (Environment.GetEnvironmentVariable("HY_SKIPTEST") == "1")
            {
                RunSkipStoreTest();
            }

            // JSON 源列表解析自检：HY_JSON=<文件路径>，或 HY_JSON=1 用仓库自带的 sources.example.json
            var jsonArg = Environment.GetEnvironmentVariable("HY_JSON");
            if (jsonArg is { Length: > 0 })
            {
                RunJsonImportTest(jsonArg == "1" ? null : jsonArg);
            }

            // 页面导航自检：HY_NAV=detail[:id] / play[:id] 会真实导航并打印页面状态
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HY_NAV")))
            {
                _ = RunNavigationTestAsync();
            }
            else if (!string.IsNullOrWhiteSpace(ShotPath))
            {
                // 只截图外壳（含侧边栏）
                _ = ShotShellAsync();
            }
        }
        catch (Exception ex)
        {
            Log($"OnLaunched failed: {ex}");
            throw;
        }
    }

    /// <summary>诊断用：把当前窗口内容渲染成 PNG（HY_SHOT=路径 触发）</summary>
    private static async System.Threading.Tasks.Task CaptureScreenshotAsync(string path)
    {
        try
        {
            if (MainWindow?.Content is not Microsoft.UI.Xaml.UIElement root) return;

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(root);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var pixels = await bitmap.GetPixelsAsync();

            var pixelReader = Windows.Storage.Streams.DataReader.FromBuffer(pixels);
            var pixelBytes = new byte[pixels.Length];
            pixelReader.ReadBytes(pixelBytes);
            pixelReader.Dispose();

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);

            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)width, (uint)height, 96, 96,
                pixelBytes);

            await encoder.FlushAsync();

            stream.Seek(0);
            var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);

            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            reader.Dispose();

            File.WriteAllBytes(path, bytes);
            Log($"SHOT OK: {path} {width}x{height}");
        }
        catch (Exception ex)
        {
            Log($"SHOT FAIL: {ex}");
        }
    }

    private static string? ShotPath => Environment.GetEnvironmentVariable("HY_SHOT");

    private static async System.Threading.Tasks.Task ShotAsync(string suffix)
    {
        var basePath = ShotPath;
        if (string.IsNullOrWhiteSpace(basePath)) return;

        var target = basePath.Contains("{0}")
            ? string.Format(basePath, suffix)
            : Path.Combine(Path.GetDirectoryName(basePath) ?? ".", $"{Path.GetFileNameWithoutExtension(basePath)}-{suffix}.png");

        await CaptureScreenshotAsync(target);
    }

    /// <summary>只截外壳（含侧边栏），HY_SHOT 但不带 HY_NAV 时触发</summary>
    private static async System.Threading.Tasks.Task ShotShellAsync()
    {
        // 等首屏数据 + 海报加载完成
        var delay = int.TryParse(Environment.GetEnvironmentVariable("HY_SHOT_DELAY"), out var d) ? d : 6000;
        await System.Threading.Tasks.Task.Delay(delay);

        if (MainWindow?.Content is Microsoft.UI.Xaml.Controls.Frame frame &&
            frame.Content is Views.ShellPage shell &&
            shell.DebugCurrentPage() is { Length: > 0 } state)
        {
            Log($"SHELL STATE: {state}");
        }

        await ShotAsync("shell");
    }

    private static async System.Threading.Tasks.Task RunProbeAsync()
    {
        var api = Services.AppServices.Vod;
        var url = $"{api.BaseUrl}/?ac=list";
        Log($"PROBE START: {url}");
        Log(await Services.SniProbe.RunAsync(url));
    }

    /// <summary>
    /// 页面导航自检：HY_NAV=detail[:id] 或 play[:id]。
    /// 真实的导航到详情页 / 播放页，并把页面状态写进日志（崩溃也能看到原因）。
    /// </summary>
    private static async System.Threading.Tasks.Task RunNavigationTestAsync()
    {
        var spec = Environment.GetEnvironmentVariable("HY_NAV");
        if (string.IsNullOrWhiteSpace(spec)) return;

        try
        {
            await System.Threading.Tasks.Task.Delay(1500);

            // 订阅自检：HY_SUB=<订阅链接> 时先导入一次，验证解析、去重与落盘
            if (Environment.GetEnvironmentVariable("HY_SUB") is { Length: > 0 } subUrl)
            {
                try
                {
                    var entries = await Services.SubscriptionService.FetchAsync(subUrl);
                    Log($"SUB FETCH: {entries.Count} 条");
                    foreach (var entry in entries) Log($"  · name='{entry.Name}' url={entry.Url}");

                    var (added, skipped) = Services.AppServices.Sources.Import(entries, subUrl);
                    Log($"SUB IMPORT: added={added} skipped={skipped} " +
                        $"total={Services.AppServices.Sources.Items.Count} " +
                        $"active='{Services.AppServices.Sources.ActiveName}'");
                }
                catch (Exception ex)
                {
                    Log($"SUB FAIL: {ex.GetType().Name} {ex.Message}");
                }
            }

            if (MainWindow?.Content is not Microsoft.UI.Xaml.Controls.Frame root)
            {
                Log("NAV FAIL: 找不到根 Frame");
                return;
            }

            var api = Services.AppServices.Vod;
            var parts = spec.Split(':', StringSplitOptions.RemoveEmptyEntries);
            var target = parts[0].Trim().ToLowerInvariant();
            var vodId = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 0;

            // 外壳内导航自检（保留侧边栏，用于生成 README 样板图）：
            //   HY_NAV=shell:home / shell:detail:1002 / shell:source / shell:settings / shell:favorites / shell:history
            if (target == "shell")
            {
                var page = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "home";
                var id = parts.Length > 2 && int.TryParse(parts[2], out var parsedId) ? parsedId : 0;

                if (root.Content is not Views.ShellPage shell)
                {
                    Log($"NAV FAIL shell: 根内容不是 ShellPage（{root.Content?.GetType().Name}）");
                    return;
                }

                if (page == "detail")
                {
                    if (id <= 0)
                    {
                        var list = await api.GetListAsync(1);
                        id = list.List.Count > 0 ? list.List[0].Id : 0;
                    }
                    shell.NavigateTo(typeof(Views.ShowDetailPage), id);
                }
                else
                {
                    var pageType = page switch
                    {
                        "source" => typeof(Views.SourceListPage),
                        "settings" => typeof(Views.SettingsPage),
                        "favorites" => typeof(Views.FavoritesPage),
                        "history" => typeof(Views.HistoryPage),
                        _ => typeof(Views.HomePage)
                    };
                    shell.NavigateTo(pageType);
                }

                await System.Threading.Tasks.Task.Delay(12000);
                Log($"NAV OK shell:{page}: {shell.DebugCurrentPage()}");
                await ShotAsync(page);
                return;
            }

            // 首页（瀑布流 + 图片队列）：不需要 ID
            if (target == "home")
            {
                root.Navigate(typeof(Views.HomePage));
                await System.Threading.Tasks.Task.Delay(18000);
                Log(root.Content is Views.HomePage home
                    ? $"NAV OK home: {home.DebugState()}"
                    : $"NAV FAIL home: content={root.Content?.GetType().Name}");
                await ShotAsync("home");
                return;
            }

            // 源管理：不需要 ID
            if (target == "source")
            {
                root.Navigate(typeof(Views.SourceListPage));
                await System.Threading.Tasks.Task.Delay(2500);
                Log(root.Content is Views.SourceListPage sources
                    ? $"NAV OK source: {sources.DebugState()}"
                    : $"NAV FAIL source: content={root.Content?.GetType().Name}");
                await ShotAsync("source");
                return;
            }

            // 切换源 → 回首页看是否换了数据源
            if (target == "switch")
            {
                var store = Services.AppServices.Sources;
                var original = store.Active;
                var other = store.Items.FirstOrDefault(s => !ReferenceEquals(s, original));

                if (other == null)
                {
                    Log("NAV FAIL switch: 只有一条源，无法切换");
                    return;
                }

                store.SetActive(other);
                Log($"SWITCH: {original?.Name} -> {other.Name} base={api.BaseUrl}");

                root.Navigate(typeof(Views.HomePage));
                await System.Threading.Tasks.Task.Delay(14000);
                Log(root.Content is Views.HomePage switched
                    ? $"NAV OK switch-home: {switched.DebugState()}"
                    : $"NAV FAIL switch-home: content={root.Content?.GetType().Name}");

                if (original != null) store.SetActive(original);
                Log($"SWITCH BACK: {store.Active?.Name} base={api.BaseUrl}");
                return;
            }

            if (vodId <= 0)
            {
                var list = await api.GetListAsync(1);
                vodId = list.List.Count > 0 ? list.List[0].Id : 0;
            }

            if (vodId <= 0)
            {
                Log("NAV FAIL: 接口没有返回可用 ID");
                return;
            }

            if (target == "detail")
            {
                root.Navigate(typeof(Views.ShowDetailPage), vodId);
                await System.Threading.Tasks.Task.Delay(6000);
                Log(root.Content is Views.ShowDetailPage detail
                    ? $"NAV OK detail: {detail.DebugState()}"
                    : $"NAV FAIL detail: content={root.Content?.GetType().Name}");
                return;
            }

            if (target == "play")
            {
                var item = await api.GetDetailAsync(vodId);
                if (item == null || !item.HasPlaySource)
                {
                    Log($"NAV FAIL play: id={vodId} 无播放线路");
                    return;
                }

                var episodes = item.Sources[0].Episodes;
                var playerArgs = new Models.PlayerArgs
                {
                    VodId = item.Id,
                    VodName = item.Name,
                    Title = $"{item.Name} · {episodes[0].Name}",
                    PosterUrl = item.Pic,
                    Episodes = episodes,
                    StartIndex = 0
                };

                // 走真实链路：播放页在 ShellPage 里面，全屏时才能验证侧边栏是否收起
                if (root.Content is Views.ShellPage host) host.NavigateTo(typeof(Views.PlayerPage), playerArgs);
                else root.Navigate(typeof(Views.PlayerPage), playerArgs);

                await System.Threading.Tasks.Task.Delay(16000);

                var player = root.Content as Views.PlayerPage
                             ?? (root.Content as Views.ShellPage)?.CurrentContent as Views.PlayerPage;
                Log(player != null
                    ? $"NAV OK play: {player.DebugState()}"
                    : $"NAV FAIL play: content={root.Content?.GetType().Name}");

                // 全屏自检：HY_FS=1 时切一次全屏、截图、再还原，验证侧边栏是否收起
                if (Environment.GetEnvironmentVariable("HY_FS") == "1" && player != null)
                {
                    player.DebugToggleFullScreen();
                    await System.Threading.Tasks.Task.Delay(2500);
                    Log($"NAV FULLSCREEN: {player.DebugChromeState()}");
                    await ShotAsync("player-fs");

                    player.DebugToggleFullScreen();
                    await System.Threading.Tasks.Task.Delay(1800);
                    Log($"NAV RESTORE: {player.DebugChromeState()}");
                    await ShotAsync("player-restored");
                }

                // 音量自检：HY_VOL=<0-100> 时写到系统音量再回读
                if (Environment.GetEnvironmentVariable("HY_VOL") is { Length: > 0 } volSpec
                    && int.TryParse(volSpec, out var volPercent) && player != null)
                {
                    Log($"VOL SET: {player.DebugSetVolume(volPercent)}");
                }

                // 常亮自检：HY_AWAKE=1 时申请一次、打印状态、再归还
                if (Environment.GetEnvironmentVariable("HY_AWAKE") == "1" && player != null)
                {
                    Log($"NAV AWAKE ON: {player.DebugKeepAwake(true)}");
                    await System.Threading.Tasks.Task.Delay(1500);
                    Log($"NAV AWAKE OFF: {player.DebugKeepAwake(false)}");
                }

                // 快捷键映射自检：HY_KEYTEST=1 时逐个走一遍 ← → ↑ ↓ 空格 与一个无关键
                if (Environment.GetEnvironmentVariable("HY_KEYTEST") == "1" && player != null)
                {
                    Log($"NAV KEYS: {player.DebugShortcuts()}");
                    Log($"NAV KEYS AFTER: {player.DebugState()}");
                }

                if (Environment.GetEnvironmentVariable("HY_INFO") == "1" && player != null)
                {
                    Log($"NAV INFO: {await player.DebugInfoTextAsync()}");
                    await ShotAsync("player-info");
                }

                var history = Services.LibraryStore.Current.GetHistory(item.Id);
                Log(history == null
                    ? "LIB: 尚未记录历史"
                    : $"LIB: {history.Name} 第{history.EpisodeIndex}集 {history.Position:0}s/{history.Duration:0}s 进度{history.Progress:0}%");
                return;
            }

            Log($"NAV FAIL: 未知目标 '{target}'");
        }
        catch (Exception ex)
        {
            Log($"NAV EXCEPTION: {ex}");
        }
    }

    private static async System.Threading.Tasks.Task RunApiSelfTestAsync()
    {
        var api = Services.AppServices.Vod;
        Log($"APITEST START: {api.BaseUrl}");

        try
        {
            var response = await api.GetListAsync(1);
            var first = response.List.Count > 0 ? response.List[0] : null;
            Log($"APITEST OK   : page={response.Page}/{response.PageCount} total={response.Total} " +
                $"items={response.List.Count} first={first?.Name ?? "-"} " +
                $"episodes={(first == null ? 0 : first.EpisodeCount)} " +
                $"sniFallback={api.LastUsedFallback}");
        }
        catch (Exception ex)
        {
            Log($"APITEST FAIL : {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 聚合搜索自检：并行查询全部已配置源，打印每个源的结果与合并去重后的命中。
    /// 触发方式：HY_AGGTEST=关键词
    /// </summary>
    private static async System.Threading.Tasks.Task RunAggregateSelfTestAsync(string keyword)
    {
        var sources = Services.AppServices.Sources.Items
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .ToList();

        Log($"AGGTEST START: keyword={keyword} sources={sources.Count}");
        foreach (var s in sources)
        {
            Log($"AGGTEST SRC  : {s.Name} -> {s.Url} (active={s.IsActive})");
        }

        try
        {
            var service = new Services.AggregateSearchService(Services.AppServices.Settings);
            var result = await service.SearchAsync(keyword);

            Log($"AGGTEST 总数  : sources={result.SourceCount} ok={result.SuccessCount} " +
                $"failed={result.FailedCount} raw={result.RawCount} hits={result.Hits.Count}");

            foreach (var r in result.SourceResults)
            {
                Log($"AGGTEST 源结果: {r.SourceName} success={r.Success} items={r.Items.Count} " +
                    $"err={r.Error ?? "-"}");
            }

            foreach (var hit in result.Hits.Take(12))
            {
                Log($"AGGTEST 命中  : {hit.Item.Name} ({hit.Item.Year}) id={hit.Item.Id} " +
                    $"来源={hit.SourceLabel} 源数={hit.SourceNames.Count} url={hit.SourceUrl}");
            }

            Log("AGGTEST DONE");
        }
        catch (Exception ex)
        {
            Log($"AGGTEST FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 跳过片头/片尾的存储自检：写入 → 读回 → 落盘 → 删除 → 确认清理。
    /// 用不存在的影片 ID，避免动到用户的真实设置。
    /// </summary>
    private static void RunSkipStoreTest()
    {
        const int testId = 999001;
        var store = Services.LibraryStore.Current;
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Huanying", "library.json");

        try
        {
            store.RemoveSkip(testId);

            store.SetSkip(testId, 42, 18);
            var saved = store.GetSkip(testId);
            Log($"SKIPTEST 写入  : intro={saved?.IntroSeconds} outro={saved?.OutroSeconds} " +
                $"(期望 42/18) → {(saved?.IntroSeconds == 42 && saved?.OutroSeconds == 18 ? "PASS" : "FAIL")}");

            var text = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
            Log($"SKIPTEST 落盘  : 文件含 {testId} = {text.Contains(testId.ToString())} " +
                $"含\"IntroSeconds\" = {text.Contains("IntroSeconds")}");

            store.SetSkip(testId, 0, 25);
            var updated = store.GetSkip(testId);
            Log($"SKIPTEST 更新  : intro={updated?.IntroSeconds} outro={updated?.OutroSeconds} " +
                $"(期望 0/25) → {(updated?.IntroSeconds == 0 && updated?.OutroSeconds == 25 ? "PASS" : "FAIL")}");

            store.RemoveSkip(testId);
            var removed = store.GetSkip(testId);
            Log($"SKIPTEST 删除  : 读回 = {(removed == null ? "null(PASS)" : "仍有值(FAIL)")}");

            var after = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
            Log($"SKIPTEST 清理  : 文件仍含 {testId} = {after.Contains(testId.ToString())} (期望 False)");
            Log("SKIPTEST DONE");
        }
        catch (Exception ex)
        {
            Log($"SKIPTEST FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// JSON / 文本源列表解析自检：解析指定文件（或仓库自带的 sources.example.json），
    /// 打印解析出的条目，并把结果导入本地库验证去重逻辑。
    /// </summary>
    private static void RunJsonImportTest(string? path)
    {
        var file = path ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sources.example.json");

        try
        {
            file = Path.GetFullPath(file);
            Log($"JSONTEST START: {file}");

            if (!File.Exists(file))
            {
                Log($"JSONTEST FAIL : 文件不存在（{file}）");
                return;
            }

            var text = File.ReadAllText(file);
            var entries = Services.SubscriptionService.Parse(text);

            Log($"JSONTEST 解析  : {entries.Count} 条");
            foreach (var entry in entries)
            {
                Log($"JSONTEST 条目  : name='{entry.Name}' url={entry.Url} note='{entry.Note}'");
            }

            var (added, skipped) = Services.AppServices.Sources.ImportFromFile(entries, "jsontest.json");
            Log($"JSONTEST 导入  : added={added} skipped={skipped} " +
                $"total={Services.AppServices.Sources.Items.Count} " +
                $"active='{Services.AppServices.Sources.ActiveName}'");

            // 重复导入同一份文件，应该全部跳过
            var (added2, skipped2) = Services.AppServices.Sources.ImportFromFile(entries, "jsontest.json");
            Log($"JSONTEST 重复  : added={added2} skipped={skipped2} (期望 added=0)");
            Log("JSONTEST DONE");
        }
        catch (Exception ex)
        {
            Log($"JSONTEST FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunSmokeTest()
    {
        var pages = new (string Name, Func<object> Factory)[]
        {
            ("HomePage", () => new Views.HomePage()),
            ("FavoritesPage", () => new Views.FavoritesPage()),
            ("HistoryPage", () => new Views.HistoryPage()),
            ("SettingsPage", () => new Views.SettingsPage()),
            ("SourceListPage", () => new Views.SourceListPage()),
            ("ShowDetailPage", () => new Views.ShowDetailPage()),
            ("PlayerPage", () => new Views.PlayerPage()),
        };

        foreach (var (name, factory) in pages)
        {
            try
            {
                factory();
                Log($"SMOKE OK   : {name}");
            }
            catch (Exception ex)
            {
                Log($"SMOKE FAIL : {name} -> {ex}");
            }
        }
    }
}
