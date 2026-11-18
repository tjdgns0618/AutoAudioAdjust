using System.Drawing;
using System.Windows.Forms;

namespace AutoAudioAdjust.App.Services;

/// <summary>
/// Notification-area icon. Left click opens the mini player, double click opens the main window,
/// right click shows a dark menu with playback controls and app options.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _nowPlayingItem;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _autoAdjustItem;
    private readonly ToolStripMenuItem _askOnCloseItem;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private Point _lastClickPoint;

    public TrayIcon(Icon icon)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f)
        };

        _nowPlayingItem = new ToolStripMenuItem("재생 중인 곡 없음") { Enabled = false };
        _playPauseItem = new ToolStripMenuItem("재생", null, (_, _) => PlayPauseRequested?.Invoke());
        _autoAdjustItem = new ToolStripMenuItem("자동 보정", null, (_, _) => AutoAdjustToggled?.Invoke()) { CheckOnClick = false };
        _askOnCloseItem = new ToolStripMenuItem("X 버튼을 누르면 물어보기", null, (_, _) => AskOnCloseToggled?.Invoke());

        menu.Items.AddRange(
        [
            _nowPlayingItem,
            new ToolStripSeparator(),
            _playPauseItem,
            new ToolStripMenuItem("이전 곡", null, (_, _) => PreviousRequested?.Invoke()),
            new ToolStripMenuItem("다음 곡", null, (_, _) => NextRequested?.Invoke()),
            new ToolStripSeparator(),
            _autoAdjustItem,
            _askOnCloseItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("창 열기", null, (_, _) => OpenRequested?.Invoke()),
            new ToolStripMenuItem("종료", null, (_, _) => ExitRequested?.Invoke())
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "AAA · Auto Audio Adjust",
            ContextMenuStrip = menu,
            Visible = true
        };

        // Delay single clicks so a double click opens the window without flashing the mini player.
        _singleClickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            MiniPlayerRequested?.Invoke(_lastClickPoint);
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _lastClickPoint = Cursor.Position;
                _singleClickTimer.Start();
            }
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _singleClickTimer.Stop();
                OpenRequested?.Invoke();
            }
        };
    }

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? AutoAdjustToggled;
    public event Action? AskOnCloseToggled;

    /// <summary>Raised on a single left click, with the cursor position in screen pixels.</summary>
    public event Action<Point>? MiniPlayerRequested;

    public void Update(NowPlaying nowPlaying, bool autoAdjustOn, bool askOnClose)
    {
        var song = nowPlaying.HasTrack
            ? string.IsNullOrWhiteSpace(nowPlaying.Artist) ? nowPlaying.Title : $"{nowPlaying.Title} - {nowPlaying.Artist}"
            : "";
        _nowPlayingItem.Text = nowPlaying.HasTrack ? Shorten(song, 48) : "재생 중인 곡 없음";
        _playPauseItem.Text = nowPlaying.IsPlaying ? "일시정지" : "재생";
        _autoAdjustItem.Checked = autoAdjustOn;
        _askOnCloseItem.Checked = askOnClose;

        // NotifyIcon tooltips are limited to 127 characters.
        var tip = nowPlaying.HasTrack
            ? $"AAA · {(nowPlaying.IsPlaying ? "▶" : "❚❚")} {song}"
            : "AAA · Auto Audio Adjust";
        _notifyIcon.Text = Shorten(tip, 120);
    }

    public void ShowBalloon(string title, string text) =>
        _notifyIcon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _singleClickTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Dark menu colours matching the app (navy background, mint highlight).</summary>
    private sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(0xED, 0xF5, 0xFA) : Color.FromArgb(0x91, 0xA3, 0xB8);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Color.FromArgb(0x35, 0xD6, 0xB1), 2f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen,
            [
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
            ]);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(0x10, 0x1D, 0x2E);
        private static readonly Color Hover = Color.FromArgb(0x1A, 0x3A, 0x3A);
        private static readonly Color Border = Color.FromArgb(0x2A, 0x46, 0x64);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
