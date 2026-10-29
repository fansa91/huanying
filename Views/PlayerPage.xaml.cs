using Huanying.Models;
using Huanying.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.System;

namespace Huanying.Views;

/// <summary>
/// 播放页。使用 MediaPlayerElement 播放 HLS(m3u8)，自绘控制层：
/// 进度 / 上下一集 / 音量 / 倍速 / 选集 / 画中画 / 全屏；
/// 手势：水平拖动快进快退、左半屏上下拖动调亮度、右半屏调音量、单击切换控制栏、双击全屏；
/// 快捷键：空格、←/→、↑/↓、F、Esc、M；支持断点续播与自动连播。
/// </summary>
public sealed partial class PlayerPage : Page
{
    private PlayerArgs? _args;
    private MediaPlayer? _mediaPlayer;
    private MediaPlaybackSession? _session;
    private MediaPlaybackItem? _playbackItem;

    /// <summary>视频 / 音频轨道的编码参数（媒体管道就绪后填充，用于「视频信息」面板）</summary>
    private VideoEncodingProperties? _videoProps;
    private AudioEncodingProperties? _audioProps;

    private readonly List<(string Label, string Value)> _infoLines = new();
    private int _infoTick;
    private string _infoText = string.Empty;

    /// <summary>m3u8 主播放列表声明的参数（帧率 / 码率补充来源）</summary>
    private HlsPlaylistInfo? _playlistInfo;
    private string? _playlistInfoUrl;
    private Task? _playlistTask;

    private int _index;
    private bool _isFullScreen;
    private bool _isPip;
    private bool _controlsVisible = true;
    private bool _updatingSlider;
    private bool _isScrubbing;

    /// <summary>当前影片的跳过片头 / 片尾设置（来自本地库）</summary>
    private SkipEntry? _skipEntry;

    /// <summary>当前加载的字幕轨（外挂字幕，自绘渲染）</summary>
    private SubtitleTrack? _subtitleTrack;

    /// <summary>字幕字号档位：0 小 / 1 中 / 2 大</summary>
    private int _subtitleFontScale = 1;

    /// <summary>当前播放地址（用于探测 m3u8 字幕轨）</summary>
    private string _currentUrl = string.Empty;

    /// <summary>待跳转的秒数，MediaOpened 后应用一次（用于断点续播）</summary>
    private double _pendingSeek;
    private bool _pendingSeekApplied;
    private double _resumedFrom;

    private DateTime _lastSaveAt = DateTime.MinValue;

    /// <summary>期望的播放状态（打开完成 / 跳转后据它决定要不要恢复播放）</summary>
    private bool _desiredPlaying;

    /// <summary>打开超时保护：某些源打不开会一直卡在 Opening</summary>
    private DateTime _openStartedAt = DateTime.MinValue;
    private bool _openTimeoutReported;

    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3.6) };
    private readonly DispatcherTimer _nextTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private int _nextCountdown;

    /* 手势状态 */
    private bool _dragging;
    private bool _dragMoved;
    private bool _dragIsSeek;
    private Point _dragOrigin;
    private double _dragStartSeconds;
    private double _dragTargetSeconds;
    private double _dragBaseVolume;
    private double _dragBaseBrightness;

    /* 音量绑定系统主音量 */
    private SystemVolumeService? _systemVolume;

    /// <summary>播放期间压住系统休眠与息屏（暂停 / 离开页面即归还）</summary>
    private readonly KeepAwakeService _keepAwake = new();

    /// <summary>诊断用：被子键处理函数接住的按键次数（HY_KEYTEST 自检）</summary>
    private int _keysHandled;

    /// <summary>正在把外部音量写回滑块，避免 ValueChanged 又反过来写系统音量</summary>
    private bool _applyingVolume;

    public PlayerPage()
    {
        this.InitializeComponent();

        _tickTimer.Tick += TickTimer_Tick;
        _hideTimer.Tick += (_, _) => HideControls();
        _nextTimer.Tick += NextTimer_Tick;
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            GestureHint.Visibility = Visibility.Collapsed;
        };

        // 鼠标移动即显示控制栏（滑块 / 按钮上的移动也要能触发）
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), handledEventsToo: true);

        // 拖动进度条：按下时暂停跟随，松开后跳转
        ProgressSlider.AddHandler(PointerPressedEvent,
            new PointerEventHandler((_, _) => _isScrubbing = true), handledEventsToo: true);
        ProgressSlider.AddHandler(PointerReleasedEvent,
            new PointerEventHandler((_, _) =>
            {
                if (!_isScrubbing) return;
                _isScrubbing = false;
                SeekTo(ProgressSlider.Value);
            }), handledEventsToo: true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler((_, _) =>
            {
                if (!_isScrubbing) return;
                _isScrubbing = false;
                SeekTo(ProgressSlider.Value);
            }), handledEventsToo: true);
    }

    /* ================= 生命周期 ================= */

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PlayerArgs args || args.Episodes.Count == 0)
        {
            ShowError("没有可播放的剧集。");
            return;
        }

        _args = args;
        _index = Math.Clamp(args.StartIndex, 0, args.Episodes.Count - 1);

        // 跳过片头 / 片尾设置
        _skipEntry = args.VodId > 0 ? LibraryStore.Current.GetSkip(args.VodId) : null;
        UpdateSkipButtonVisibility();

        // 续播：显式传入优先，否则读本地记录（同一集才续播）
        if (args.ResumeSeconds > 3)
        {
            _pendingSeek = args.ResumeSeconds;
        }
        else
        {
            var history = LibraryStore.Current.GetHistory(args.VodId);
            if (history != null && history.EpisodeIndex == _index + 1 && history.Position > 5)
            {
                _pendingSeek = history.Position;
            }
        }

        _mediaPlayer = new MediaPlayer { AutoPlay = false };
        _session = _mediaPlayer.PlaybackSession;

        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

        _session.PlaybackStateChanged += Session_PlaybackStateChanged;
        _session.BufferingStarted += Session_BufferingStarted;
        _session.BufferingEnded += Session_BufferingEnded;

        Player.SetMediaPlayer(_mediaPlayer);

        InitVolumeBinding();

        EpisodeList.ItemsSource = args.Episodes;
        TitleText.Text = string.IsNullOrWhiteSpace(args.VodName) ? args.Title : args.VodName;

        // 焦点收在播放页自己身上：播放器里的按钮 / 滑块都不参与焦点，
        // 于是 ← → 不会去挪侧边栏或别的控件，空格也不会去「点」上一次点过的按钮。
        Loaded += (_, _) => Focus(FocusState.Programmatic);

        _tickTimer.Start();
        PlayCurrent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        SaveProgress(force: true);

        _tickTimer.Stop();
        _hideTimer.Stop();
        _nextTimer.Stop();
        _hintTimer.Stop();

        // 离开播放页：把休眠 / 息屏策略交还系统
        _keepAwake.Release();

        RestorePresenter();

        if (_systemVolume != null)
        {
            _systemVolume.Changed -= SystemVolume_Changed;
            _systemVolume.Dispose();
            _systemVolume = null;
        }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;

            if (_session != null)
            {
                _session.PlaybackStateChanged -= Session_PlaybackStateChanged;
                _session.BufferingStarted -= Session_BufferingStarted;
                _session.BufferingEnded -= Session_BufferingEnded;
            }

            Player.SetMediaPlayer(null);
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
            _session = null;
        }

        if (_playbackItem != null)
        {
            _playbackItem.VideoTracksChanged -= PlaybackItem_VideoTracksChanged;
            _playbackItem.AudioTracksChanged -= PlaybackItem_AudioTracksChanged;
            _playbackItem = null;
        }

        _videoProps = null;
        _audioProps = null;

        base.OnNavigatedFrom(e);
    }

    /* ================= 播放 ================= */

    private void PlayCurrent()
    {
        if (_args == null || _mediaPlayer == null) return;

        var episode = _args.Episodes[_index];
        if (!Uri.TryCreate(episode.Url, UriKind.Absolute, out var uri))
        {
            ShowError($"播放地址无效：{episode.Url}");
            return;
        }

        HideError();
        CancelNextCountdown();

        _pendingSeekApplied = false;
        _videoProps = null;
        _audioProps = null;
        _playlistInfo = null;
        _playlistInfoUrl = null;
        _playlistTask = null;

        // 切集时先清掉旧字幕，再探测新地址里的字幕轨
        _currentUrl = episode.Url;
        SetSubtitleTrack(null, string.Empty);
        _ = ProbeHlsSubtitlesAsync(episode.Url);

        // 用 MediaPlaybackItem 包一层，才能拿到视频 / 音频轨道的编码参数（编解码器、帧率、码率）
        var mediaItem = new MediaPlaybackItem(MediaSource.CreateFromUri(uri));
        mediaItem.VideoTracksChanged += PlaybackItem_VideoTracksChanged;
        mediaItem.AudioTracksChanged += PlaybackItem_AudioTracksChanged;
        _playbackItem = mediaItem;

        _openStartedAt = DateTime.Now;
        _openTimeoutReported = false;

        _mediaPlayer.Source = mediaItem;

        // 音量走系统主音量（见 InitVolumeBinding），播放器自身保持满音量
        if (_systemVolume is { IsAvailable: true }) _mediaPlayer.Volume = 1;
        else _mediaPlayer.Volume = Math.Clamp(VolumePercent / 100.0, 0, 1);

        _desiredPlaying = true;
        _mediaPlayer.Play();
        SyncKeepAwake();

        TitleText.Text = string.IsNullOrWhiteSpace(_args.VodName) ? _args.Title : _args.VodName;
        SubtitleText.Text = $"{episode.Name} · 第 {_index + 1}/{_args.Episodes.Count} 集";

        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index + 1 < _args.Episodes.Count;

        SelectEpisodeInPanel();
        SkipIntroButton.Visibility = Visibility.Collapsed;
        SkipOutroButton.Visibility = Visibility.Collapsed;
        ShowControls();
        UpdatePlayStateUi();
    }

    private void PlayNext()
    {
        if (_args == null || _index + 1 >= _args.Episodes.Count) return;

        _index++;
        _pendingSeek = 0;
        PlayCurrent();
    }

    private void PlayPrevious()
    {
        if (_args == null || _index <= 0) return;

        _index--;
        _pendingSeek = 0;
        PlayCurrent();
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer == null || _session == null) return;

        // 明确记下意图：调用 Play() 后 PlaybackState 不保证立刻变 Playing，
        // 直接读状态会把「刚点了播放」当成暂停。
        if (_session.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _desiredPlaying = false;
        }
        else
        {
            _mediaPlayer.Play();
            _desiredPlaying = true;
        }

        SyncKeepAwake();
        ShowControls();
        UpdatePlayStateUi();
    }

    private void SeekBy(double seconds)
    {
        if (_session == null) return;

        var duration = _session.NaturalDuration.TotalSeconds;
        if (duration <= 0) return;

        var target = Math.Clamp(_session.Position.TotalSeconds + seconds, 0, duration - 0.5);
        _session.Position = TimeSpan.FromSeconds(target);

        ShowHint($"{(seconds > 0 ? "快进" : "快退")} {Math.Abs(seconds):0} 秒");
        ShowControls();
    }

    private void SeekTo(double seconds)
    {
        if (_session == null) return;

        var duration = _session.NaturalDuration.TotalSeconds;
        if (duration <= 0) return;

        _session.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration - 0.5));
        ShowControls();
    }

    /* ================= 跳过片头 / 片尾 ================= */

    /* ================= 字幕 ================= */

    /// <summary>按当前播放位置刷新字幕显示</summary>
    private void UpdateSubtitleDisplay(double position)
    {
        if (_subtitleTrack == null)
        {
            if (SubtitleOverlay.Visibility != Visibility.Collapsed)
                SubtitleOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var text = _subtitleTrack.TextAt(position);

        if (string.IsNullOrEmpty(text))
        {
            SubtitleOverlay.Visibility = Visibility.Collapsed;
            SubtitleOverlayText.Text = string.Empty;
            return;
        }

        SubtitleOverlayText.Text = text;
        SubtitleOverlayText.FontSize = _subtitleFontScale switch
        {
            0 => 17,
            2 => 24,
            _ => 20
        };
        SubtitleOverlay.Visibility = Visibility.Visible;
    }

    private void SetSubtitleTrack(SubtitleTrack? track, string sourceName)
    {
        _subtitleTrack = track;

        if (track == null || track.Count == 0)
        {
            SubtitleOverlay.Visibility = Visibility.Collapsed;
            SubtitleOverlayText.Text = string.Empty;
            SubtitleButtonText.Text = "字幕";
            ShowHint("字幕已关闭");
            return;
        }

        SubtitleButtonText.Text = string.IsNullOrWhiteSpace(sourceName) ? "字幕" : sourceName;
        ShowHint($"已加载字幕：{track.Name}（{track.Count} 条）");
    }

    /// <summary>打开本地字幕文件（SRT / VTT）</summary>
    private async void LoadSubtitleFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".srt");
            picker.FileTypeFilter.Add(".vtt");
            picker.FileTypeFilter.Add(".txt");

            // 非打包应用要用窗口句柄初始化，否则选择器拿不到 UI 线程
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var text = await File.ReadAllTextAsync(file.Path);
            var track = SubtitleParser.Parse(text, file.Name);

            if (track.Count == 0)
            {
                ShowHint("字幕文件里没有解析出内容");
                return;
            }

            SetSubtitleTrack(track, track.Name);
        }
        catch (Exception ex)
        {
            ShowHint($"字幕加载失败：{FirstLine(ex.Message)}");
        }
    }

    /// <summary>从字幕地址加载（SRT / VTT）</summary>
    private async void LoadSubtitleUrl_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "输入字幕文件地址（.srt / .vtt）",
            Text = string.Empty
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从地址加载字幕",
            Content = input,
            PrimaryButtonText = "加载",
            CloseButtonText = "取消"
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var url = input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        await LoadSubtitleFromUrlAsync(url, "远程字幕");
    }

    /// <summary>下载并加载字幕</summary>
    private async Task LoadSubtitleFromUrlAsync(string url, string sourceName)
    {
        try
        {
            var content = await HlsPlaylist.FetchSubtitleAsync(url);
            if (string.IsNullOrWhiteSpace(content))
            {
                ShowHint("字幕地址没有返回内容");
                return;
            }

            var track = SubtitleParser.Parse(content, sourceName);
            if (track.Count == 0)
            {
                ShowHint("字幕内容里没有解析出条目");
                return;
            }

            SetSubtitleTrack(track, sourceName);
        }
        catch (Exception ex)
        {
            ShowHint($"字幕加载失败：{FirstLine(ex.Message)}");
        }
    }

    /// <summary>探测 m3u8 里声明的字幕轨，找到就自动加载第一条</summary>
    private async Task ProbeHlsSubtitlesAsync(string mediaUrl)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl) ||
            !mediaUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var content = await HlsPlaylist.FetchSubtitleAsync(mediaUrl);
            if (string.IsNullOrWhiteSpace(content)) return;

            var tracks = HlsPlaylist.ParseSubtitleTracks(content);
            if (tracks.Count == 0) return;

            App.Log($"SUBTITLE TRACKS: {tracks.Count} 条 · {string.Join(", ", tracks.Select(t => t.DisplayName))}");

            // 优先默认轨，其次第一条
            var target = tracks.FirstOrDefault(t => t.IsDefault) ?? tracks[0];
            var absolute = HlsPlaylist.ResolveUri(mediaUrl, target.Uri);

            await LoadSubtitleFromUrlAsync(absolute, target.DisplayName);
        }
        catch (Exception ex)
        {
            App.Log($"SUBTITLE PROBE FAIL: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void CloseSubtitle_Click(object sender, RoutedEventArgs e)
    {
        SetSubtitleTrack(null, string.Empty);
    }

    /// <summary>C 键：字幕开关 / 字号循环（小 → 中 → 大）</summary>
    private void ToggleSubtitle()
    {
        if (_subtitleTrack == null)
        {
            ShowHint("当前没有字幕，可用「字幕」按钮加载");
            return;
        }

        _subtitleFontScale = (_subtitleFontScale + 1) % 3;

        ShowHint(_subtitleFontScale switch
        {
            0 => "字幕字号：小",
            2 => "字幕字号：大",
            _ => "字幕字号：中"
        });
    }

    /// <summary>取异常信息的第一行，过长的截断（给提示气泡用）</summary>
    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private void SubtitleMenu_Opened(object sender, object e)
    {
        SubtitleCloseItem.IsEnabled = _subtitleTrack != null;
    }

    /// <summary>底部「跳过」按钮是否可用：该影片配置了片头或片尾才显示</summary>
    private void UpdateSkipButtonVisibility()
    {
        var configured = _skipEntry is { IntroSeconds: > 0 } or { OutroSeconds: > 0 };
        SkipButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>检测是否进入片头区域，显示「跳过片头」按钮</summary>
    private void CheckSkipIntro(double position, double duration)
    {
        if (_skipEntry == null || _skipEntry.IntroSeconds <= 0)
        {
            SkipIntroButton.Visibility = Visibility.Collapsed;
            return;
        }

        // 从第 1 秒起进入片头区域（避免续播时一进来就触发），片头结束 +2 秒缓冲
        var introEnd = _skipEntry.IntroSeconds;
        SkipIntroButton.Visibility =
            position >= 1 && position <= introEnd + 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>检测是否进入片尾区域，显示「跳过片尾」按钮</summary>
    private void CheckSkipOutro(double position, double duration)
    {
        if (_skipEntry == null || _skipEntry.OutroSeconds <= 0 || duration <= 0)
        {
            SkipOutroButton.Visibility = Visibility.Collapsed;
            return;
        }

        var outroStart = duration - _skipEntry.OutroSeconds;
        SkipOutroButton.Visibility =
            position >= outroStart - 2 && position < duration - 1
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SkipIntroNow_Click(object sender, RoutedEventArgs e)
    {
        if (_skipEntry == null || _skipEntry.IntroSeconds <= 0) return;

        SeekTo(_skipEntry.IntroSeconds);
        SkipIntroButton.Visibility = Visibility.Collapsed;
        ShowHint("已跳过片头");
    }

    private void SkipOutroNow_Click(object sender, RoutedEventArgs e)
    {
        if (_skipEntry == null || _skipEntry.OutroSeconds <= 0 || _session == null) return;

        var duration = _session.NaturalDuration.TotalSeconds;
        if (duration <= 0) return;

        SeekTo(Math.Max(0, duration - _skipEntry.OutroSeconds));
        SkipOutroButton.Visibility = Visibility.Collapsed;
        ShowHint("已跳过片尾");
    }

    private void SkipIntro_Click(object sender, RoutedEventArgs e) => SkipIntroNow_Click(sender, e);

    private void SkipOutro_Click(object sender, RoutedEventArgs e) => SkipOutroNow_Click(sender, e);

    /// <summary>打开详情页设置片头 / 片尾</summary>
    private void OpenSkipSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_args == null) return;

        Frame.Navigate(typeof(ShowDetailPage), _args.VodId);
    }

    private void Skip_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SkipByShortcut();
        args.Handled = true;
    }

    private void Subtitle_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleSubtitle();
        args.Handled = true;
    }

    /// <summary>J 键：在片头区域内跳过片头，在片尾区域内跳过片尾</summary>
    private void SkipByShortcut()
    {
        if (_skipEntry == null || _session == null) return;

        var position = _session.Position.TotalSeconds;
        var duration = _session.NaturalDuration.TotalSeconds;

        if (_skipEntry.IntroSeconds > 0 && position >= 1 && position <= _skipEntry.IntroSeconds + 2)
        {
            SeekTo(_skipEntry.IntroSeconds);
            SkipIntroButton.Visibility = Visibility.Collapsed;
            ShowHint("已跳过片头");
        }
        else if (_skipEntry.OutroSeconds > 0 && duration > 0 &&
                 position >= duration - _skipEntry.OutroSeconds - 2 && position < duration - 1)
        {
            SeekTo(Math.Max(0, duration - _skipEntry.OutroSeconds));
            SkipOutroButton.Visibility = Visibility.Collapsed;
            ShowHint("已跳过片尾");
        }
    }

    /* ================= 防休眠 / 防息屏 ================= */

    /// <summary>
    /// 只要「用户想看」就压住休眠：播放中、打开中、缓冲中都算。
    /// 暂停 / 播完 / 出错 / 离开页面时归还，把休眠策略交回系统。
    /// </summary>
    private void SyncKeepAwake()
    {
        var state = _session?.PlaybackState ?? MediaPlaybackState.None;

        var wantAwake = _desiredPlaying
                        && state is MediaPlaybackState.Playing
                            or MediaPlaybackState.Opening
                            or MediaPlaybackState.Buffering;

        if (wantAwake) _keepAwake.Acquire();
        else _keepAwake.Release();
    }

    /* ================= 音量（绑定系统主音量） ================= */

    /// <summary>当前音量百分比：优先读系统主音量，拿不到音频端点时退回播放器自身音量</summary>
    private double VolumePercent =>
        _systemVolume is { IsAvailable: true } sv ? sv.Volume * 100 : (_mediaPlayer?.Volume ?? 1) * 100;

    private bool IsMuted =>
        _systemVolume is { IsAvailable: true } sv ? sv.Muted : (_mediaPlayer?.IsMuted ?? false);

    /// <summary>
    /// 把音量控件接到系统主音量上：滑块 / 拖动 / 静音按钮 / ↑↓ 改的都是系统音量，
    /// 任务栏和键盘音量键改了也会实时反映到界面上。
    /// </summary>
    private void InitVolumeBinding()
    {
        _systemVolume = new SystemVolumeService();

        if (_systemVolume.IsAvailable)
        {
            _systemVolume.Changed += SystemVolume_Changed;

            // 音量交给系统主音量后，播放器自身保持满音量，避免两边各降一半
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = 1;
                _mediaPlayer.IsMuted = false;
            }
        }

        SyncVolumeUi();

        App.Log($"VOLUME: system={_systemVolume.IsAvailable} percent={VolumePercent:0} muted={IsMuted}");
    }

    private void SystemVolume_Changed() => OnUi(SyncVolumeUi);

    /// <summary>把系统音量写回界面（外部改动时调用）</summary>
    private void SyncVolumeUi()
    {
        var percent = Math.Clamp(VolumePercent, 0, 100);

        _applyingVolume = true;
        try
        {
            if (Math.Abs(VolumeSlider.Value - percent) > 0.5) VolumeSlider.Value = percent;
        }
        finally
        {
            _applyingVolume = false;
        }

        UpdateVolumeIcon();
    }

    /// <summary>把音量百分比写到系统（或退回播放器）</summary>
    private void ApplyVolume(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        if (_systemVolume is { IsAvailable: true } sv)
        {
            if (sv.Muted) sv.Muted = false;
            sv.Volume = (float)(percent / 100);
        }
        else if (_mediaPlayer != null)
        {
            _mediaPlayer.IsMuted = false;
            _mediaPlayer.Volume = percent / 100;
        }

        UpdateVolumeIcon();
    }

    private void SetVolumeFromUi(double percent)
    {
        ApplyVolume(percent);

        _applyingVolume = true;
        try
        {
            if (Math.Abs(VolumeSlider.Value - percent) > 0.5) VolumeSlider.Value = Math.Clamp(percent, 0, 100);
        }
        finally
        {
            _applyingVolume = false;
        }
    }

    private void AdjustVolume(double delta)
    {
        var volume = Math.Clamp(VolumePercent + delta * 100, 0, 100);

        SetVolumeFromUi(volume);

        ShowHint($"音量 {volume:0}%");
        ShowControls();
    }

    private void ToggleMute()
    {
        var muted = !IsMuted;

        if (_systemVolume is { IsAvailable: true } sv) sv.Muted = muted;
        else if (_mediaPlayer != null) _mediaPlayer.IsMuted = muted;

        UpdateVolumeIcon();
        ShowHint(muted ? "静音" : "取消静音");
        ShowControls();
    }

    /* ================= 播放器事件 ================= */

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        OnUi(() =>
        {
            var duration = sender.PlaybackSession.NaturalDuration;
            DurationText.Text = FormatTime(duration);

            if (!_pendingSeekApplied && _pendingSeek > 3 && duration.TotalSeconds > 0)
            {
                var target = Math.Min(_pendingSeek, duration.TotalSeconds - 2);
                if (target > 0)
                {
                    sender.PlaybackSession.Position = TimeSpan.FromSeconds(target);
                    _resumedFrom = target;
                    ShowHint($"已从 {FormatTime(TimeSpan.FromSeconds(target))} 继续播放");
                }
                _pendingSeekApplied = true;
            }

            _pendingSeek = 0;
            SetBuffering(false);
            CaptureTrackProperties();
            UpdatePlayStateUi();

            // 打开 / 跳转之后媒体管道有时会停在 Paused，这里按用户意图恢复一次
            if (_desiredPlaying && sender.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                sender.Play();
            }

            if (InfoPanel.Visibility == Visibility.Visible) RefreshVideoInfo();
        });
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        OnUi(() =>
        {
            SaveProgress(force: true);

            if (_args != null && _index + 1 < _args.Episodes.Count) StartNextCountdown();
            else SubtitleText.Text = "已播放完最后一集";
        });
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        OnUi(() =>
        {
            SetBuffering(false);
            var url = _args != null && _index < _args.Episodes.Count ? _args.Episodes[_index].Url : string.Empty;
            ShowError($"{args.ErrorMessage}（{args.Error}）\n地址：{url}");
        });
    }

    private void Session_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        OnUi(() =>
        {
            UpdatePlayStateUi();

            // 播放 → 压住休眠；暂停 / 播完 → 交还
            SyncKeepAwake();

            if (sender.PlaybackState == MediaPlaybackState.Playing)
            {
                if (_controlsVisible) _hideTimer.Start();
            }
            else
            {
                _hideTimer.Stop();
            }
        });
    }

    private void Session_BufferingStarted(MediaPlaybackSession sender, object args) => OnUi(() => SetBuffering(true));

    private void Session_BufferingEnded(MediaPlaybackSession sender, object args) => OnUi(() => SetBuffering(false));

    /* ================= 轨道信息 ================= */

    private void PlaybackItem_VideoTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
    {
        OnUi(() =>
        {
            try
            {
                if (sender.VideoTracks.Count > 0)
                {
                    _videoProps = sender.VideoTracks[0].GetEncodingProperties();
                }
            }
            catch
            {
                // 自适应流可能不给静态轨道参数，面板会退回 NaturalVideoWidth/Height
            }

            if (InfoPanel.Visibility == Visibility.Visible) RefreshVideoInfo();
        });
    }

    private void PlaybackItem_AudioTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
    {
        OnUi(() =>
        {
            try
            {
                if (sender.AudioTracks.Count > 0)
                {
                    _audioProps = sender.AudioTracks[0].GetEncodingProperties();
                }
            }
            catch
            {
                // 同上
            }

            if (InfoPanel.Visibility == Visibility.Visible) RefreshVideoInfo();
        });
    }

    /// <summary>媒体管道打开后补齐一次轨道参数（HLS 的轨道信息往往是异步到达的）</summary>
    private void CaptureTrackProperties()
    {
        try
        {
            if (_playbackItem != null)
            {
                if (_videoProps == null && _playbackItem.VideoTracks.Count > 0)
                {
                    _videoProps = _playbackItem.VideoTracks[0].GetEncodingProperties();
                }

                if (_audioProps == null && _playbackItem.AudioTracks.Count > 0)
                {
                    _audioProps = _playbackItem.AudioTracks[0].GetEncodingProperties();
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    /* ================= 每秒刷新 ================= */

    private void TickTimer_Tick(object? sender, object e)
    {
        if (_session == null) return;

        var duration = _session.NaturalDuration;
        var position = _session.Position;

        if (!_isScrubbing)
        {
            _updatingSlider = true;
            if (duration.TotalSeconds > 0) ProgressSlider.Maximum = duration.TotalSeconds;
            ProgressSlider.Value = Math.Clamp(position.TotalSeconds, 0, ProgressSlider.Maximum);
            _updatingSlider = false;

            TimeText.Text = FormatTime(position);
        }

        if (duration.TotalSeconds > 0) DurationText.Text = FormatTime(duration);

        // 检测跳过片头 / 片尾
        if (_skipEntry != null && _session.PlaybackState == MediaPlaybackState.Playing)
        {
            CheckSkipIntro(position.TotalSeconds, duration.TotalSeconds);
            CheckSkipOutro(position.TotalSeconds, duration.TotalSeconds);
        }

        // 刷新字幕
        UpdateSubtitleDisplay(position.TotalSeconds);

        if (_session.PlaybackState == MediaPlaybackState.Playing &&
            (DateTime.Now - _lastSaveAt).TotalSeconds >= 5)
        {
            SaveProgress();
        }

        // 信息面板打开时每 2 秒刷新一次运行时数据（缓冲 / 进度），避免每秒重建
        if (InfoPanel.Visibility == Visibility.Visible && ++_infoTick % 2 == 0)
        {
            RefreshVideoInfo();
        }

        // 打开超时保护：源站被墙 / 分片 404 时 MF 会一直停在 Opening
        if (!_openTimeoutReported &&
            _session.PlaybackState == MediaPlaybackState.Opening &&
            _openStartedAt != DateTime.MinValue &&
            (DateTime.Now - _openStartedAt).TotalSeconds > 25)
        {
            _openTimeoutReported = true;
            var url = _args != null && _index < _args.Episodes.Count ? _args.Episodes[_index].Url : string.Empty;
            ShowError($"打开超时：25 秒内没有取到可播放的媒体。\n可能是该线路被网络阻断或分片失效，试试切换线路 / 下一集。\n地址：{url}");
        }
    }

    private void SaveProgress(bool force = false)
    {
        if (_args == null || _session == null) return;

        var position = _session.Position.TotalSeconds;
        var duration = _session.NaturalDuration.TotalSeconds;
        if (position < 1) return;

        var episode = _args.Episodes[_index];
        LibraryStore.Current.RecordProgress(
            _args.VodId, _args.VodName, _args.PosterUrl,
            episode.Index, episode.Name, position, duration, force);

        _lastSaveAt = DateTime.Now;
    }

    /* ================= 自动连播 ================= */

    private void StartNextCountdown()
    {
        if (_args == null || _index + 1 >= _args.Episodes.Count) return;

        _nextCountdown = 5;
        var next = _args.Episodes[_index + 1];
        NextToastText.Text = $"即将播放「{next.Name}」（{_index + 2}/{_args.Episodes.Count}）· {_nextCountdown} 秒后开始";
        NextToast.Visibility = Visibility.Visible;
        _nextTimer.Start();
    }

    private void NextTimer_Tick(object? sender, object e)
    {
        if (_args == null)
        {
            CancelNextCountdown();
            return;
        }

        _nextCountdown--;
        var next = _index + 1 < _args.Episodes.Count ? _args.Episodes[_index + 1] : null;

        if (_nextCountdown <= 0 || next == null)
        {
            CancelNextCountdown();
            PlayNext();
            return;
        }

        NextToastText.Text = $"即将播放「{next.Name}」（{_index + 2}/{_args.Episodes.Count}）· {_nextCountdown} 秒后开始";
    }

    private void CancelNextCountdown()
    {
        _nextTimer.Stop();
        NextToast.Visibility = Visibility.Collapsed;
    }

    /* ================= 手势 ================= */

    private void Gesture_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragMoved = false;
        _dragIsSeek = false;
        _dragOrigin = e.GetCurrentPoint(GestureLayer).Position;

        _dragBaseVolume = VolumePercent / 100.0;
        _dragBaseBrightness = BrightnessOverlay.Opacity;
        _dragStartSeconds = _session?.Position.TotalSeconds ?? 0;
        _dragTargetSeconds = _dragStartSeconds;

        GestureLayer.CapturePointer(e.Pointer);
        ShowControls();
    }

    private void Gesture_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            ShowControls();
            return;
        }

        var point = e.GetCurrentPoint(GestureLayer).Position;
        var dx = point.X - _dragOrigin.X;
        var dy = point.Y - _dragOrigin.Y;

        if (!_dragMoved && Math.Abs(dx) < 9 && Math.Abs(dy) < 9) return;
        _dragMoved = true;

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            // 水平：快进快退。整屏宽度约对应 90 秒，长片也不会太灵敏
            _dragIsSeek = true;

            var duration = _session?.NaturalDuration.TotalSeconds ?? 0;
            var span = Math.Max(360, GestureLayer.ActualWidth);
            var target = _dragStartSeconds + dx * (90.0 / span);
            _dragTargetSeconds = Math.Clamp(target, 0, Math.Max(0, duration - 1));

            var delta = _dragTargetSeconds - _dragStartSeconds;
            ShowHint($"{(delta >= 0 ? "快进" : "快退")} {FormatTime(TimeSpan.FromSeconds(Math.Abs(delta)))} → {FormatTime(TimeSpan.FromSeconds(_dragTargetSeconds))}");
        }
        else if (point.X < GestureLayer.ActualWidth / 2)
        {
            // 左半屏：亮度（半透明遮罩模拟）
            var brightness = Math.Clamp(_dragBaseBrightness - dy / 420.0, 0, 0.85);
            BrightnessOverlay.Opacity = brightness;
            ShowHint($"亮度 {(1 - brightness) * 100:0}%");
        }
        else
        {
            // 右半屏：音量
            var volume = Math.Clamp(_dragBaseVolume - dy / 320.0, 0, 1);
            SetVolumeFromUi(volume * 100);
            ShowHint($"音量 {volume * 100:0}%");
        }
    }

    private void Gesture_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        GestureLayer.ReleasePointerCapture(e.Pointer);

        if (_dragIsSeek && _dragMoved) SeekTo(_dragTargetSeconds);
        else if (!_dragMoved)
        {
            // 单击：切换控制栏显示
            if (_controlsVisible) HideControls();
            else ShowControls();
        }

        _dragIsSeek = false;
        HideHintSoon();
    }

    private void Gesture_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        if (_dragIsSeek && _dragMoved) SeekTo(_dragTargetSeconds);
        _dragIsSeek = false;
        HideHintSoon();
    }

    private void GestureLayer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleFullScreen();
        ShowControls();
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) ShowControls();
    }

    /* ================= 控制栏显隐 ================= */

    private void ShowControls()
    {
        _controlsVisible = true;
        TopBar.Opacity = 1;
        BottomBar.Opacity = 1;
        TopBar.IsHitTestVisible = true;
        BottomBar.IsHitTestVisible = true;
        UpdatePlayStateUi();

        _hideTimer.Stop();
        if (_session?.PlaybackState == MediaPlaybackState.Playing &&
            NextToast.Visibility == Visibility.Collapsed)
        {
            _hideTimer.Start();
        }
    }

    private void HideControls()
    {
        _hideTimer.Stop();

        // 暂停时不隐藏，避免用户找不到播放按钮
        if (_session?.PlaybackState != MediaPlaybackState.Playing) return;

        _controlsVisible = false;
        TopBar.Opacity = 0;
        BottomBar.Opacity = 0;
        TopBar.IsHitTestVisible = false;
        BottomBar.IsHitTestVisible = false;
        UpdatePlayStateUi();
    }

    private void UpdatePlayStateUi()
    {
        bool playing = _session?.PlaybackState == MediaPlaybackState.Playing;
        bool paused = !playing && _session?.PlaybackState != MediaPlaybackState.Opening;

        BottomPlayIcon.Glyph = playing ? "\uE769" : "\uE768";
        CenterPlayIcon.Glyph = playing ? "\uE769" : "\uE768";

        // 暂停时露出中央播放按钮，播放中隐藏
        CenterPlayButton.Opacity = paused && _controlsVisible ? 1 : 0;
        CenterPlayButton.IsHitTestVisible = paused && _controlsVisible;
    }

    private void SetBuffering(bool active)
    {
        BufferingRing.IsActive = active;
        BufferingRing.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowHint(string text)
    {
        GestureHintText.Text = text;
        GestureHint.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void HideHintSoon()
    {
        if (GestureHint.Visibility != Visibility.Visible) return;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    /* ================= 视频信息面板 ================= */

    private void ToggleInfo_Click(object sender, RoutedEventArgs e) => ToggleInfoPanel();

    private void CloseInfoPanel_Click(object sender, RoutedEventArgs e) => InfoPanel.Visibility = Visibility.Collapsed;

    private void RefreshInfo_Click(object sender, RoutedEventArgs e) => RefreshVideoInfo();

    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_infoText);
            Clipboard.SetContent(package);
            ShowHint("视频信息已复制");
        }
        catch
        {
            ShowHint("复制失败");
        }
    }

    private void Info_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleInfoPanel();
        args.Handled = true;
    }

    private void ToggleInfoPanel()
    {
        var show = InfoPanel.Visibility != Visibility.Visible;
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            EpisodePanel.Visibility = Visibility.Collapsed;
            CaptureTrackProperties();
            RefreshVideoInfo();
            _ = LoadPlaylistInfoAsync();
        }

        ShowControls();
    }

    /// <summary>补一次 m3u8 主播放列表信息（拿到帧率 / 声明码率，MF 对自适应流常常不提供）</summary>
    private Task LoadPlaylistInfoAsync()
    {
        if (_args == null || _index >= _args.Episodes.Count) return Task.CompletedTask;

        var url = _args.Episodes[_index].Url;

        // 同一集重复调用时复用同一个探测任务，避免并发与「返回时数据还没到」
        if (_playlistInfoUrl == url) return _playlistTask ?? Task.CompletedTask;

        _playlistInfoUrl = url;
        _playlistTask = LoadPlaylistInfoCoreAsync(url);
        return _playlistTask;
    }

    private async Task LoadPlaylistInfoCoreAsync(string url)
    {
        var info = await HlsPlaylist.ProbeAsync(url);

        if (_playlistInfoUrl != url) return;

        _playlistInfo = info;

        if (InfoPanel.Visibility == Visibility.Visible) RefreshVideoInfo();
    }

    /// <summary>重建「视频信息」面板：解码参数来自轨道编码属性，运行时数据来自 PlaybackSession</summary>
    private void RefreshVideoInfo()
    {
        if (_session == null) return;

        _infoLines.Clear();
        InfoRows.Children.Clear();

        var url = _args != null && _index < _args.Episodes.Count ? _args.Episodes[_index].Url : string.Empty;

        AddSafeRow("分辨率", ResolutionText);
        AddSafeRow("视频编码", () => CodecName(_videoProps?.Subtype));
        AddSafeRow("帧率", FrameRateText);
        AddSafeRow("视频码率", VideoBitrateText);
        AddSafeRow("音频编码", () => CodecName(_audioProps?.Subtype));
        AddSafeRow("采样 / 声道", AudioLayoutText);
        AddSafeRow("音频码率", () => BitrateText(_audioProps?.Bitrate));
        AddSafeRow("传输方式", () => TransportText(url));
        AddSafeRow("轨道数", () => $"视频 {_playbackItem?.VideoTracks.Count ?? 0} · 音频 {_playbackItem?.AudioTracks.Count ?? 0}");
        AddSafeRow("时长", () => _session!.NaturalDuration > TimeSpan.Zero ? FormatTime(_session.NaturalDuration) : "未知");
        AddSafeRow("当前进度", () => $"{FormatTime(_session!.Position)} / {FormatTime(_session.NaturalDuration)}");
        AddSafeRow("预缓冲", BufferAheadText);        AddSafeRow("已缓冲区间", BufferedRangesText);
        AddSafeRow("播放状态", () => PlayStateText(SafePlaybackState()));
        AddSafeRow("播放倍速", () => $"{_session!.PlaybackRate:0.##}x");
        AddSafeRow("可跳转", () => SafeCanSeek() ? "支持" : "不支持");
        AddSafeRow("DRM 加密", () => SafeIsProtected() ? "是（受保护内容）" : "否");
        AddSafeRow("解码链路", () => "Media Foundation（系统自动选择硬件 / 软件解码）");
        AddSafeRow("播放地址", () => string.IsNullOrWhiteSpace(url) ? "—" : url);

        _infoText = string.Join("\n", _infoLines.ConvertAll(line => $"{line.Label}: {line.Value}"));
    }

    /* ---------- 面板单行读取（个别属性在 Opening 阶段会抛 COMException，不能让整块面板失效） ---------- */

    private void AddSafeRow(string label, Func<string> factory)
    {
        string value;
        try
        {
            value = factory();
        }
        catch (Exception ex)
        {
            value = $"读取失败（{ex.GetType().Name}）";
        }

        AddInfoRow(label, value);
    }

    private string ResolutionText()
    {
        var width = _videoProps?.Width ?? _session?.NaturalVideoWidth ?? 0;
        var height = _videoProps?.Height ?? _session?.NaturalVideoHeight ?? 0;

        if (width > 0 && height > 0) return $"{width} × {height}（{QualityLabel(height)}）";
        if (!string.IsNullOrWhiteSpace(_playlistInfo?.Resolution)) return $"{_playlistInfo.Resolution}（播放列表声明）";

        return "未知";
    }

    private string FrameRateText()
    {
        var rate = _videoProps?.FrameRate;
        if (rate is { Denominator: > 0, Numerator: > 0 })
        {
            return $"{(double)rate.Numerator / rate.Denominator:0.###} fps";
        }

        if (!string.IsNullOrWhiteSpace(_playlistInfo?.FrameRate)) return $"{_playlistInfo.FrameRate} fps（声明）";

        return "未知";
    }

    private string VideoBitrateText()
    {
        if (_videoProps?.Bitrate is > 0) return BitrateText(_videoProps.Bitrate);
        if (_playlistInfo?.Bandwidth is > 0) return $"{_playlistInfo.Bandwidth / 1_000_000.0:0.##} Mbps（播放列表声明）";

        return "未知";
    }

    private MediaPlaybackState SafePlaybackState()
    {
        try
        {
            return _session?.PlaybackState ?? MediaPlaybackState.None;
        }
        catch
        {
            return MediaPlaybackState.None;
        }
    }

    /// <summary>打开过程中 IsProtected 会抛 COMException，这里吞掉</summary>
    private bool SafeIsProtected()
    {
        try
        {
            return _session?.IsProtected == true;
        }
        catch
        {
            return false;
        }
    }

    private bool SafeCanSeek()
    {
        try
        {
            return _session?.CanSeek == true;
        }
        catch
        {
            return false;
        }
    }

    private void AddInfoRow(string label, string value)
    {
        _infoLines.Add((label, value));

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(102) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.55 }
        };
        Grid.SetColumn(labelText, 0);

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        Grid.SetColumn(valueText, 1);

        row.Children.Add(labelText);
        row.Children.Add(valueText);
        InfoRows.Children.Add(row);
    }

    private static string QualityLabel(uint height) => height switch
    {
        >= 2000 => "4K",
        >= 1000 => "1080p",
        >= 700 => "720p",
        >= 400 => "480p",
        > 0 => $"{height}p",
        _ => "—"
    };

    private static string BitrateText(uint? bitrate) =>
        bitrate is > 0 ? $"{bitrate.Value / 1_000_000.0:0.##} Mbps（{bitrate.Value / 1000} kbps）" : "未知";

    private string AudioLayoutText()
    {
        if (_audioProps == null) return "未知";

        var channels = _audioProps.ChannelCount;
        var layout = channels switch
        {
            1 => "单声道",
            2 => "立体声",
            6 => "5.1 声道",
            8 => "7.1 声道",
            _ => $"{channels} 声道"
        };

        return $"{_audioProps.SampleRate / 1000.0:0.#} kHz · {layout}";
    }

    private string BufferedRangesText()
    {
        if (_session == null) return "—";

        try
        {
            var ranges = _session.GetBufferedRanges();
            if (ranges == null || ranges.Count == 0) return "—";

            var first = ranges[0];
            var last = ranges[ranges.Count - 1];
            return $"{FormatTime(first.Start)} – {FormatTime(last.End)}（{ranges.Count} 段）";
        }
        catch
        {
            return "—";
        }
    }

    /// <summary>当前位置前方已缓冲的时长（BufferingProgress 在部分管道上会抛异常，改用缓冲区间推算）</summary>
    private string BufferAheadText()
    {
        if (_session == null) return "—";

        try
        {
            var ranges = _session.GetBufferedRanges();
            if (ranges == null || ranges.Count == 0) return "—";

            var position = _session.Position;
            foreach (var range in ranges)
            {
                if (position >= range.Start && position <= range.End)
                {
                    return $"前方 {FormatTime(range.End - position)}";
                }
            }

            return "已跳出缓冲区";
        }
        catch
        {
            return "—";
        }
    }

    private static string PlayStateText(MediaPlaybackState state) => state switch
    {
        MediaPlaybackState.Playing => "正在播放",
        MediaPlaybackState.Paused => "已暂停",
        MediaPlaybackState.Opening => "正在打开",
        MediaPlaybackState.Buffering => "缓冲中",
        MediaPlaybackState.None => "空闲",
        _ => state.ToString()
    };

    private static string TransportText(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "—";

        var lower = url.ToLowerInvariant();
        if (lower.Contains(".m3u8")) return "HLS（m3u8 切片流）";
        if (lower.Contains(".mpd")) return "MPEG-DASH";
        if (lower.Contains(".mp4")) return "MP4（渐进式下载）";
        if (lower.Contains(".flv")) return "FLV";
        if (lower.Contains(".mkv")) return "MKV";
        if (lower.Contains(".ts")) return "MPEG-TS";
        return "直链";
    }

    private static string CodecName(string? subtype)
    {
        if (string.IsNullOrWhiteSpace(subtype)) return "未知";

        var key = subtype.Trim().ToUpperInvariant();
        return key switch
        {
            "H264" or "AVC1" => "H.264 / AVC",
            "HEVC" or "H265" or "HEV1" or "HVC1" => "H.265 / HEVC",
            "AV01" => "AV1",
            "VP90" or "VP9" => "VP9",
            "VP80" => "VP8",
            "MP4V" => "MPEG-4 Part 2",
            "WVC1" => "VC-1",
            "AAC" or "MP4A" => "AAC",
            "MP3" or "MPG3" => "MP3",
            "AC3" => "Dolby AC-3",
            "EAC3" => "Dolby E-AC-3",
            "OPUS" => "Opus",
            "FLAC" => "FLAC",
            "PCM" => "PCM",
            "VORBIS" => "Vorbis",
            _ => subtype
        };
    }

    /* ================= 全屏 / 画中画 ================= */

    private void ToggleFullScreen()
    {
        var window = App.MainWindow;
        if (window == null) return;

        try
        {
            if (_isFullScreen)
            {
                window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isFullScreen = false;
                FullScreenIcon.Glyph = "\uE740";
            }
            else
            {
                if (_isPip)
                {
                    window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    _isPip = false;
                }

                window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;
                FullScreenIcon.Glyph = "\uE73F";
            }
        }
        catch
        {
            // 某些窗口状态（如已被系统接管）可能失败，静默忽略
        }
        finally
        {
            // 全屏时把外壳侧边栏一起收掉，画面才是真正的满屏
            ApplyShellChrome();
        }
    }

    /// <summary>全屏 / 画中画时收起外壳（侧边栏 + 页头），回到窗口时还原</summary>
    private void ApplyShellChrome() => FindShell()?.SetChromeVisible(!_isFullScreen && !_isPip);

    private ShellPage? _shellCache;

    /// <summary>沿视觉树往上找外壳页；播放页被直接导航到根 Frame 时返回 null</summary>
    private ShellPage? FindShell()
    {
        if (_shellCache != null) return _shellCache;

        DependencyObject? node = this;
        while (node != null)
        {
            if (node is ShellPage shell)
            {
                _shellCache = shell;
                return shell;
            }

            node = node is FrameworkElement fe
                ? fe.Parent ?? VisualTreeHelper.GetParent(fe)
                : VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void TogglePip()
    {
        var window = App.MainWindow;
        if (window == null) return;

        try
        {
            if (_isPip)
            {
                window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isPip = false;
                return;
            }

            if (_isFullScreen)
            {
                window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isFullScreen = false;
                FullScreenIcon.Glyph = "\uE740";
            }

            window.AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
            _isPip = true;
        }
        catch
        {
            // 忽略
        }
        finally
        {
            ApplyShellChrome();
        }
    }

    private void RestorePresenter()
    {
        var window = App.MainWindow;
        if (window == null) return;

        if (!_isFullScreen && !_isPip) return;

        try
        {
            window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        catch
        {
            // 忽略
        }

        _isFullScreen = false;
        _isPip = false;

        ApplyShellChrome();
    }

    /* ================= 控件事件 ================= */

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Prev_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void Pip_Click(object sender, RoutedEventArgs e)
    {
        TogglePip();
        ShowControls();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
        ShowControls();
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;
        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rate)) return;

        if (_session != null) _session.PlaybackRate = rate;
        SpeedText.Text = $"{rate:0.##}x";
        ShowHint($"播放速度 {rate:0.##}x");
    }

    private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _mediaPlayer == null) return;

        if (_isScrubbing)
        {
            // 拖动中只更新时间预览，松手后才真正跳转
            TimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            return;
        }

        SeekTo(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 外部音量回写界面时不再反向写一次，避免来回抖动
        if (_applyingVolume) return;

        if (Math.Abs(e.NewValue - VolumePercent) < 0.5)
        {
            UpdateVolumeIcon();
            return;
        }

        ApplyVolume(e.NewValue);
    }

    private void UpdateVolumeIcon()
    {
        var muted = IsMuted || VolumePercent <= 0.5;
        VolumeIcon.Glyph = muted ? "\uE74F" : "\uE767";
    }

    private void ToggleEpisodePanel_Click(object sender, RoutedEventArgs e)
    {
        bool show = EpisodePanel.Visibility != Visibility.Visible;
        EpisodePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            SelectEpisodeInPanel();
        }
    }

    private void CloseEpisodePanel_Click(object sender, RoutedEventArgs e)
    {
        EpisodePanel.Visibility = Visibility.Collapsed;
    }

    private void EpisodeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_args == null || e.ClickedItem is not VodEpisode episode) return;

        var index = _args.Episodes.IndexOf(episode);
        if (index < 0 || index == _index) return;

        _index = index;
        _pendingSeek = 0;
        PlayCurrent();
        EpisodePanel.Visibility = Visibility.Collapsed;
    }

    private void SelectEpisodeInPanel()
    {
        if (_args == null) return;

        EpisodeList.SelectedIndex = _index;
        if (_index >= 0 && _index < _args.Episodes.Count)
        {
            EpisodeList.ScrollIntoView(_args.Episodes[_index]);
        }
    }

    private void NextNow_Click(object sender, RoutedEventArgs e)
    {
        CancelNextCountdown();
        PlayNext();
    }

    private void CancelNext_Click(object sender, RoutedEventArgs e) => CancelNextCountdown();

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _pendingSeek = 0;
        PlayCurrent();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SaveProgress(force: true);
        RestorePresenter();

        if (Frame.CanGoBack) Frame.GoBack();
    }

    /* ================= 快捷键 ================= */

    private void Space_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        TogglePlayPause();
    }

    private void SeekBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _keysHandled++;
        SeekBy(-5);
        args.Handled = true;
    }

    private void SeekForward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _keysHandled++;
        SeekBy(5);
        args.Handled = true;
    }

    private void VolumeUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        AdjustVolume(0.05);
        args.Handled = true;
    }

    private void VolumeDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        AdjustVolume(-0.05);
        args.Handled = true;
    }

    private void Mute_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleMute();
        args.Handled = true;
    }

    private void FullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleFullScreen();
        args.Handled = true;
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HandleEscape();
    }

    /// <summary>
    /// Esc 的逐级退出：先关信息面板，再关选集抽屉，再退全屏 / 画中画，最后返回上一页。
    /// </summary>
    private void HandleEscape()
    {
        if (InfoPanel.Visibility == Visibility.Visible)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (EpisodePanel.Visibility == Visibility.Visible)
        {
            EpisodePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_isFullScreen || _isPip)
        {
            RestorePresenter();
            FullScreenIcon.Glyph = "\uE740";
            ShowControls();
            return;
        }

        Back_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// 播放器的全部快捷键都在页面根部拦截。
    ///
    /// 用 <c>PreviewKeyDown</c>（隧道事件，从根往下走）而不是只在 Page 上挂
    /// <see cref="KeyboardAccelerator"/>：加速键只有在焦点元素**没处理**该按键时才会被调用，
    /// 所以焦点一旦停在进度条 / 音量条 / 选集列表上，← → 就被它们吃掉——
    /// 要么没快进快退，要么顺手把音量、进度改了一格，甚至把焦点挪到别的控件上。
    /// 现在在根部先拦下来并标 Handled，这些控件压根收不到方向键，只会快进 / 快退。
    /// </summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 万一将来页面里出现输入框，打字时不要劫持按键
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;

        if (!HandleShortcut(e.Key)) return;

        _keysHandled++;
        e.Handled = true;
    }

    /// <summary>按键 → 动作。返回 false 表示这个键不归播放器管，交给系统。</summary>
    private bool HandleShortcut(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Space:
                TogglePlayPause();
                break;
            case VirtualKey.Left:
                SeekBy(-5);
                break;
            case VirtualKey.Right:
                SeekBy(5);
                break;
            case VirtualKey.Up:
                AdjustVolume(0.05);
                break;
            case VirtualKey.Down:
                AdjustVolume(-0.05);
                break;
            case VirtualKey.F:
                ToggleFullScreen();
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            case VirtualKey.I:
                ToggleInfoPanel();
                break;
            case VirtualKey.C:
                ToggleSubtitle();
                break;
            case VirtualKey.J:
                SkipByShortcut();
                break;
            case VirtualKey.Escape:
                HandleEscape();
                break;
            default:
                return false;
        }

        return true;
    }

    /* ================= 辅助 ================= */

    /// <summary>诊断用：切一次全屏（HY_FS=1 时由自检调用）</summary>
    internal void DebugToggleFullScreen() => ToggleFullScreen();

    /// <summary>诊断用：把音量设到指定百分比并回读，验证是否真的写进了系统音量</summary>
    internal string DebugSetVolume(int percent)
    {
        var before = VolumePercent;
        SetVolumeFromUi(percent);
        var after = VolumePercent;
        var muted = IsMuted;

        SetVolumeFromUi((int)Math.Round(before));
        return $"target={percent}% before={before:0}% after={after:0}% muted={muted}";
    }

    /// <summary>诊断用：全屏 / 外壳 / 音量状态</summary>
    internal string DebugChromeState() =>
        $"fullscreen={_isFullScreen} pip={_isPip} shellChromeHidden={FindShell()?.IsChromeHidden} " +
        $"volume={VolumePercent:0}% systemVolume={_systemVolume?.IsAvailable == true} keepAwake={_keepAwake.IsActive}";

    /// <summary>诊断用：手动申请 / 归还常亮，返回状态（HY_AWAKE=1 自检）</summary>
    internal string DebugKeepAwake(bool on)
    {
        if (on) _keepAwake.Acquire();
        else _keepAwake.Release();

        return $"keepAwake={_keepAwake.IsActive} desiredPlaying={_desiredPlaying} " +
               $"state={_session?.PlaybackState.ToString() ?? "no-session"}";
    }

    /// <summary>诊断用：直接走一遍快捷键映射（HY_KEYTEST=1 自检，不依赖真实键盘焦点）</summary>
    internal string DebugShortcuts()
    {
        var before = VolumePercent;
        var parts = new List<string>();

        foreach (var key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down, VirtualKey.Space, VirtualKey.Q })
        {
            parts.Add($"{key}={HandleShortcut(key)}");
        }

        return $"{string.Join(" ", parts)} volumeBefore={before:0}% volumeAfter={VolumePercent:0}% " +
               $"state={_session?.PlaybackState.ToString() ?? "no-session"} desiredPlaying={_desiredPlaying} " +
               $"focus={FocusManager.GetFocusedElement(XamlRoot)?.GetType().Name ?? "-"}";
    }

    /// <summary>诊断用：供 HY_NAV 自检读取当前播放状态</summary>
    internal string DebugState()
    {
        var state = _session?.PlaybackState.ToString() ?? "no-session";
        var position = _session?.Position.ToString() ?? "-";
        var duration = _session?.NaturalDuration.ToString() ?? "-";
        var error = ErrorPanel.Visibility == Visibility.Visible ? ErrorText.Text : "none";

        return $"vod={_args?.VodId ?? 0} state={state} pos={position} dur={duration} index={_index + 1}/{_args?.Episodes.Count ?? 0} " +
               $"resumedFrom={_resumedFrom:0}s video={_videoProps?.Width ?? 0}x{_videoProps?.Height ?? 0} " +
               $"codec={_videoProps?.Subtype ?? "-"} audio={_audioProps?.Subtype ?? "-"} err={error} " +
               $"keys={_keysHandled} panel={EpisodePanel.Visibility} keepAwake={_keepAwake.IsActive} " +
               $"focus={FocusManager.GetFocusedElement(XamlRoot)?.GetType().Name ?? "-"}";
    }

    /// <summary>诊断用：打开「视频信息」面板并返回面板文本（供 HY_NAV 自检）</summary>
    internal async Task<string> DebugInfoTextAsync()
    {
        if (InfoPanel.Visibility != Visibility.Visible) ToggleInfoPanel();

        await LoadPlaylistInfoAsync();
        RefreshVideoInfo();

        return _infoText.Replace("\n", " | ") +
               $" || playlist={(HlsPlaylist.LastError.Length > 0 ? HlsPlaylist.LastError : "ok")}";
    }

    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        SetBuffering(false);
    }

    private void HideError()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}
