// Ya Mini Player - a tiny always-on-top remote for Yandex Music.
// Talks to the player through Windows' System Media Transport Controls, so it
// needs no login and no Yandex API: whatever Yandex Music reports to Windows
// (title, artist, cover) is shown here, and the buttons send media commands back.
//
// Written against C# 5 so it builds with the csc.exe that ships with Windows.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;
using Rect = System.Windows.Rect;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using StreamRef = Windows.Storage.Streams.IRandomAccessStreamReference;

[assembly: AssemblyTitle("Ya Mini Player")]
[assembly: AssemblyProduct("Ya Mini Player")]
[assembly: AssemblyDescription("Tiny floating remote for Yandex Music")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace YaMini
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (new Mutex(true, "YaMiniPlayer.SingleInstance", out first))
            {
                if (!first) return;
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.Run(new Player().Window);
            }
        }
    }

    // The stock csc.exe cannot see the framework's GetAwaiter for WinRT operations, so bridge to Task by hand
    static class Async
    {
        public static Task<T> AsTask<T>(IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = delegate(IAsyncOperation<T> o, AsyncStatus status)
            {
                if (status == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else if (status == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                else tcs.TrySetCanceled();
            };
            return tcs.Task;
        }
    }

    class Player
    {
        const string PlayGeo = "M3,1 L13,7 L3,13 Z";
        const string PauseGeo = "M2,1 H6 V13 H2 Z M8,1 H12 V13 H8 Z";

        public readonly Window Window;

        readonly FrameworkElement normalView, compactView;
        readonly Border art, artC;
        readonly UIElement artNote, artNoteC;
        readonly System.Windows.Shapes.Rectangle backdrop;
        readonly TextBlock titleText, artistText, lineC;
        readonly Run titleC, artistC;
        readonly System.Windows.Shapes.Path playIcon, playIconC, pinIcon, pinIconC;
        readonly Button pinBtn, pinBtnC;
        readonly MenuItem compactItem, topmostItem;
        readonly string settingsPath;

        Manager manager;
        Session session;
        bool busy;
        bool compact;
        long artHash = -1;

        public Player()
        {
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("Player.xaml"))
                Window = (Window)XamlReader.Load(xaml);

            normalView = Find<FrameworkElement>("NormalView");
            compactView = Find<FrameworkElement>("CompactView");
            art = Find<Border>("Art");
            artC = Find<Border>("ArtC");
            artNote = Find<UIElement>("ArtNote");
            artNoteC = Find<UIElement>("ArtNoteC");
            backdrop = Find<System.Windows.Shapes.Rectangle>("Backdrop");
            titleText = Find<TextBlock>("TitleText");
            artistText = Find<TextBlock>("ArtistText");
            lineC = Find<TextBlock>("LineC");
            titleC = Find<Run>("TitleC");
            artistC = Find<Run>("ArtistC");
            playIcon = Find<System.Windows.Shapes.Path>("PlayIcon");
            playIconC = Find<System.Windows.Shapes.Path>("PlayIconC");

            Find<Button>("PrevBtn").Click += OnPrev;
            Find<Button>("PrevBtnC").Click += OnPrev;
            Find<Button>("NextBtn").Click += OnNext;
            Find<Button>("NextBtnC").Click += OnNext;
            Find<Button>("PlayBtn").Click += OnPlay;
            Find<Button>("PlayBtnC").Click += OnPlay;
            pinIcon = Find<System.Windows.Shapes.Path>("PinIcon");
            pinIconC = Find<System.Windows.Shapes.Path>("PinIconC");
            pinBtn = Find<Button>("PinBtn");
            pinBtnC = Find<Button>("PinBtnC");
            pinBtn.Click += OnPin;
            pinBtnC.Click += OnPin;
            Find<Button>("ExpandBtn").Click += delegate { SetCompact(false); SaveSettings(); };
            Find<Button>("CompactBtn").Click += delegate { SetCompact(true); SaveSettings(); };
            Find<Button>("CloseBtn").Click += delegate { Window.Close(); };

            // Keep the blurred cover inside the rounded card
            var clipGrid = Find<FrameworkElement>("ClipGrid");
            clipGrid.SizeChanged += delegate
            {
                clipGrid.Clip = new RectangleGeometry(new Rect(0, 0, clipGrid.ActualWidth, clipGrid.ActualHeight), 13, 13);
            };

            compactItem = new MenuItem { Header = "Compact mode", IsCheckable = true };
            compactItem.Click += delegate { SetCompact(compactItem.IsChecked); SaveSettings(); };
            topmostItem = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = true };
            topmostItem.Click += delegate { SetTopmost(topmostItem.IsChecked); SaveSettings(); };
            var openItem = new MenuItem { Header = "Open Yandex Music" };
            openItem.Click += delegate { LaunchYandexMusic(); };
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += delegate { Window.Close(); };
            var menu = new ContextMenu();
            menu.Items.Add(compactItem);
            menu.Items.Add(topmostItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);
            Window.ContextMenu = menu;

            Window.MouseLeftButtonDown += OnMouseDown;
            Window.Closing += delegate { SaveSettings(); };

            settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaMiniPlayer", "settings.txt");
            SetTopmost(true);
            LoadSettings();
            ShowIdle();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async delegate { await Refresh(); };
            Window.Loaded += async delegate { await Refresh(); timer.Start(); };
        }

        T Find<T>(string name) where T : class
        {
            return (T)Window.FindName(name);
        }

        // ---- window behaviour ------------------------------------------------

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetCompact(!compact);
                SaveSettings();
                return;
            }
            try { Window.DragMove(); } catch (InvalidOperationException) { }
            SaveSettings();
        }

        void SetCompact(bool value)
        {
            compact = value;
            compactItem.IsChecked = value;
            normalView.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            compactView.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnPin(object sender, RoutedEventArgs e)
        {
            SetTopmost(!Window.Topmost);
            SaveSettings();
        }

        // Pinned: solid yellow upright pin. Unpinned: outlined pin tipped over.
        void SetTopmost(bool value)
        {
            Window.Topmost = value;
            topmostItem.IsChecked = value;
            Brush yellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
            Brush outline = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            string tip = value ? "Always on top: on (click to unpin)" : "Always on top: off (click to pin)";
            foreach (var icon in new[] { pinIcon, pinIconC })
            {
                icon.Fill = value ? yellow : Brushes.Transparent;
                icon.Stroke = value ? yellow : outline;
                icon.RenderTransform = value ? Transform.Identity : new RotateTransform(45);
            }
            foreach (var btn in new[] { pinBtn, pinBtnC })
            {
                btn.ToolTip = tip;
                // An active pin stays fully lit instead of dimming with the other secondary buttons
                if (value) btn.Opacity = 1; else btn.ClearValue(UIElement.OpacityProperty);
            }
        }

        void LoadSettings()
        {
            double left = double.NaN, top = double.NaN;
            try
            {
                foreach (string line in File.ReadAllLines(settingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                    if (key == "left") left = double.Parse(val, CultureInfo.InvariantCulture);
                    else if (key == "top") top = double.Parse(val, CultureInfo.InvariantCulture);
                    else if (key == "compact") SetCompact(val == "1");
                    else if (key == "topmost") SetTopmost(val == "1");
                }
            }
            catch (IOException) { }
            catch (FormatException) { }

            // Fall back to the bottom-right corner if there is no saved spot or it is off-screen
            bool visible = !double.IsNaN(left) && !double.IsNaN(top)
                && left > SystemParameters.VirtualScreenLeft - 40
                && top > SystemParameters.VirtualScreenTop - 20
                && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
            if (!visible)
            {
                Rect work = SystemParameters.WorkArea;
                left = work.Right - 344;
                top = work.Bottom - 136;
            }
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            Window.Left = left;
            Window.Top = top;
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new[]
                {
                    "left=" + Window.Left.ToString(CultureInfo.InvariantCulture),
                    "top=" + Window.Top.ToString(CultureInfo.InvariantCulture),
                    "compact=" + (compact ? "1" : "0"),
                    "topmost=" + (Window.Topmost ? "1" : "0")
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- media session ---------------------------------------------------

        // Yandex Music desktop app first, then anything Yandex (e.g. Yandex Browser),
        // then whatever Windows considers the current player (music.yandex.ru in another browser).
        Session PickSession()
        {
            Session anyYandex = null;
            foreach (Session s in manager.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (id.IndexOf("yandex", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (id.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0) return s;
                if (anyYandex == null) anyYandex = s;
            }
            return anyYandex ?? manager.GetCurrentSession();
        }

        async Task Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                if (manager == null) manager = await Async.AsTask(Manager.RequestAsync());
                session = PickSession();
                if (session == null) { ShowIdle(); return; }

                var props = await Async.AsTask(session.TryGetMediaPropertiesAsync());
                var info = session.GetPlaybackInfo();
                SetPlaying(info != null && info.PlaybackStatus == Status.Playing);
                SetText(props.Title, props.Artist, props.AlbumTitle);
                await UpdateArt(props.Thumbnail);
            }
            catch (Exception)
            {
                // The session can vanish mid-call when the player closes; next tick sorts it out
                session = null;
                ShowIdle();
            }
            finally
            {
                busy = false;
            }
        }

        async void RefreshSoon()
        {
            await Task.Delay(150);
            await Refresh();
            await Task.Delay(500);
            await Refresh();
        }

        async void OnPlay(object sender, RoutedEventArgs e)
        {
            Session s = session;
            if (s == null) { LaunchYandexMusic(); return; }
            try { await Async.AsTask(s.TryTogglePlayPauseAsync()); } catch (Exception) { }
            RefreshSoon();
        }

        async void OnNext(object sender, RoutedEventArgs e)
        {
            Session s = session;
            if (s == null) return;
            try { await Async.AsTask(s.TrySkipNextAsync()); } catch (Exception) { }
            RefreshSoon();
        }

        async void OnPrev(object sender, RoutedEventArgs e)
        {
            Session s = session;
            if (s == null) return;
            try { await Async.AsTask(s.TrySkipPreviousAsync()); } catch (Exception) { }
            RefreshSoon();
        }

        // ---- display ---------------------------------------------------------

        void ShowIdle()
        {
            SetPlaying(false);
            SetText("Yandex Music", "Nothing playing — press play to open", null);
            SetArt(null);
            artHash = -1;
        }

        void SetPlaying(bool playing)
        {
            Geometry g = Geometry.Parse(playing ? PauseGeo : PlayGeo);
            playIcon.Data = g;
            playIconC.Data = g;
        }

        void SetText(string title, string artist, string album)
        {
            if (string.IsNullOrEmpty(title)) title = "Yandex Music";
            artist = artist ?? "";
            if (titleText.Text == title && artistText.Text == artist) return;

            titleText.Text = title;
            artistText.Text = artist;
            titleC.Text = title;
            artistC.Text = artist.Length > 0 ? "  —  " + artist : "";

            string tip = title;
            if (artist.Length > 0) tip += "\n" + artist;
            if (!string.IsNullOrEmpty(album)) tip += "\n" + album;
            titleText.ToolTip = tip;
            artistText.ToolTip = tip;
            lineC.ToolTip = tip;
            art.ToolTip = tip;
            artC.ToolTip = tip;
        }

        async Task UpdateArt(StreamRef thumbnail)
        {
            if (thumbnail == null)
            {
                if (artHash != -1) { SetArt(null); artHash = -1; }
                return;
            }

            byte[] bytes;
            using (var ras = await Async.AsTask(thumbnail.OpenReadAsync()))
            using (var reader = new DataReader(ras))
            {
                uint loaded = await Async.AsTask(reader.LoadAsync((uint)ras.Size));
                bytes = new byte[loaded];
                reader.ReadBytes(bytes);
            }

            // The cover often lands a moment after the title, so compare bytes instead of track names
            long hash = bytes.Length;
            for (int i = 0; i < bytes.Length; i += 7) hash = hash * 31 + bytes[i];
            if (hash == artHash) return;
            artHash = hash;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 200;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            SetArt(bmp);
        }

        void SetArt(ImageSource image)
        {
            Brush placeholder = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
            Brush cover = image == null ? null : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            art.Background = cover ?? placeholder;
            artC.Background = cover ?? placeholder;
            backdrop.Fill = cover;
            artNote.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
            artNoteC.Visibility = artNote.Visibility;
        }

        // ---- launching Yandex Music -----------------------------------------

        static void LaunchYandexMusic()
        {
            try
            {
                string appId = FindYandexMusicApp();
                if (appId != null) Process.Start("explorer.exe", "shell:AppsFolder\\" + appId);
                else Process.Start("https://music.yandex.ru");
            }
            catch (Exception) { }
        }

        // Looks through the Start menu's app list for the Yandex Music desktop app
        static string FindYandexMusicApp()
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string id = (string)item.Path ?? "";
                    string name = (string)item.Name ?? "";
                    bool byId = id.IndexOf("yandex", StringComparison.OrdinalIgnoreCase) >= 0
                        && id.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool byName = name.IndexOf("Yandex Music", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Яндекс Музыка", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (byId || byName) return id;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
