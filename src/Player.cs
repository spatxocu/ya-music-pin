// Ya Mini Player - a tiny always-on-top remote for Yandex Music.
// Talks to the player through Windows' System Media Transport Controls, so it
// needs no login and no Yandex API: whatever Yandex Music reports to Windows
// (title, artist, cover) is shown here, and the buttons send media commands back.
//
// Written against C# 5 so it builds with the csc.exe that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using StreamRef = Windows.Storage.Streams.IRandomAccessStreamReference;

[assembly: AssemblyTitle("Ya Mini Player")]
[assembly: AssemblyProduct("Ya Mini Player")]
[assembly: AssemblyDescription("Tiny floating remote for Yandex Music")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Ya Mini Player contributors")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]

namespace YaMini
{
    static class Program
    {
        public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        [STAThread]
        static void Main()
        {
            bool first;
            using (new Mutex(true, "YaMiniPlayer.SingleInstance", out first))
            {
                if (!first) return;   // already running

                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal error", e.ExceptionObject as Exception);
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Log.Write("Background task error", e.Exception);
                    e.SetObserved();
                };
                try
                {
                    Log.Write("Started, version " + Version);
                    if (!MediaControlsAvailable())
                    {
                        Log.Write("Windows media controls are missing on this version of Windows");
                        MessageBox.Show("Ya Mini Player needs Windows 10 (version 1809 or later) or Windows 11.",
                            "Ya Mini Player", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var app = new Application();
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    // A failure in one click or one refresh is logged and the player keeps running
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        Log.Write("Unexpected error", e.Exception);
                        e.Handled = true;
                    };
                    app.Run(new Player().Window);
                    Log.Write("Closed");
                }
                catch (Exception ex)
                {
                    Log.Write("Could not start", ex);
                    MessageBox.Show("Ya Mini Player ran into a problem and has to close.\n\nDetails were saved to:\n" + Log.FilePath,
                        "Ya Mini Player", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // The media session API arrived in Windows 10 1809; older systems do not have the type at all
        static bool MediaControlsAvailable()
        {
            try
            {
                return Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows, ContentType=WindowsRuntime", false) != null;
            }
            catch (Exception) { return false; }
        }
    }

    // Plain-text log next to the settings, for diagnosing problems on someone else's PC
    static class Log
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaMiniPlayer");
        public static readonly string FilePath = Path.Combine(Folder, "log.txt");

        const long MaxBytes = 256 * 1024;
        static readonly object gate = new object();
        static string last;

        public static void Write(string what, Exception error = null)
        {
            try
            {
                string text = error == null ? what : what + ": " + error;
                lock (gate)
                {
                    // A problem that repeats every second is written once, not thousands of times
                    if (text == last) return;
                    last = text;

                    Directory.CreateDirectory(Folder);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        string old = Path.Combine(Folder, "log.old.txt");
                        File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text + Environment.NewLine);
                }
            }
            catch (Exception) { }   // logging must never take the app down
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

    // Volume of the Yandex Music app alone, as shown for it in the Windows volume mixer.
    // The system master volume and other apps are never touched.
    static class AppVolume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class DeviceEnumerator { }

        // Only the methods used here are declared properly; the rest just hold their vtable slots.
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints();
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            void GetAudioSessionControl();
            void GetSimpleAudioVolume();
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            void GetState();
            void GetDisplayName();
            void SetDisplayName();
            void GetIconPath();
            void SetIconPath();
            void GetGroupingParam();
            void SetGroupingParam();
            void RegisterAudioSessionNotification();
            void UnregisterAudioSessionNotification();
            void GetSessionIdentifier();
            void GetSessionInstanceIdentifier();
            [PreserveSig] int GetProcessId(out uint processId);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
        }

        const string AppNameRu = "Яндекс Музыка";

        static bool IsYandexMusic(uint processId)
        {
            if (processId == 0) return false;
            try
            {
                string name = Process.GetProcessById((int)processId).ProcessName;
                return name.IndexOf(AppNameRu, StringComparison.OrdinalIgnoreCase) >= 0
                    || (name.IndexOf("yandex", StringComparison.OrdinalIgnoreCase) >= 0
                        && name.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch (ArgumentException) { return false; }         // process already gone
            catch (InvalidOperationException) { return false; }
        }

        // Yandex Music's audio streams on the default output device
        static List<ISimpleAudioVolume> Sessions()
        {
            var found = new List<ISimpleAudioVolume>();
            var devices = (IMMDeviceEnumerator)new DeviceEnumerator();
            IMMDevice device;
            if (devices.GetDefaultAudioEndpoint(0, 1, out device) != 0) return found;   // render, multimedia

            Guid iid = typeof(IAudioSessionManager2).GUID;
            object instance;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out instance) != 0) return found;

            IAudioSessionEnumerator sessions;
            if (((IAudioSessionManager2)instance).GetSessionEnumerator(out sessions) != 0) return found;
            int count;
            sessions.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 control;
                uint processId;
                if (sessions.GetSession(i, out control) != 0) continue;
                if (control.GetProcessId(out processId) >= 0 && IsYandexMusic(processId))
                    found.Add((ISimpleAudioVolume)control);
            }
            return found;
        }

        public static bool TryGet(out float level)
        {
            level = 0;
            try
            {
                List<ISimpleAudioVolume> sessions = Sessions();
                foreach (ISimpleAudioVolume s in sessions)
                {
                    float one;
                    if (s.GetMasterVolume(out one) == 0) level = Math.Max(level, one);
                }
                return sessions.Count > 0;
            }
            catch (Exception) { return false; }
        }

        public static bool Set(float level)
        {
            try
            {
                List<ISimpleAudioVolume> sessions = Sessions();
                Guid context = Guid.Empty;
                foreach (ISimpleAudioVolume s in sessions) s.SetMasterVolume(level, ref context);
                return sessions.Count > 0;
            }
            catch (Exception) { return false; }
        }
    }

    class LyricLine
    {
        public double Time;     // seconds from the start of the track; unused for untimed lyrics
        public string Text;
    }

    class LyricsResult
    {
        public List<LyricLine> Lines;
        public bool Synced;     // true when every line carries a time
    }

    // Lyrics come from LRCLIB (lrclib.net), a free public lyrics database. Yandex Music does not
    // hand its own lyrics to other apps. Only the track title and artist are sent.
    static class LyricsSource
    {
        static readonly Regex Stamp = new Regex(@"^\[(\d+):(\d+(?:[.,]\d+)?)\]\s*(.*)$");

        // Called off the UI thread. Returns null when nothing usable is found.
        public static LyricsResult Find(string title, string artist, double seconds)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2

            // The exact-match lookup is the quick, dependable one; the search behind it is the wider net
            LyricsResult found = seconds > 0 ? Pick(Exact(title, artist, seconds), seconds) : null;
            if (found != null) return found;
            found = Pick(Search(title, artist), seconds);
            if (found != null) return found;

            // Second try without the "(Slowed)" / "[Remix]" style suffix and with the first artist only
            string plainTitle = Regex.Replace(title, @"\s*[\(\[].*?[\)\]]", "").Trim();
            string firstArtist = artist.Split(',', '&')[0].Trim();
            if (plainTitle.Length == 0 || (plainTitle == title && firstArtist == artist)) return null;
            return Pick(Search(plainTitle, firstArtist), plainTitle == title ? seconds : 0);
        }

        static List<Dictionary<string, object>> Search(string title, string artist)
        {
            string json = Download("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
                + "&artist_name=" + Uri.EscapeDataString(artist));
            var items = json == null ? null : Parser().Deserialize<List<Dictionary<string, object>>>(json);
            return items ?? new List<Dictionary<string, object>>();
        }

        static List<Dictionary<string, object>> Exact(string title, string artist, double seconds)
        {
            string json = Download("https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
                + "&artist_name=" + Uri.EscapeDataString(artist)
                + "&duration=" + Math.Round(seconds).ToString(CultureInfo.InvariantCulture));
            var items = new List<Dictionary<string, object>>();
            if (json != null) items.Add(Parser().Deserialize<Dictionary<string, object>>(json));
            return items;
        }

        static JavaScriptSerializer Parser()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        // Returns null for "not found". The service answers "busy" fairly often, so other failures
        // are retried a couple of times before giving up.
        static string Download(string url)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.UserAgent = "YaMiniPlayer/" + Program.Version + " (https://github.com/spatxocu/ya-music-pin)";
                    request.Timeout = 15000;
                    using (WebResponse response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (WebException ex)
                {
                    var reply = ex.Response as HttpWebResponse;
                    if (reply != null && reply.StatusCode == HttpStatusCode.NotFound) return null;
                    if (attempt == 3) throw;
                    Thread.Sleep(1500 * attempt);
                }
            }
        }

        // Timed lyrics are only trusted when the recording is the same length as the one playing;
        // otherwise the words would light up at the wrong moments, so plain text is used instead.
        static LyricsResult Pick(List<Dictionary<string, object>> items, double seconds)
        {
            Dictionary<string, object> timed = null, plain = null;
            double timedGap = double.MaxValue, plainGap = double.MaxValue;
            foreach (var item in items)
            {
                double gap = 0;
                object length;
                if (seconds > 0)
                {
                    // An entry that does not say how long it is cannot be trusted for timing
                    gap = item.TryGetValue("duration", out length) && length != null
                        ? Math.Abs(Convert.ToDouble(length, CultureInfo.InvariantCulture) - seconds)
                        : 999;
                }

                if (Text(item, "syncedLyrics") != null && gap <= 3 && gap < timedGap) { timed = item; timedGap = gap; }
                if (Text(item, "plainLyrics") != null && gap < plainGap) { plain = item; plainGap = gap; }
            }

            if (timed != null)
            {
                var lines = new List<LyricLine>();
                foreach (string raw in Text(timed, "syncedLyrics").Split('\n'))
                {
                    Match m = Stamp.Match(raw.Trim());
                    if (!m.Success || m.Groups[3].Value.Trim().Length == 0) continue;
                    double time = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                        + double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    lines.Add(new LyricLine { Time = time, Text = m.Groups[3].Value.Trim() });
                }
                if (lines.Count > 0) return new LyricsResult { Lines = lines, Synced = true };
            }

            if (plain != null)
            {
                var lines = new List<LyricLine>();
                foreach (string raw in Text(plain, "plainLyrics").Split('\n'))
                    lines.Add(new LyricLine { Text = raw.Trim() });
                return new LyricsResult { Lines = lines, Synced = false };
            }
            return null;
        }

        static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            string text = item.TryGetValue(key, out value) ? value as string : null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    struct Part
    {
        public readonly string Text;
        public readonly Brush Brush;
        public readonly FontWeight Weight;

        public Part(string text, Brush brush, FontWeight weight)
        {
            Text = text;
            Brush = brush;
            Weight = weight;
        }
    }

    // One line of text that scrolls to the left in a loop when it is too wide for its host
    class Marquee
    {
        const double Gap = 40;          // space between the end of the text and its repeat
        const double Speed = 32;        // pixels per second
        const double PauseSeconds = 2;  // rest at the start of every lap

        public readonly Canvas Host;

        readonly double fontSize;
        readonly bool centered;
        readonly Canvas strip = new Canvas();
        readonly TranslateTransform shift = new TranslateTransform();
        Part[] parts = new Part[0];

        public Marquee(Canvas host, double fontSize, bool centered)
        {
            Host = host;
            this.fontSize = fontSize;
            this.centered = centered;
            strip.RenderTransform = shift;
            host.Children.Add(strip);
            host.SizeChanged += delegate { Layout(); };
        }

        public void Set(params Part[] newParts)
        {
            parts = newParts;
            Layout();
        }

        TextBlock Build(bool scrolling)
        {
            var block = new TextBlock { FontSize = fontSize };
            // Pixel-snapped text moves in visible steps, so scrolling text uses ideal metrics
            if (scrolling) TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
            foreach (Part p in parts)
                block.Inlines.Add(new Run(p.Text) { Foreground = p.Brush, FontWeight = p.Weight });
            return block;
        }

        void Layout()
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            strip.Children.Clear();
            Host.OpacityMask = null;

            double room = Host.ActualWidth;
            if (room <= 0) return;

            TextBlock first = Build(false);
            first.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = first.DesiredSize.Width;
            if (width <= room)
            {
                if (centered) Canvas.SetLeft(first, Math.Floor((room - width) / 2));
                strip.Children.Add(first);
                return;
            }

            // Two copies one lap apart, so the loop restarts without a visible jump
            first = Build(true);
            TextBlock second = Build(true);
            double lap = width + Gap;
            Canvas.SetLeft(second, lap);
            strip.Children.Add(first);
            strip.Children.Add(second);

            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0.9));
            fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            Host.OpacityMask = fade;

            var slide = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds))));
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(-lap, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds + lap / Speed))));
            shift.BeginAnimation(TranslateTransform.XProperty, slide);
        }
    }

    class Player
    {
        const string PlayGeo = "M3,1 L13,7 L3,13 Z";
        const string PauseGeo = "M2,1 H6 V13 H2 Z M8,1 H12 V13 H8 Z";

        public readonly Window Window;

        const int Full = 0, Compact = 1, Vinyl = 2, LyricsMode = 3;
        static readonly string[] ModeNames = { "full", "compact", "vinyl", "lyrics" };

        readonly FrameworkElement[] views;
        readonly FrameworkElement volBar;
        readonly RowDefinition volEmpty, volFull;
        readonly Border art, artC, artL;
        readonly Marquee titleLineL, artistLineL;
        readonly ScrollViewer lyricScroll;
        readonly StackPanel lyricPanel;
        readonly TextBlock lyricStatus;
        readonly DispatcherTimer lyricTimer;
        List<LyricLine> lyrics;         // null while there is nothing to show
        bool lyricsSynced;
        int lyricIndex = -1;
        double lyricTarget = -1;        // scroll offset the view is easing towards
        string trackKey = "", lyricsKey, lyricRetryKey;
        int missedSessions;             // refreshes in a row that found no player
        string trackTitle = "", trackArtist = "";
        TimeSpan position, duration;    // as last reported by the player
        DateTimeOffset positionStamp;
        readonly System.Windows.Shapes.Ellipse discArt;
        readonly UIElement[] artNotes;
        readonly System.Windows.Shapes.Rectangle backdrop;
        readonly Marquee titleLine, artistLine, compactLine, titleLineV, artistLineV;
        string lastTitle, lastArtist;
        readonly System.Windows.Shapes.Path[] playIcons, pinIcons;
        readonly Button[] pinBtns;
        readonly MenuItem[] modeItems;
        readonly MenuItem topmostItem, volItem;
        readonly AnimationClock spinClock;
        readonly string settingsPath;

        Manager manager;
        Session session;
        bool busy;
        int mode;
        bool playing;
        bool volDragging;
        double volLevel = -1;
        long artHash = -1;

        public Player()
        {
            using (Stream xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("Player.xaml"))
                Window = (Window)XamlReader.Load(xaml);

            views = new[]
            {
                Find<FrameworkElement>("NormalView"), Find<FrameworkElement>("CompactView"), Find<FrameworkElement>("VinylView"),
                Find<FrameworkElement>("LyricsView")
            };
            art = Find<Border>("Art");
            artC = Find<Border>("ArtC");
            discArt = Find<System.Windows.Shapes.Ellipse>("DiscArt");
            artL = Find<Border>("ArtL");
            artNotes = new[]
            {
                Find<UIElement>("ArtNote"), Find<UIElement>("ArtNoteC"), Find<UIElement>("ArtNoteV"), Find<UIElement>("ArtNoteL")
            };
            titleLineL = new Marquee(Find<Canvas>("TitleHostL"), 13, false);
            artistLineL = new Marquee(Find<Canvas>("ArtistHostL"), 11.5, false);
            lyricScroll = Find<ScrollViewer>("LyricScroll");
            lyricPanel = Find<StackPanel>("LyricPanel");
            lyricStatus = Find<TextBlock>("LyricStatus");
            lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            lyricTimer.Tick += delegate { FollowLyrics(); };
            backdrop = Find<System.Windows.Shapes.Rectangle>("Backdrop");
            titleLine = new Marquee(Find<Canvas>("TitleHost"), 14, false);
            artistLine = new Marquee(Find<Canvas>("ArtistHost"), 12, false);
            compactLine = new Marquee(Find<Canvas>("LineHost"), 12.5, false);
            titleLineV = new Marquee(Find<Canvas>("TitleHostV"), 14, true);
            artistLineV = new Marquee(Find<Canvas>("ArtistHostV"), 12, true);
            playIcons = new[]
            {
                Find<System.Windows.Shapes.Path>("PlayIcon"), Find<System.Windows.Shapes.Path>("PlayIconC"),
                Find<System.Windows.Shapes.Path>("PlayIconV"), Find<System.Windows.Shapes.Path>("PlayIconL")
            };
            pinIcons = new[]
            {
                Find<System.Windows.Shapes.Path>("PinIcon"), Find<System.Windows.Shapes.Path>("PinIconC"),
                Find<System.Windows.Shapes.Path>("PinIconV"), Find<System.Windows.Shapes.Path>("PinIconL")
            };
            pinBtns = new[] { Find<Button>("PinBtn"), Find<Button>("PinBtnC"), Find<Button>("PinBtnV"), Find<Button>("PinBtnL") };

            foreach (string suffix in new[] { "", "C", "V", "L" })
            {
                Find<Button>("PrevBtn" + suffix).Click += OnPrev;
                Find<Button>("NextBtn" + suffix).Click += OnNext;
                Find<Button>("PlayBtn" + suffix).Click += OnPlay;
                Find<Button>("PinBtn" + suffix).Click += OnPin;
            }
            // The size button in each view leads to the next one: full -> compact -> vinyl -> full
            Find<Button>("CompactBtn").Click += delegate { SetMode(Compact); SaveSettings(); };
            Find<Button>("ExpandBtn").Click += delegate { SetMode(Vinyl); SaveSettings(); };
            Find<Button>("ModeBtnV").Click += delegate { SetMode(LyricsMode); SaveSettings(); };
            Find<Button>("ModeBtnL").Click += delegate { SetMode(Full); SaveSettings(); };
            Find<Button>("CloseBtnL").Click += delegate { Window.Close(); };
            Find<Button>("CloseBtn").Click += delegate { Window.Close(); };
            Find<Button>("CloseBtnV").Click += delegate { Window.Close(); };

            // The record turns once every 7 seconds and holds its angle while paused
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(7))) { RepeatBehavior = RepeatBehavior.Forever };
            spinClock = spin.CreateClock();
            Find<FrameworkElement>("Disc").RenderTransform.ApplyAnimationClock(RotateTransform.AngleProperty, spinClock);
            spinClock.Controller.Pause();

            volBar = Find<FrameworkElement>("VolBar");
            volEmpty = Find<RowDefinition>("VolEmpty");
            volFull = Find<RowDefinition>("VolFull");
            volBar.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;   // do not start dragging the window
                volDragging = volBar.CaptureMouse();
                SetVolume(1 - e.GetPosition(volBar).Y / volBar.ActualHeight);
            };
            volBar.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (volDragging) SetVolume(1 - e.GetPosition(volBar).Y / volBar.ActualHeight);
            };
            volBar.MouseLeftButtonUp += delegate
            {
                volDragging = false;
                volBar.ReleaseMouseCapture();
            };
            Window.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                if (volLevel >= 0) SetVolume(volLevel + (e.Delta > 0 ? 0.05 : -0.05));
            };

            // Keep the blurred cover inside the rounded card
            var clipGrid = Find<FrameworkElement>("ClipGrid");
            clipGrid.SizeChanged += delegate
            {
                clipGrid.Clip = new RectangleGeometry(new Rect(0, 0, clipGrid.ActualWidth, clipGrid.ActualHeight), 13, 13);
            };

            modeItems = new[]
            {
                new MenuItem { Header = "Full size" }, new MenuItem { Header = "Compact" }, new MenuItem { Header = "Vinyl" },
                new MenuItem { Header = "Lyrics" }
            };
            for (int i = 0; i < modeItems.Length; i++)
            {
                int target = i;
                modeItems[i].Click += delegate { SetMode(target); SaveSettings(); };
            }
            volItem = new MenuItem { Header = "Volume bar", IsCheckable = true, IsChecked = true };
            volItem.Click += delegate { SetVolumeBar(volItem.IsChecked); SaveSettings(); };
            topmostItem = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = true };
            topmostItem.Click += delegate { SetTopmost(topmostItem.IsChecked); SaveSettings(); };
            var logItem = new MenuItem { Header = "Open log folder" };
            logItem.Click += delegate
            {
                try { Directory.CreateDirectory(Log.Folder); Process.Start(Log.Folder); }
                catch (Exception ex) { Log.Write("Could not open the log folder", ex); }
            };
            var openItem = new MenuItem { Header = "Open Yandex Music" };
            openItem.Click += delegate { LaunchYandexMusic(); };
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += delegate { Window.Close(); };
            var menu = new ContextMenu();
            foreach (MenuItem item in modeItems) menu.Items.Add(item);
            menu.Items.Add(new Separator());
            menu.Items.Add(volItem);
            menu.Items.Add(topmostItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(openItem);
            menu.Items.Add(logItem);
            menu.Items.Add(exitItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Ya Mini Player " + Program.Version, IsEnabled = false });
            Window.ContextMenu = menu;

            // When the view changes size, grow away from the nearest screen edges so it stays on screen
            Window.SizeChanged += delegate(object s, SizeChangedEventArgs e)
            {
                if (!Window.IsLoaded) return;   // the first layout must not move the saved position
                Rect work = SystemParameters.WorkArea;
                if (Window.Top + e.PreviousSize.Height / 2 > work.Top + work.Height / 2)
                    Window.Top += e.PreviousSize.Height - e.NewSize.Height;
                if (Window.Left + e.PreviousSize.Width / 2 > work.Left + work.Width / 2)
                    Window.Left += e.PreviousSize.Width - e.NewSize.Width;
                SaveSettings();
            };
            // A spot saved for a small view can push a bigger one past the edge of the main screen
            Window.Loaded += delegate
            {
                Rect work = SystemParameters.WorkArea;
                if (!work.Contains(new Point(Window.Left, Window.Top))) return;
                Window.Left = Math.Max(work.Left, Math.Min(Window.Left, work.Right - Window.ActualWidth));
                Window.Top = Math.Max(work.Top, Math.Min(Window.Top, work.Bottom - Window.ActualHeight));
            };

            Window.MouseLeftButtonDown += OnMouseDown;
            Window.Closing += delegate { SaveSettings(); };
            Window.Loaded += delegate { SaveSettings(); };   // creates the settings file on first run

            settingsPath = Path.Combine(Log.Folder, "settings.txt");
            SetTopmost(true);
            SetMode(Full);
            LoadSettings();
            ShowIdle();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async delegate { ReadVolume(); await Refresh(); };
            Window.Loaded += async delegate { ReadVolume(); await Refresh(); timer.Start(); };
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
                SetMode((mode + 1) % views.Length);
                SaveSettings();
                return;
            }
            try { Window.DragMove(); } catch (InvalidOperationException) { }
            SaveSettings();
        }

        void SetMode(int value)
        {
            mode = value;
            for (int i = 0; i < views.Length; i++)
            {
                views[i].Visibility = i == value ? Visibility.Visible : Visibility.Collapsed;
                modeItems[i].IsChecked = i == value;
            }
            UpdateSpin();

            // Lyrics are only looked up while their view is open
            if (mode == LyricsMode)
            {
                lyricTimer.Start();
                if (lyricsKey != trackKey) LoadLyrics();
            }
            else lyricTimer.Stop();
        }

        // ---- lyrics ----------------------------------------------------------

        async void LoadLyrics()
        {
            string key = trackKey, title = trackTitle, artist = trackArtist;
            lyricsKey = key;
            if (session == null) { ShowLyrics(null, "No lyrics"); return; }
            ShowLyrics(null, "Looking for lyrics…");
            bool failed = false;
            try
            {
                // Give the player a moment to report the new track's length; it picks the right version
                await Task.Delay(800);
                if (key != trackKey) return;
                double seconds = duration.TotalSeconds;
                LyricsResult found = await Task.Run(() => LyricsSource.Find(title, artist, seconds));
                if (key != trackKey) return;   // the track changed while we were looking
                if (found == null) ShowLyrics(null, "No lyrics");
                else ShowLyrics(found, null);
            }
            catch (Exception ex)
            {
                // Offline or the lyrics service is down: say so, and look again next time the view opens
                Log.Write("Lyrics lookup failed", ex);
                if (key == trackKey)
                {
                    ShowLyrics(null, "Lyrics unavailable right now");
                    lyricsKey = null;
                    failed = true;
                }
            }

            // The lyrics service is often busy for a few seconds; try each track once more on its own
            if (failed && lyricRetryKey != key)
            {
                lyricRetryKey = key;
                await Task.Delay(15000);
                if (key == trackKey && lyricsKey == null && mode == LyricsMode) LoadLyrics();
            }
        }

        void ShowLyrics(LyricsResult result, string status)
        {
            lyrics = result == null ? null : result.Lines;
            lyricsSynced = result != null && result.Synced;
            lyricIndex = -1;
            lyricTarget = -1;
            lyricPanel.Children.Clear();
            lyricStatus.Text = status ?? "";
            lyricScroll.ScrollToVerticalOffset(0);
            if (lyrics == null) return;

            // Timed lyrics get room above and below so the first and last lines can sit in the middle
            lyricPanel.Margin = lyricsSynced ? new Thickness(0, 100, 0, 100) : new Thickness(0, 10, 0, 10);
            foreach (LyricLine line in lyrics)
            {
                lyricPanel.Children.Add(new TextBlock
                {
                    Text = line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Margin = new Thickness(0, 5, 0, 5),
                    Foreground = lyricsSynced ? Dim(0x66) : Dim(0xD9)
                });
            }
        }

        // Runs many times a second in the lyrics view: lights up the line being sung and glides to it
        void FollowLyrics()
        {
            if (lyrics == null || !lyricsSynced) return;

            // The player reports its position only now and then, so count on from the last report
            double now = position.TotalSeconds;
            if (playing) now += (DateTimeOffset.Now - positionStamp).TotalSeconds;

            int index = -1;
            for (int i = 0; i < lyrics.Count && lyrics[i].Time <= now + 0.15; i++) index = i;

            if (index != lyricIndex)
            {
                if (lyricIndex >= 0) Highlight(lyricIndex, false);
                lyricIndex = index;
                lyricTarget = 0;
                if (index >= 0)
                {
                    Highlight(index, true);
                    lyricPanel.UpdateLayout();
                    var line = (FrameworkElement)lyricPanel.Children[index];
                    if (line.IsVisible)
                    {
                        double top = line.TransformToAncestor(lyricScroll).Transform(new Point(0, 0)).Y;
                        lyricTarget = lyricScroll.VerticalOffset + top + line.ActualHeight / 2 - lyricScroll.ViewportHeight / 2;
                    }
                }
                lyricTarget = Math.Max(0, Math.Min(lyricTarget, lyricScroll.ScrollableHeight));
            }

            if (lyricTarget < 0) return;
            double gap = lyricTarget - lyricScroll.VerticalOffset;
            if (Math.Abs(gap) < 0.5) lyricTarget = -1;
            else lyricScroll.ScrollToVerticalOffset(lyricScroll.VerticalOffset + gap * 0.15);
        }

        void Highlight(int index, bool current)
        {
            var line = (TextBlock)lyricPanel.Children[index];
            line.Foreground = current ? Brushes.White : Dim(0x66);
            line.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void UpdateSpin()
        {
            if (playing && mode == Vinyl) spinClock.Controller.Resume();
            else spinClock.Controller.Pause();
        }

        // ---- volume ----------------------------------------------------------

        void SetVolumeBar(bool visible)
        {
            volItem.IsChecked = visible;
            volBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Picks up changes made elsewhere, e.g. in the Windows volume mixer
        void ReadVolume()
        {
            if (volDragging) return;
            float level;
            volLevel = AppVolume.TryGet(out level) ? level : -1;
            ShowVolume();
        }

        void SetVolume(double level)
        {
            level = Math.Max(0, Math.Min(1, level));
            volLevel = AppVolume.Set((float)level) ? level : -1;
            ShowVolume();
        }

        void ShowVolume()
        {
            bool known = volLevel >= 0;
            volEmpty.Height = new GridLength(known ? 1 - volLevel : 1, GridUnitType.Star);
            volFull.Height = new GridLength(known ? volLevel : 0, GridUnitType.Star);
            volBar.Opacity = known ? 1 : 0.4;
            volBar.ToolTip = known
                ? "Yandex Music volume: " + Math.Round(volLevel * 100) + "%"
                : "Volume: play something in the Yandex Music app first";
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
            foreach (var icon in pinIcons)
            {
                icon.Fill = value ? yellow : Brushes.Transparent;
                icon.Stroke = value ? yellow : outline;
                icon.RenderTransform = value ? Transform.Identity : new RotateTransform(45);
            }
            foreach (var btn in pinBtns)
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
                    else if (key == "compact") { if (val == "1") SetMode(Compact); }   // settings from before vinyl mode
                    else if (key == "mode") SetMode(Math.Max(0, Array.IndexOf(ModeNames, val)));
                    else if (key == "volbar") SetVolumeBar(val == "1");
                    else if (key == "topmost") SetTopmost(val == "1");
                }
            }
            catch (IOException) { }   // no settings yet: first run
            catch (Exception ex) { Log.Write("Settings could not be read, using defaults", ex); }

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
                    "mode=" + ModeNames[mode],
                    "topmost=" + (Window.Topmost ? "1" : "0"),
                    "volbar=" + (volItem.IsChecked ? "1" : "0")
                });
            }
            catch (Exception ex) { Log.Write("Settings could not be saved", ex); }
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
                if (session == null)
                {
                    // The player drops out for a moment between tracks; only a lasting absence counts as idle
                    if (++missedSessions >= 3) ShowIdle();
                    return;
                }
                missedSessions = 0;

                var props = await Async.AsTask(session.TryGetMediaPropertiesAsync());
                var info = session.GetPlaybackInfo();
                SetPlaying(info != null && info.PlaybackStatus == Status.Playing);
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    position = timeline.Position - timeline.StartTime;
                    duration = timeline.EndTime - timeline.StartTime;
                    positionStamp = timeline.LastUpdatedTime;
                }
                SetText(props.Title, props.Artist, props.AlbumTitle);
                await UpdateArt(props.Thumbnail);
            }
            catch (Exception ex)
            {
                // The session can vanish mid-call when the player closes; next tick sorts it out
                Log.Write("Lost contact with the music player, will retry", ex);
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
            try { await Async.AsTask(s.TryTogglePlayPauseAsync()); } catch (Exception ex) { Log.Write("Play/pause failed", ex); }
            RefreshSoon();
        }

        async void OnNext(object sender, RoutedEventArgs e)
        {
            Session s = session;
            if (s == null) return;
            try { await Async.AsTask(s.TrySkipNextAsync()); } catch (Exception ex) { Log.Write("Next failed", ex); }
            RefreshSoon();
        }

        async void OnPrev(object sender, RoutedEventArgs e)
        {
            Session s = session;
            if (s == null) return;
            try { await Async.AsTask(s.TrySkipPreviousAsync()); } catch (Exception ex) { Log.Write("Previous failed", ex); }
            RefreshSoon();
        }

        // ---- display ---------------------------------------------------------

        void ShowIdle()
        {
            SetPlaying(false);
            SetText("Yandex Music", "Press play to open", null);
            SetArt(null);
            artHash = -1;
        }

        void SetPlaying(bool value)
        {
            playing = value;
            Geometry g = Geometry.Parse(value ? PauseGeo : PlayGeo);
            foreach (var icon in playIcons) icon.Data = g;
            UpdateSpin();
        }

        void SetText(string title, string artist, string album)
        {
            if (string.IsNullOrEmpty(title)) title = "Yandex Music";
            artist = artist ?? "";
            if (lastTitle == title && lastArtist == artist) return;
            lastTitle = title;
            lastArtist = artist;

            titleLine.Set(new Part(title, Brushes.White, FontWeights.SemiBold));
            artistLine.Set(new Part(artist, Dim(0xB3), FontWeights.Normal));
            compactLine.Set(
                new Part(title, Brushes.White, FontWeights.SemiBold),
                new Part(artist.Length > 0 ? "  —  " + artist : "", Dim(0xA6), FontWeights.Normal));

            string tip = title;
            if (artist.Length > 0) tip += "\n" + artist;
            if (!string.IsNullOrEmpty(album)) tip += "\n" + album;
            titleLineV.Set(new Part(title, Brushes.White, FontWeights.SemiBold));
            artistLineV.Set(new Part(artist, Dim(0xB3), FontWeights.Normal));
            titleLineL.Set(new Part(title, Brushes.White, FontWeights.SemiBold));
            artistLineL.Set(new Part(artist, Dim(0xB3), FontWeights.Normal));

            trackTitle = title;
            trackArtist = artist;
            trackKey = title + "\n" + artist;
            if (mode == LyricsMode) LoadLyrics();

            foreach (Marquee line in new[] { titleLine, artistLine, compactLine, titleLineV, artistLineV, titleLineL, artistLineL })
                line.Host.ToolTip = tip;
            art.ToolTip = tip;
            artC.ToolTip = tip;
            discArt.ToolTip = tip;
        }

        static Brush Dim(byte alpha)
        {
            return new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
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
            artL.Background = cover ?? placeholder;
            discArt.Fill = cover ?? placeholder;
            backdrop.Fill = cover;
            foreach (UIElement note in artNotes)
                note.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
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
            catch (Exception ex) { Log.Write("Could not open Yandex Music", ex); }
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
