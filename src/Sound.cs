// Sound modes. Windows has no way to put an effect on another app's audio directly, so while a
// mode is on the player:
//   1. turns Yandex Music down to -80 dB in the Windows volume mixer (far too quiet to hear),
//   2. records Yandex Music's own output with Windows process loopback, which is an exact
//      floating-point copy taken after that mixer level,
//   3. scales it back up by the same amount, applies the effect, and plays the result itself.
// Turning the mode off, closing the player, or any failure puts Yandex Music's level back.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace YaMini
{
    static class Effects
    {
        public const int Normal = 0, Club = 1;
        public static readonly string[] Names = { "normal", "club" };
        public static readonly string[] Labels = { "Normal", "Club" };
    }

    class SoundEngine
    {
        // Yandex Music's mixer level while a mode is on; the captured copy is multiplied back by 1 / Tiny
        public const float Tiny = 0.0001f;
        const int Rate = 48000;
        const int RingFrames = Rate * 2;

        public static SoundEngine Current;      // for the last-chance restore in the crash handler

        public volatile int Effect;
        // The player's volume bar while a mode is on. Yandex Music goes back to this level afterwards.
        public volatile float Volume = 1f;
        public event Action Failed;              // raised on the engine thread when sound modes cannot work here
        public bool Running { get { return thread != null; } }

        Thread thread;
        volatile bool stop;
        bool tookOver;                           // Yandex Music is currently turned down by us

        public SoundEngine() { Current = this; }

        public void Start(int effect)
        {
            Effect = effect;
            if (thread != null) return;
            stop = false;
            thread = new Thread(Run) { IsBackground = true, Name = "Sound", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        // Returns true when Yandex Music's level is back where the user wants it
        public bool Stop()
        {
            Thread t = thread;
            if (t == null) return !tookOver;
            stop = true;
            t.Join(3000);
            thread = null;
            return !tookOver;
        }

        public void EmergencyRestore()
        {
            try { if (tookOver) AppVolume.Set(Volume); } catch (Exception) { }
        }

        // ---- engine thread ---------------------------------------------------

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        void Run()
        {
            int failures = 0;
            timeBeginPeriod(1);
            try
            {
                while (!stop)
                {
                    bool ran = false;
                    try { ran = Session(); failures = 0; }
                    catch (Exception ex)
                    {
                        failures++;
                        Log.Write("Sound mode problem (attempt " + failures + ")", ex);
                        if (failures == 5)
                        {
                            var failed = Failed;
                            if (failed != null) failed();
                            return;
                        }
                    }
                    for (int i = 0; i < (ran ? 5 : 10) && !stop; i++) Thread.Sleep(100);
                }
            }
            finally
            {
                Restore();
                timeEndPeriod(1);
            }
        }

        void TakeOver()
        {
            float level;
            if (!AppVolume.TryGet(out level)) return;
            if (!tookOver && level > Tiny * 2) Volume = level;   // first time: keep the loudness the user had
            if (level > Tiny * 1.5f) AppVolume.Set(Tiny);
            tookOver = true;
        }

        void Restore()
        {
            if (!tookOver) return;
            try { if (AppVolume.Set(Volume) || !YandexRunning()) tookOver = false; }
            catch (Exception ex) { Log.Write("Could not put Yandex Music's volume back", ex); }
        }

        // One capture-and-play run. Returns false when there was nothing to do (Yandex Music not running).
        bool Session()
        {
            uint root = YandexRoot();
            if (root == 0) { Restore(); return false; }

            IAudioClient capClient = null, renClient = null;
            try
            {
                capClient = OpenLoopback(root);
                IntPtr format = StereoFloat();
                try
                {
                    // Loopback of one app's audio, converted to 48 kHz float stereo for us
                    Check(capClient.Initialize(0, 0x00020000 | unchecked((int)0x80000000), 2000000, 0, format, IntPtr.Zero), "capture setup");
                    string deviceId;
                    renClient = OpenDefaultOutput(out deviceId);
                    // 200 ms output buffer (only a small part is kept filled); Windows converts our format to the speakers'
                    Check(renClient.Initialize(0, unchecked((int)0x80000000) | 0x08000000, 2000000, 0, format, IntPtr.Zero), "output setup");
                    var capture = (IAudioCaptureClient)Service(capClient, typeof(IAudioCaptureClient).GUID);
                    var render = (IAudioRenderClient)Service(renClient, typeof(IAudioRenderClient).GUID);
                    uint renFrames;
                    Check(renClient.GetBufferSize(out renFrames), "output size");

                    TakeOver();
                    Check(capClient.Start(), "capture start");
                    Check(renClient.Start(), "output start");

                    // Slow checks live on their own thread: when they ran on the audio thread they held it
                    // up for up to 80 ms at a time, and the music dropped out
                    restart = false;
                    var watcher = new Thread(delegate() { Watch(root, deviceId); }) { IsBackground = true, Name = "Sound checks" };
                    watcher.SetApartmentState(ApartmentState.MTA);
                    watcher.Start();
                    try { Pump(capture, render, renClient, renFrames); }
                    finally { restart = true; watcher.Join(2000); }
                    return true;
                }
                finally { Marshal.FreeHGlobal(format); }
            }
            finally
            {
                if (capClient != null) { capClient.Stop(); Marshal.ReleaseComObject(capClient); }
                if (renClient != null) { renClient.Stop(); Marshal.ReleaseComObject(renClient); }
                // Between runs (device switch, Yandex Music restarting) the original plays as usual
                Restore();
            }
        }

        volatile bool restart;   // set by the checks when this run should end

        // Every quarter second: keep new Yandex Music streams turned down, and notice when Yandex Music
        // closes or the speakers change
        void Watch(uint root, string deviceId)
        {
            while (!stop && !restart)
            {
                try
                {
                    TakeOver();
                    if (!Alive(root) || DefaultOutputId() != deviceId) restart = true;
                }
                catch (Exception ex) { Log.Write("Sound mode check failed", ex); }
                for (int i = 0; i < 5 && !stop && !restart; i++) Thread.Sleep(50);
            }
        }

        void Pump(IAudioCaptureClient capture, IAudioRenderClient render, IAudioClient renClient, uint renFrames)
        {
            var ring = new float[RingFrames * 2];
            int head = 0, count = 0;           // ring write position and frames waiting, in frames
            var packet = new float[Rate * 2];
            int effect = -1;
            Processor fx = null;
            var limiter = new Limiter();
            const int Reserve = Rate * 60 / 1000;     // 60 ms kept queued for the speakers
            bool primed = false;

            while (!stop && !restart)
            {
                if (Effect != effect) { effect = Effect; fx = Processor.For(effect); }

                // Collect what Yandex Music played since last time
                uint next;
                Check(capture.GetNextPacketSize(out next), "capture read");
                while (next > 0)
                {
                    IntPtr data; uint frames, flags; ulong devPos, qpc;
                    Check(capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc), "capture buffer");
                    int n = (int)frames;
                    if ((flags & 2) != 0) Array.Clear(packet, 0, n * 2);       // silent packet
                    else Marshal.Copy(data, packet, 0, n * 2);
                    capture.ReleaseBuffer(frames);

                    float gain = 1f / Tiny;
                    float loudest = 0;
                    for (int i = 0; i < n * 2; i++)
                    {
                        float v = packet[i] * gain;
                        packet[i] = v;
                        if (v > loudest) loudest = v; else if (-v > loudest) loudest = -v;
                    }
                    // A stream that was not turned down yet would come out 80 dB too loud: drop it until the
                    // checks turn it down (within a quarter second)
                    if (loudest > 2f) Array.Clear(packet, 0, n * 2);

                    float volume = Volume;
                    for (int i = 0; i < n; i++)
                    {
                        float l = packet[2 * i], r = packet[2 * i + 1];
                        fx.Run(ref l, ref r);
                        limiter.Run(ref l, ref r);
                        int at = ((head + i) % RingFrames) * 2;
                        ring[at] = l * volume;
                        ring[at + 1] = r * volume;
                    }
                    head = (head + n) % RingFrames;
                    count = Math.Min(count + n, RingFrames);
                    Check(capture.GetNextPacketSize(out next), "capture read");
                }

                // Never fall more than 0.3 s behind the music
                if (count > Rate * 3 / 10) count = Reserve;

                uint padding;
                Check(renClient.GetCurrentPadding(out padding), "output fill");
                // Start (and restart after running dry) only once a reserve is queued, so small hiccups
                // are absorbed instead of heard
                if (padding == 0) primed = false;
                if (!primed && count >= Reserve) primed = true;
                int space = (int)(renFrames - padding);
                int write = primed ? Math.Min(space, count) : 0;
                if (write > 0)
                {
                    IntPtr dest;
                    Check(render.GetBuffer((uint)write, out dest), "output buffer");
                    int start = ((head - count) % RingFrames + RingFrames) % RingFrames;
                    int first = Math.Min(write, RingFrames - start);
                    Marshal.Copy(ring, start * 2, dest, first * 2);
                    if (write > first) Marshal.Copy(ring, 0, dest + first * 8, (write - first) * 2);
                    render.ReleaseBuffer((uint)write, 0);
                    count -= write;
                }
                Thread.Sleep(3);
            }
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException("Sound mode: " + what + " failed", hr);
        }

        static object Service(IAudioClient client, Guid iid)
        {
            object service;
            Check(client.GetService(ref iid, out service), "service");
            return service;
        }

        static IntPtr StereoFloat()
        {
            IntPtr f = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(f, 0, 3);              // IEEE float
            Marshal.WriteInt16(f, 2, 2);              // stereo
            Marshal.WriteInt32(f, 4, Rate);
            Marshal.WriteInt32(f, 8, Rate * 8);
            Marshal.WriteInt16(f, 12, 8);
            Marshal.WriteInt16(f, 14, 32);
            Marshal.WriteInt16(f, 16, 0);
            return f;
        }

        // ---- finding Yandex Music ------------------------------------------

        static bool YandexRunning() { return YandexRoot() != 0; }

        static bool Alive(uint pid)
        {
            try { return !Process.GetProcessById((int)pid).HasExited; }
            catch (ArgumentException) { return false; }
        }

        // The app's main process: the one whose parent is not Yandex Music itself.
        // Capturing it with its whole process tree picks up the helper process that actually plays audio.
        static uint YandexRoot()
        {
            var parents = new Dictionary<uint, uint>();
            var names = new Dictionary<uint, string>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == new IntPtr(-1)) return 0;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    parents[e.th32ProcessID] = e.th32ParentProcessID;
                    names[e.th32ProcessID] = e.szExeFile;
                }
            }
            finally { CloseHandle(snap); }

            foreach (var p in names)
            {
                if (!AppVolume.IsYandexName(System.IO.Path.GetFileNameWithoutExtension(p.Value))) continue;
                uint parent;
                string parentName;
                bool childOfYandex = parents.TryGetValue(p.Key, out parent) && names.TryGetValue(parent, out parentName)
                    && AppVolume.IsYandexName(System.IO.Path.GetFileNameWithoutExtension(parentName));
                if (!childOfYandex) return p.Key;
            }
            return 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        // ---- Windows audio interfaces ----------------------------------------

        static IAudioClient OpenLoopback(uint pid)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, pid, INCLUDE_TARGET_PROCESS_TREE } in a VT_BLOB PROPVARIANT
            IntPtr args = Marshal.AllocHGlobal(12);
            IntPtr variant = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(args, 0, 1);
                Marshal.WriteInt32(args, 4, (int)pid);
                Marshal.WriteInt32(args, 8, 0);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
                Marshal.WriteInt16(variant, 0, 65);
                Marshal.WriteInt32(variant, 8, 12);
                Marshal.WriteIntPtr(variant, IntPtr.Size == 8 ? 16 : 12, args);

                var done = new Activation();
                IActivateAudioInterfaceAsyncOperation op;
                Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, done, out op), "loopback request");
                if (!done.Finished.WaitOne(5000)) throw new TimeoutException("Sound mode: loopback request timed out");
                Check(done.Result, "loopback activation");
                return (IAudioClient)done.Client;
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(args);
            }
        }

        static IAudioClient OpenDefaultOutput(out string id)
        {
            var devices = NewEnumerator();
            IMMDevice device;
            Check(devices.GetDefaultAudioEndpoint(0, 1, out device), "find speakers");
            Check(device.GetId(out id), "speaker id");
            Guid iid = typeof(IAudioClient).GUID;
            object client;
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out client), "open speakers");
            return (IAudioClient)client;
        }

        static string DefaultOutputId()
        {
            try
            {
                var devices = NewEnumerator();
                IMMDevice device;
                string id;
                if (devices.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device.GetId(out id) != 0) return null;
                return id;
            }
            catch (COMException) { return null; }
        }

        class Activation : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);
            public int Result;
            public object Client;
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try { operation.GetActivateResult(out Result, out Client); }
                catch (Exception ex) { Result = Marshal.GetHRForException(ex); }
                Finished.Set();
            }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
            [MarshalAs(UnmanagedType.LPStruct)] Guid iid, IntPtr parameters,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

        // Created by class id: a second [ComImport] class for the same id in this assembly would clash with
        // the one the volume code uses, and the cast would fail at run time
        static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }

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
            void OpenPropertyStore();
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        }

        // Marks the completion handler as callable from any thread, which the activation call requires
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAgileObject { }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
        }
    }

    // ---- the effects ---------------------------------------------------------

    abstract class Processor
    {
        public abstract void Run(ref float l, ref float r);

        public static Processor For(int effect)
        {
            if (effect == Effects.Club) return new Club();
            return new Plain();
        }
    }

    class Plain : Processor
    {
        public override void Run(ref float l, ref float r) { }
    }

    // Modelled on the Telegram copy of "Больше, чем ближе": both channels summed to mono, and a strong
    // sub-bass lift centred near 38 Hz (it measured about +20 dB at 31-40 Hz relative to 1 kHz).
    class Club : Processor
    {
        readonly Biquad sub = Biquad.Peak(38, 1.1, 9);
        readonly Biquad low = Biquad.LowShelf(150, 3);

        public override void Run(ref float l, ref float r)
        {
            double m = (l + r) * 0.25;            // mono, with 6 dB of room for the boost
            m = low.Run(sub.Run(m)) * 1.6;        // limiter afterwards catches the peaks
            l = r = (float)m;
        }
    }

    // Standard "Audio EQ Cookbook" filters (Robert Bristow-Johnson)
    class Biquad
    {
        double b0, b1, b2, a1, a2, x1, x2, y1, y2;

        public static Biquad Peak(double freq, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * freq / 48000, alpha = Math.Sin(w) / (2 * q), cos = Math.Cos(w);
            return Make(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
        }

        public static Biquad LowShelf(double freq, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * freq / 48000, cos = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt(2), s = 2 * Math.Sqrt(a) * alpha;
            return Make(a * ((a + 1) - (a - 1) * cos + s), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - s),
                (a + 1) + (a - 1) * cos + s, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - s);
        }

        static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            return new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };
        }

        public double Run(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x;
            y2 = y1; y1 = Math.Abs(y) < 1e-20 ? 0 : y;
            return y;
        }
    }

    // Keeps the boosted signal under full scale: instant turn-down, slow recovery
    class Limiter
    {
        const float Ceiling = 0.95f;
        const float Recovery = 0.99997f;     // about 0.7 s back to full level
        float gain = 1f;

        public void Run(ref float l, ref float r)
        {
            float peak = Math.Max(Math.Abs(l), Math.Abs(r));
            if (peak * gain > Ceiling) gain = Ceiling / peak;
            l *= gain;
            r *= gain;
            gain = 1f - (1f - gain) * Recovery;
            if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
            if (r > 1f) r = 1f; else if (r < -1f) r = -1f;
        }
    }
}
