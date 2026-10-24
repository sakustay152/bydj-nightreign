using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BydjNightreign
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

        [DllImport("kernel32.dll")]
        public static extern uint SuspendThread(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern uint ResumeThread(IntPtr h);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        public static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        public static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int index);

        public static float SystemDpiScale()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int dpi = 96;
            if (dc != IntPtr.Zero)
            {
                dpi = GetDeviceCaps(dc, 88);
                ReleaseDC(IntPtr.Zero, dc);
            }
            if (dpi < 96) dpi = 96;
            return dpi / 96f;
        }
    }

    internal sealed class ModConfig
    {
        public float Volume = 28f;
        public float MaxStretch = 0.30f;
        public float Poisson = 0.5f;
        public float Bpm = 133.3333f;
        public string MusicFile = "邦友的酒.wav";
        public bool TopMost = true;

        public static ModConfig Load(string path)
        {
            ModConfig c = new ModConfig();
            try
            {
                if (!File.Exists(path)) return c;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "volume") c.Volume = ParseFloat(v, c.Volume);
                    else if (k == "maxstretch") c.MaxStretch = ParseFloat(v, c.MaxStretch);
                    else if (k == "poisson") c.Poisson = ParseFloat(v, c.Poisson);
                    else if (k == "bpm") c.Bpm = ParseFloat(v, c.Bpm);
                    else if (k == "musicfile") c.MusicFile = v;
                    else if (k == "topmost") c.TopMost = ParseBool(v, c.TopMost);
                }
            }
            catch { }
            c.Volume = Clamp(c.Volume, 0f, 100f);
            c.MaxStretch = Clamp(c.MaxStretch, 0.05f, 0.60f);
            c.Poisson = Clamp(c.Poisson, 0f, 0.9f);
            if (c.Bpm < 40f) c.Bpm = 40f;
            if (c.Bpm > 300f) c.Bpm = 300f;
            return c;
        }

        public void Save(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 邦友的酒 - 黑夜君临版");
                sb.AppendLine("Volume=" + Volume.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("MaxStretch=" + MaxStretch.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("Poisson=" + Poisson.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("Bpm=" + Bpm.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("MusicFile=" + MusicFile);
                sb.AppendLine("TopMost=" + (TopMost ? "1" : "0"));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static float ParseFloat(string s, float fallback)
        {
            float v;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static bool ParseBool(string s, bool fallback)
        {
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }

    internal sealed class WavPlayer : IDisposable
    {
        private const uint WaveMapper = 0xFFFFFFFF;
        private const uint CallbackNull = 0;
        private const uint WhdrDone = 0x1;
        private const int HdrSize = 48;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hwo, uint device, ref WaveFormatEx fmt, IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        private sealed class Buf
        {
            public IntPtr Data;
            public IntPtr Hdr;
            public byte[] Staging;
            public bool Queued;
        }

        private readonly object lifeLock = new object();
        private byte[] wav;
        private int dataOff;
        private int dataLen;
        private int blockAlign;
        private int sampleRate;
        private int channels;
        private int cursor;
        private IntPtr hwo = IntPtr.Zero;
        private Buf[] buffers;
        private Thread thread;
        private volatile bool running;
        private volatile float targetVolume = 0.28f;
        private float gain;
        private string lastError;

        public string LastError { get { return lastError; } }
        public bool IsReady { get { return wav != null; } }
        public bool IsPlaying { get { return running; } }
        public double PositionSeconds
        {
            get
            {
                if (wav == null || blockAlign <= 0 || sampleRate <= 0) return 0;
                return (double)cursor / blockAlign / sampleRate;
            }
        }
        public double LengthSeconds
        {
            get
            {
                if (wav == null || blockAlign <= 0 || sampleRate <= 0) return 0;
                return (double)dataLen / blockAlign / sampleRate;
            }
        }

        public float Volume
        {
            get { return targetVolume; }
            set
            {
                if (value < 0f) value = 0f;
                if (value > 1f) value = 1f;
                targetVolume = value;
            }
        }

        public bool Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    lastError = "音乐文件不存在：" + path;
                    return false;
                }
                byte[] b = File.ReadAllBytes(path);
                if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
                    throw new InvalidDataException("不是有效的 WAV 文件");

                int fmtOff = -1;
                int fmtSize = 0;
                int p = 12;
                dataOff = -1;
                dataLen = 0;
                while (p + 8 <= b.Length)
                {
                    string id = Encoding.ASCII.GetString(b, p, 4);
                    int sz = BitConverter.ToInt32(b, p + 4);
                    int body = p + 8;
                    if (sz < 0 || (long)body + sz > b.Length) break;
                    if (id == "fmt ")
                    {
                        fmtOff = body;
                        fmtSize = sz;
                    }
                    else if (id == "data" && dataOff < 0)
                    {
                        dataOff = body;
                        dataLen = sz;
                    }
                    p = body + sz + (sz & 1);
                    if (fmtOff >= 0 && dataOff >= 0) break;
                }
                if (fmtOff < 0 || dataOff < 0) throw new InvalidDataException("WAV 缺少 fmt/data 块");
                if (fmtSize < 16) throw new InvalidDataException("WAV fmt 块过小");

                ushort tag = BitConverter.ToUInt16(b, fmtOff);
                channels = BitConverter.ToUInt16(b, fmtOff + 2);
                sampleRate = (int)BitConverter.ToUInt32(b, fmtOff + 4);
                blockAlign = BitConverter.ToUInt16(b, fmtOff + 12);
                ushort bits = BitConverter.ToUInt16(b, fmtOff + 14);
                if (tag != 1 || bits != 16)
                    throw new InvalidDataException("仅支持 16-bit PCM WAV（当前格式=" + tag + "，" + bits + " bit）");
                if (channels < 1 || channels > 2 || sampleRate < 8000 || blockAlign != channels * 2)
                    throw new InvalidDataException("WAV 声道/采样率不受支持");
                dataLen -= dataLen % blockAlign;
                if (dataLen <= 0) throw new InvalidDataException("WAV 没有音频数据");

                wav = b;
                cursor = 0;
                lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                wav = null;
                lastError = "载入音乐失败：" + ex.Message;
                return false;
            }
        }

        public bool Start()
        {
            lock (lifeLock)
            {
                if (wav == null) return false;
                if (hwo != IntPtr.Zero) return true;

                WaveFormatEx fmt = new WaveFormatEx();
                fmt.wFormatTag = 1;
                fmt.nChannels = (ushort)channels;
                fmt.nSamplesPerSec = (uint)sampleRate;
                fmt.nAvgBytesPerSec = (uint)(sampleRate * blockAlign);
                fmt.nBlockAlign = (ushort)blockAlign;
                fmt.wBitsPerSample = 16;
                fmt.cbSize = 0;

                IntPtr h;
                int rc = waveOutOpen(out h, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, CallbackNull);
                if (rc != 0)
                {
                    lastError = "打开音频设备失败（waveOut 错误 " + rc + "）";
                    return false;
                }
                hwo = h;
                gain = 0f;
                int bytesPerBuf = sampleRate / 4 * blockAlign;
                if (bytesPerBuf < 4096) bytesPerBuf = 4096;

                try
                {
                    buffers = new Buf[3];
                    for (int i = 0; i < buffers.Length; i++)
                    {
                        Buf b = new Buf();
                        b.Data = Marshal.AllocHGlobal(bytesPerBuf);
                        b.Hdr = Marshal.AllocHGlobal(HdrSize);
                        b.Staging = new byte[bytesPerBuf];
                        buffers[i] = b;
                        QueueBuffer(b, true);
                    }
                }
                catch (Exception ex)
                {
                    lastError = "初始化音频缓冲失败：" + ex.Message;
                    CloseDevice();
                    return false;
                }

                running = true;
                thread = new Thread(AudioLoop);
                thread.IsBackground = true;
                thread.Name = "bydj-audio";
                thread.Start();
                lastError = null;
                return true;
            }
        }

        private void QueueBuffer(Buf b, bool first)
        {
            if (!first)
            {
                waveOutUnprepareHeader(hwo, b.Hdr, HdrSize);
            }

            WaveHdr h = new WaveHdr();
            h.lpData = b.Data;
            h.dwBufferLength = (uint)b.Staging.Length;
            h.dwFlags = 0;
            Marshal.StructureToPtr(h, b.Hdr, false);

            int rc = waveOutPrepareHeader(hwo, b.Hdr, HdrSize);
            if (rc != 0) throw new InvalidOperationException("waveOutPrepareHeader 错误 " + rc);

            Fill(b.Staging);
            Marshal.Copy(b.Staging, 0, b.Data, b.Staging.Length);

            rc = waveOutWrite(hwo, b.Hdr, HdrSize);
            if (rc != 0) throw new InvalidOperationException("waveOutWrite 错误 " + rc);
            b.Queued = true;
        }

        private void AudioLoop()
        {
            try
            {
                while (running)
                {
                    bool wrote = false;
                    for (int i = 0; i < buffers.Length && running; i++)
                    {
                        Buf b = buffers[i];
                        if (!b.Queued) continue;
                        WaveHdr h = (WaveHdr)Marshal.PtrToStructure(b.Hdr, typeof(WaveHdr));
                        if ((h.dwFlags & WhdrDone) == 0) continue;
                        QueueBuffer(b, false);
                        wrote = true;
                    }
                    if (!wrote) Thread.Sleep(8);
                }
            }
            catch (Exception ex)
            {
                lastError = "播放中断：" + ex.Message;
                running = false;
            }
        }

        private void Fill(byte[] dst)
        {
            int frame = blockAlign;
            int frames = dst.Length / frame;
            float step = 1f / Math.Max(1f, sampleRate * 0.18f);
            int pos = 0;
            for (int f = 0; f < frames; f++)
            {
                if (cursor + frame > dataLen) cursor = 0;
                float tv = targetVolume;
                if (gain < tv) { gain += step; if (gain > tv) gain = tv; }
                else if (gain > tv) { gain -= step; if (gain < tv) gain = tv; }
                for (int b = 0; b < frame; b += 2)
                {
                    short s = (short)(wav[dataOff + cursor + b] | (wav[dataOff + cursor + b + 1] << 8));
                    int v = (int)(s * gain);
                    if (v > 32767) v = 32767;
                    if (v < -32768) v = -32768;
                    dst[pos + b] = (byte)(v & 0xFF);
                    dst[pos + b + 1] = (byte)((v >> 8) & 0xFF);
                }
                cursor += frame;
                pos += frame;
            }
        }

        public void Stop()
        {
            lock (lifeLock)
            {
                running = false;
                Thread t = thread;
                thread = null;
                if (t != null)
                {
                    try { t.Join(500); } catch { }
                }
                CloseDevice();
            }
        }

        private void CloseDevice()
        {
            if (hwo != IntPtr.Zero)
            {
                try { waveOutReset(hwo); } catch { }
                if (buffers != null)
                {
                    for (int i = 0; i < buffers.Length; i++)
                    {
                        Buf b = buffers[i];
                        if (b == null) continue;
                        try { waveOutUnprepareHeader(hwo, b.Hdr, HdrSize); } catch { }
                        if (b.Data != IntPtr.Zero) { Marshal.FreeHGlobal(b.Data); b.Data = IntPtr.Zero; }
                        if (b.Hdr != IntPtr.Zero) { Marshal.FreeHGlobal(b.Hdr); b.Hdr = IntPtr.Zero; }
                    }
                }
                try { waveOutClose(hwo); } catch { }
                hwo = IntPtr.Zero;
            }
            buffers = null;
        }

        public void Dispose()
        {
            Stop();
            wav = null;
        }
    }

    internal sealed class MainForm : Form
    {
        private const int HotkeyToggle = 1;
        private const int HotkeyRestore = 2;
        private const uint ModNoRepeat = 0x4000;
        private const int WmHotkey = 0x0312;

        private readonly string exeDir;
        private readonly string iniPath;
        private ModConfig cfg;
        private WavPlayer player;

        private volatile GameMemory mem;
        private volatile string connectError;
        private volatile bool active;
        private volatile bool scaleRunning;
        private volatile bool appRunning;
        private Thread scaleThread;
        private Thread attachThread;

        private Label statusLabel;
        private Label detailLabel;
        private Label hintLabel;
        private Button toggleButton;
        private Button restoreButton;
        private Button reconnectButton;
        private TrackBar volumeTrack;
        private TrackBar strengthTrack;
        private Label volumeLabel;
        private Label strengthLabel;
        private CheckBox topMostCheck;
        private System.Windows.Forms.Timer uiTimer;
        private string detailText = "";
        private bool hotkeysOk;

        public MainForm()
        {
            exeDir = AppDomain.CurrentDomain.BaseDirectory;
            iniPath = Path.Combine(exeDir, "BydjNightreign.ini");
            cfg = ModConfig.Load(iniPath);

            AutoScaleMode = AutoScaleMode.None;
            Text = "邦友的酒 · 黑夜君临（全员版）";
            ClientSize = new Size(480, 364);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = cfg.TopMost;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.Gainsboro;

            Label title = new Label();
            title.Text = "邦友的酒  Nightreign 全员版";
            title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(240, 214, 140);
            title.AutoSize = true;
            title.Location = new Point(16, 12);
            Controls.Add(title);

            statusLabel = new Label();
            statusLabel.Location = new Point(16, 48);
            statusLabel.Size = new Size(448, 26);
            statusLabel.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            statusLabel.ForeColor = Color.FromArgb(120, 200, 255);
            statusLabel.Text = "未连接：等待游戏进程…";
            Controls.Add(statusLabel);

            detailLabel = new Label();
            detailLabel.Location = new Point(16, 76);
            detailLabel.Size = new Size(448, 34);
            detailLabel.ForeColor = Color.FromArgb(170, 170, 175);
            Controls.Add(detailLabel);

            Label warn = new Label();
            warn.Location = new Point(16, 110);
            warn.Size = new Size(448, 36);
            warn.ForeColor = Color.FromArgb(235, 120, 110);
            warn.Text = "仅限离线模式（ME3 / 无 EAC）。切勿在 EAC 在线模式使用，\n在线使用会导致封号。";
            Controls.Add(warn);

            toggleButton = new Button();
            toggleButton.Text = "开启 (F6)";
            toggleButton.Location = new Point(16, 152);
            toggleButton.Size = new Size(136, 36);
            toggleButton.Click += delegate { Toggle(false); };
            StyleButton(toggleButton);
            Controls.Add(toggleButton);

            restoreButton = new Button();
            restoreButton.Text = "恢复 (F7)";
            restoreButton.Location = new Point(160, 152);
            restoreButton.Size = new Size(136, 36);
            restoreButton.Click += delegate { StopAndRestore(); };
            StyleButton(restoreButton);
            Controls.Add(restoreButton);

            reconnectButton = new Button();
            reconnectButton.Text = "重新连接";
            reconnectButton.Location = new Point(304, 152);
            reconnectButton.Size = new Size(100, 36);
            reconnectButton.Click += delegate { Reconnect(); };
            StyleButton(reconnectButton);
            Controls.Add(reconnectButton);

            volumeLabel = new Label();
            volumeLabel.Location = new Point(16, 204);
            volumeLabel.Size = new Size(90, 24);
            volumeLabel.Text = "音量 28%";
            Controls.Add(volumeLabel);

            volumeTrack = new TrackBar();
            volumeTrack.Minimum = 0;
            volumeTrack.Maximum = 100;
            volumeTrack.TickFrequency = 10;
            volumeTrack.Value = (int)Math.Round(cfg.Volume);
            volumeTrack.Location = new Point(104, 198);
            volumeTrack.Size = new Size(352, 45);
            volumeTrack.ValueChanged += delegate
            {
                volumeLabel.Text = "音量 " + volumeTrack.Value + "%";
                if (player != null) player.Volume = volumeTrack.Value / 100f;
                cfg.Volume = volumeTrack.Value;
            };
            Controls.Add(volumeTrack);

            strengthLabel = new Label();
            strengthLabel.Location = new Point(16, 252);
            strengthLabel.Size = new Size(90, 24);
            strengthLabel.Text = "强度 " + (int)Math.Round(cfg.MaxStretch * 100) + "%";
            Controls.Add(strengthLabel);

            strengthTrack = new TrackBar();
            strengthTrack.Minimum = 5;
            strengthTrack.Maximum = 60;
            strengthTrack.TickFrequency = 5;
            strengthTrack.Value = (int)Math.Round(cfg.MaxStretch * 100);
            strengthTrack.Location = new Point(104, 246);
            strengthTrack.Size = new Size(352, 45);
            strengthTrack.ValueChanged += delegate
            {
                strengthLabel.Text = "强度 " + strengthTrack.Value + "%";
                cfg.MaxStretch = strengthTrack.Value / 100f;
            };
            Controls.Add(strengthTrack);

            topMostCheck = new CheckBox();
            topMostCheck.Text = "窗口置顶";
            topMostCheck.Checked = cfg.TopMost;
            topMostCheck.Location = new Point(16, 296);
            topMostCheck.Size = new Size(110, 24);
            topMostCheck.ForeColor = Color.Gainsboro;
            topMostCheck.CheckedChanged += delegate
            {
                TopMost = topMostCheck.Checked;
                cfg.TopMost = topMostCheck.Checked;
            };
            Controls.Add(topMostCheck);

            hintLabel = new Label();
            hintLabel.Location = new Point(132, 292);
            hintLabel.Size = new Size(332, 48);
            hintLabel.ForeColor = Color.FromArgb(150, 150, 155);
            hintLabel.Text = "F6 开关音乐与全员弹跳 · F7 停止并恢复\n仅限 ME3 离线。布料会随体型同步缩放。";
            Controls.Add(hintLabel);

            player = new WavPlayer();
            player.Volume = cfg.Volume / 100f;
            string musicPath = Path.IsPathRooted(cfg.MusicFile) ? cfg.MusicFile : Path.Combine(exeDir, cfg.MusicFile);
            if (!player.Load(musicPath))
            {
                detailText = player.LastError;
            }

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += delegate { UpdateUi(); };

            FormClosing += OnFormClosing;
            ScaleForDpi();
        }

        private void ScaleForDpi()
        {
            float s = Native.SystemDpiScale();
            if (s <= 1.01f) return;
            ClientSize = new Size((int)Math.Round(ClientSize.Width * s), (int)Math.Round(ClientSize.Height * s));
            foreach (Control c in Controls) ScaleControlLayout(c, s);
        }

        private static void ScaleControlLayout(Control c, float s)
        {
            c.Location = new Point((int)Math.Round(c.Left * s), (int)Math.Round(c.Top * s));
            c.Size = new Size((int)Math.Round(c.Width * s), (int)Math.Round(c.Height * s));
            foreach (Control child in c.Controls) ScaleControlLayout(child, s);
        }

        private static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(46, 46, 54);
            b.ForeColor = Color.Gainsboro;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 102);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 62, 74);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 80, 94);
        }

        private static string FormatTime(double seconds)
        {
            int total = (int)seconds;
            if (total < 0) total = 0;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            bool a = Native.RegisterHotKey(Handle, HotkeyToggle, ModNoRepeat, (uint)Keys.F6);
            bool b = Native.RegisterHotKey(Handle, HotkeyRestore, ModNoRepeat, (uint)Keys.F7);
            hotkeysOk = a && b;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyToggle) { Toggle(true); return; }
                if (id == HotkeyRestore) { StopAndRestore(); return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.timeBeginPeriod(1);
            appRunning = true;
            scaleRunning = true;

            attachThread = new Thread(AttachLoop);
            attachThread.IsBackground = true;
            attachThread.Name = "bydj-attach";
            attachThread.Start();

            scaleThread = new Thread(ScaleLoop);
            scaleThread.IsBackground = true;
            scaleThread.Name = "bydj-scale";
            scaleThread.Start();

            uiTimer.Start();
            if (!hotkeysOk) detailText = "F6/F7 全局热键注册失败，请使用窗口按钮。";
        }

        private void AttachLoop()
        {
            while (appRunning)
            {
                try
                {
                    GameMemory m = mem;
                    if (m != null)
                    {
                        if (m.HasExited)
                        {
                            mem = null;
                            m.Dispose();
                        }
                    }
                    else
                    {
                        GameMemory candidate = new GameMemory();
                        if (candidate.Connect())
                        {
                            if (!appRunning)
                            {
                                candidate.Dispose();
                                break;
                            }
                            mem = candidate;
                            connectError = null;
                        }
                        else
                        {
                            connectError = candidate.LastError;
                            candidate.Dispose();
                        }
                    }
                }
                catch { }
                Thread.Sleep(2000);
            }
        }

        private void ScaleLoop()
        {
            Stopwatch sw = Stopwatch.StartNew();
            long lastRefresh = -100000;
            double animStart = 0;
            double nextFrame = 0;
            bool wasActive = false;

            while (scaleRunning)
            {
                try
                {
                    GameMemory m = mem;
                    if (m != null)
                    {
                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs - lastRefresh >= 500)
                        {
                            if (active) m.RefreshActors();
                            else m.Refresh();
                            lastRefresh = nowMs;
                        }

                        if (active && m.Ready)
                        {
                            double now = sw.Elapsed.TotalSeconds;
                            if (!wasActive)
                            {
                                wasActive = true;
                                animStart = now;
                                nextFrame = now;
                            }
                            if (now >= nextFrame)
                            {
                                double phase = 2.0 * Math.PI * (cfg.Bpm / 60.0) * (now - animStart);
                                m.ApplyJelly(cfg.MaxStretch, cfg.Poisson, phase);
                                nextFrame += 1.0 / 60.0;
                                if (nextFrame < now - 0.10) nextFrame = now + 1.0 / 60.0;
                            }
                        }
                        else
                        {
                            wasActive = false;
                        }
                    }
                    else
                    {
                        wasActive = false;
                    }
                }
                catch { }
                Thread.Sleep(active ? 1 : 5);
            }
        }

        private void Toggle(bool fromHotkey)
        {
            if (active)
            {
                StopAndRestore();
                return;
            }

            GameMemory m = mem;
            if (m == null || m.HasExited)
            {
                detailText = "尚未连接游戏。请先离线启动并进入游戏世界。";
                return;
            }
            m.Refresh();
            if (!m.Ready)
            {
                detailText = "游戏已连接，但角色尚未载入。请进入游戏世界后再按 F6。";
                return;
            }
            if (!m.CaptureBase() || m.ActorCount == 0)
            {
                detailText = "无法读取角色体型，请稍后再试。";
                return;
            }

            active = true;
            int count = m.ActorCount;
            string cloth = "";
            try
            {
                if (m.ApplyClothFix()) cloth = "布料同步已开启。";
                else cloth = "布料同步不可用：" + m.LastError;
            }
            catch { cloth = "布料同步不可用。"; }
            if (player != null && player.IsReady)
            {
                if (!player.Start())
                {
                    detailText = player.LastError + " " + cloth;
                }
                else
                {
                    detailText = "音乐与全员弹跳已开启（" + count + " 个角色）。" + cloth;
                }
            }
            else
            {
                detailText = "音乐不可用，仅播放全员弹跳（" + count + " 个角色）。" + cloth;
            }
        }

        private void StopAndRestore()
        {
            active = false;
            try { if (player != null) player.Stop(); } catch { }
            GameMemory m = mem;
            if (m != null)
            {
                try { m.Restore(); } catch { }
            }
            detailText = "已停止并恢复所有角色体型与布料。";
        }

        private void Reconnect()
        {
            StopAndRestore();
            GameMemory m = mem;
            mem = null;
            if (m != null) m.Dispose();
            connectError = null;
            detailText = "正在重新扫描游戏进程…";
        }

        private void UpdateUi()
        {
            GameMemory m = mem;
            if (m != null && m.HasExited)
            {
                if (active) { active = false; try { player.Stop(); } catch { } }
                m.Dispose();
                mem = null;
                m = null;
                detailText = "游戏已退出。请重新离线启动。";
            }

            if (m == null)
            {
                statusLabel.Text = "未连接：等待游戏进程…";
                if (connectError != null) detailText = connectError;
                toggleButton.Enabled = false;
            }
            else if (!m.Ready)
            {
                if (active)
                {
                    statusLabel.Text = "运行中：等待角色载入…";
                    toggleButton.Enabled = true;
                }
                else
                {
                    statusLabel.Text = "已连接：等待进入游戏世界…";
                    detailText = "进入游戏世界后会自动识别角色。 " + m.Diagnostics;
                    toggleButton.Enabled = false;
                }
            }
            else if (active)
            {
                string suffix = "";
                if (player != null && player.IsPlaying)
                    suffix = "  " + FormatTime(player.PositionSeconds) + " / " + FormatTime(player.LengthSeconds);
                statusLabel.Text = "运行中 ♪  全员弹跳（" + m.ActorCount + " 个角色）" + suffix;
                toggleButton.Enabled = true;
            }
            else
            {
                string src = m.LaunchSource;
                statusLabel.Text = src == null ? "已就绪：按 F6 开始" : "已就绪（" + src + "）：按 F6 开始";
                toggleButton.Enabled = true;
            }

            if (active && player != null && player.LastError != null) detailText = player.LastError;
            toggleButton.Text = active ? "关闭 (F6)" : "开启 (F6)";
            restoreButton.Enabled = true;
            detailLabel.Text = detailText;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            appRunning = false;
            scaleRunning = false;
            active = false;
            try { if (player != null) player.Stop(); } catch { }
            if (attachThread != null) { try { attachThread.Join(500); } catch { } }
            if (scaleThread != null) { try { scaleThread.Join(500); } catch { } }

            try
            {
                GameMemory m = mem;
                mem = null;
                if (m != null)
                {
                    m.Restore();
                    m.Dispose();
                }
            }
            catch { }

            try { Native.UnregisterHotKey(Handle, HotkeyToggle); } catch { }
            try { Native.UnregisterHotKey(Handle, HotkeyRestore); } catch { }
            try { Native.timeEndPeriod(1); } catch { }

            try
            {
                cfg.Volume = volumeTrack.Value;
                cfg.MaxStretch = strengthTrack.Value / 100f;
                cfg.TopMost = topMostCheck.Checked;
                cfg.Save(iniPath);
            }
            catch { }

            try { if (player != null) player.Dispose(); } catch { }
            try { uiTimer.Stop(); } catch { }
        }
    }
}
