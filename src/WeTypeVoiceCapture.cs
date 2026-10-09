// Read-only adapter for WeType 2.1.4.6. No hooks, input synthesis, focus APIs,
// recording APIs, code injection, or writes into the input-method process.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WeTypeVoiceCapture
{
    sealed class Frame
    {
        public int State;
        public bool Finished, CancelPending, StopPending;
        public string Session, Text;
        public ulong Manager, Engine;
        public bool Active { get { return State == 1 || State == 2; } }
    }

    sealed class Source : IDisposable
    {
        const ulong TableRva = 0xfeae58;
        const string SupportedHash = "7AAE1BD693BD1E94FD2DA9CCF577C8C92BFA88D87B719DF3B9330FA81D19C292";
        readonly Process process;
        readonly ulong imageBase;
        IntPtr handle;
        ulong bridge;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        [StructLayout(LayoutKind.Sequential)]
        struct Region
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State, Protect, Type;
        }
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] data, UIntPtr count, out UIntPtr read);
        [DllImport("kernel32.dll")]
        static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out Region region, UIntPtr length);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        public Source(Process target)
        {
            process = target;
            try
            {
                string path = target.MainModule.FileName;
                string hash;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (hash != SupportedHash)
                    throw new NotSupportedException("微信输入法版本已变化，需要重新适配");
                imageBase = (ulong)target.MainModule.BaseAddress.ToInt64();
                handle = OpenProcess(0x410, false, target.Id); // QUERY_INFORMATION | VM_READ only
                if (handle == IntPtr.Zero) throw new IOException("无法只读访问微信输入法");
                var matches = new List<ulong>();
                ulong address = 0;
                while (address < 0x800000000000UL)
                {
                    Region region;
                    if (VirtualQueryEx(handle, (IntPtr)(long)address, out region, (UIntPtr)Marshal.SizeOf(typeof(Region))) == UIntPtr.Zero) break;
                    ulong start = (ulong)region.BaseAddress.ToInt64(), size = region.RegionSize.ToUInt64();
                    if (size == 0 || start + size <= address) break;
                    address = start + size;
                    if (region.State != 0x1000 || region.Type != 0x20000 || (region.Protect & 0x101) != 0) continue;
                    for (ulong offset = 0; offset < size; offset += 1048576)
                    {
                        byte[] chunk;
                        try { chunk = Read(start + offset, (int)Math.Min(1048576UL, size - offset)); }
                        catch (IOException) { continue; }
                        for (int i = 0; i + 8 <= chunk.Length; i += 8)
                        {
                            if (BitConverter.ToUInt64(chunk, i) != imageBase + TableRva) continue;
                            bridge = start + offset + (ulong)i;
                            try { Snapshot(); matches.Add(bridge); }
                            catch (IOException) { }
                            catch (DecoderFallbackException) { }
                        }
                    }
                }
                if (matches.Count != 1) throw new IOException("暂时无法定位唯一的语音结果，请稍后重试");
                bridge = matches[0];
            }
            catch { Dispose(); throw; }
        }

        byte[] Read(ulong address, int count)
        {
            if (address < 0x10000 || address >= 0x800000000000UL || count < 0 || count > 1048576)
                throw new IOException("读取范围无效");
            byte[] bytes = new byte[count];
            UIntPtr read;
            if (!ReadProcessMemory(handle, (IntPtr)(long)address, bytes, (UIntPtr)count, out read) || read.ToUInt64() != (ulong)count)
                throw new IOException("语音数据正在变化");
            return bytes;
        }
        ulong U64(ulong address) { return BitConverter.ToUInt64(Read(address, 8), 0); }
        int I32(ulong address) { return BitConverter.ToInt32(Read(address, 4), 0); }
        bool Flag(ulong address)
        {
            byte b = Read(address, 1)[0];
            if (b > 1) throw new IOException("语音状态无效");
            return b == 1;
        }
        string String(ulong address)
        {
            byte[] before = Read(address, 32);
            ulong length = BitConverter.ToUInt64(before, 16), capacity = BitConverter.ToUInt64(before, 24);
            if (length > capacity || length > 65536 || capacity > 1000000) throw new IOException("语音文本长度无效");
            ulong pointer = capacity <= 15 ? address : BitConverter.ToUInt64(before, 0);
            byte[] text = length == 0 ? new byte[0] : Read(pointer, (int)length);
            if (!before.SequenceEqual(Read(address, 32)) || (length > 0 && !text.SequenceEqual(Read(pointer, (int)length))))
                throw new IOException("语音文本尚未稳定");
            string value = StrictUtf8.GetString(text);
            if (value.IndexOf('\0') >= 0) throw new IOException("语音文本包含无效字符");
            return value;
        }

        public Frame Snapshot()
        {
            if (process.HasExited) throw new IOException("微信输入法已退出");
            if (U64(bridge) != imageBase + TableRva) throw new IOException("语音对象已变化");
            ulong manager = U64(bridge + 0x158), engine = U64(manager + 0x78);
            int state = I32(engine + 0x48);
            if (state < 0 || state > 3) throw new IOException("未知语音状态");
            string id = String(manager + 0xb08);
            var frame = new Frame {
                State = state, Session = id, Manager = manager, Engine = engine,
                Finished = Flag(manager + 0xab0), CancelPending = Flag(bridge + 0x6a8),
                StopPending = Flag(bridge + 0x6b0), Text = state == 3 ? String(engine + 0x100) : ""
            };
            if (U64(bridge + 0x158) != manager || U64(manager + 0x78) != engine ||
                I32(engine + 0x48) != state || String(manager + 0xb08) != id)
                throw new IOException("语音轮次正在变化");
            if (Flag(bridge + 0x6a8)) frame.CancelPending = true;
            return frame;
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
            process.Dispose();
        }
    }

    sealed class Capture : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly Icon trayIcon;
        readonly ToolStripMenuItem stateItem, pauseItem;
        readonly System.Windows.Forms.Timer timer;
        readonly string statusFile;
        Source source;
        Frame previous;
        bool paused, armed, seenStop, canceled;
        string candidate, lastStatus;
        DateTime stableSince, nextAttach, errorSince = DateTime.MinValue;
        int copyCount;

        public Capture()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeTypeVoiceCapture");
            Directory.CreateDirectory(folder);
            statusFile = Path.Combine(folder, "status.txt");
            var menu = new ContextMenuStrip();
            stateItem = new ToolStripMenuItem("正在连接微信输入法") { Enabled = false };
            pauseItem = new ToolStripMenuItem("暂停复制");
            pauseItem.Click += delegate {
                paused = !paused; pauseItem.Text = paused ? "恢复复制" : "暂停复制";
                armed = false; candidate = null; previous = null;
                Status(paused ? "已暂停复制" : "已恢复，等待下一轮语音");
            };
            menu.Items.Add(stateItem); menu.Items.Add(pauseItem); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            using (var stream = typeof(Capture).Assembly.GetManifestResourceStream("WeTypeVoiceCapture.AppIcon.ico"))
            using (var icon = new Icon(stream, SystemInformation.SmallIconSize))
                trayIcon = (Icon)icon.Clone();
            tray = new NotifyIcon { Icon = trayIcon, Text = "微信语音复制", ContextMenuStrip = menu, Visible = true };
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += Tick; timer.Start();
            Status("正在连接微信输入法");
        }

        void Status(string value)
        {
            if (value == lastStatus) return;
            lastStatus = value; stateItem.Text = value;
            tray.Text = ("微信语音复制：" + value).Substring(0, Math.Min(63, ("微信语音复制：" + value).Length));
            // Only operational status is saved; transcripts and audio are never logged.
            try { File.WriteAllText(statusFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + value + Environment.NewLine + "本次运行已复制次数：" + copyCount, new UTF8Encoding(false)); }
            catch (IOException) { }
        }

        void Tick(object sender, EventArgs args)
        {
            if (paused) return;
            if (source == null)
            {
                if (DateTime.UtcNow < nextAttach) return;
                nextAttach = DateTime.UtcNow.AddSeconds(5);
                Process[] processes = Process.GetProcessesByName("wetype_update");
                if (processes.Length != 1)
                {
                    foreach (var p in processes) p.Dispose();
                    Status("等待微信输入法启动"); return;
                }
                try { source = new Source(processes[0]); previous = null; Status("就绪，等待语音"); }
                catch (NotSupportedException ex) { nextAttach = DateTime.UtcNow.AddSeconds(60); Status(ex.Message); return; }
                catch (Exception) { Status("连接暂不可用，稍后自动重试"); return; }
            }
            try
            {
                Frame frame = source.Snapshot();
                errorSince = DateTime.MinValue;
                if (previous == null || previous.Engine != frame.Engine || previous.Manager != frame.Manager)
                {
                    // Completed text left over before startup/resume is never copied.
                    armed = frame.Active; seenStop = frame.State == 2 || frame.StopPending;
                    canceled = frame.CancelPending; candidate = null;
                }
                else if (frame.Active && (!previous.Active ||
                    (frame.State == 1 && previous.State == 2) ||
                    (frame.Session.Length > 0 && previous.Session.Length > 0 && frame.Session != previous.Session)))
                {
                    armed = true; seenStop = false; canceled = false; candidate = null;
                }
                if (armed)
                {
                    seenStop |= frame.State == 2 || frame.StopPending;
                    canceled |= frame.CancelPending;
                    if (frame.Active) Status(frame.State == 1 ? "正在识别，等待结束" : "正在等待最终结果");
                    if (frame.State == 3 && frame.Finished)
                    {
                        if (candidate != frame.Text) { candidate = frame.Text; stableSince = DateTime.UtcNow; }
                        else if ((DateTime.UtcNow - stableSince).TotalMilliseconds >= 600)
                        {
                            if (!canceled && seenStop && !String.IsNullOrWhiteSpace(candidate))
                            {
                                // Keep the UI thread in STA. Clipboard ownership is managed by WinForms/OLE.
                                var data = new DataObject();
                                data.SetData(DataFormats.UnicodeText, true, candidate);
                                Clipboard.SetDataObject(data, true, 10, 30);
                                copyCount++; Status("已复制 " + candidate.Length + " 字，等待下一轮");
                            }
                            else Status(canceled ? "本轮已取消，未复制" : "本轮没有可复制的最终结果");
                            armed = false; candidate = null;
                        }
                    }
                    else if (frame.State == 0) { armed = false; candidate = null; Status("本轮结束，未复制"); }
                }
                previous = frame;
                timer.Interval = armed || frame.Active ? 25 : 200;
            }
            catch (ExternalException)
            {
                if ((DateTime.UtcNow - stableSince).TotalSeconds > 8)
                { armed = false; candidate = null; Status("剪贴板持续被占用，本轮未复制"); }
                else Status("剪贴板正忙，正在重试");
            }
            catch (Exception)
            {
                if (errorSince == DateTime.MinValue) errorSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - errorSince).TotalSeconds >= 2)
                {
                    source.Dispose(); source = null; previous = null; armed = false; candidate = null;
                    nextAttach = DateTime.UtcNow.AddSeconds(3); timer.Interval = 250;
                    Status("语音连接已变化，正在重新连接");
                }
            }
        }
        protected override void ExitThreadCore()
        {
            timer.Stop(); timer.Dispose();
            if (source != null) source.Dispose();
            Status("已退出"); tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probe")
            {
                string output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "wetype-source-probe.txt");
                try
                {
                    Process[] processes = Process.GetProcessesByName("wetype_update");
                    if (processes.Length != 1) throw new IOException("Expected one input-method process");
                    using (var source = new Source(processes[0]))
                    {
                        Frame frame = source.Snapshot();
                        File.WriteAllText(output, "source_valid=true\r\nstate=" + frame.State + "\r\nfinished=" + frame.Finished + "\r\nfinal_length=" + frame.Text.Length);
                    }
                }
                catch (Exception ex) { File.WriteAllText(output, "source_valid=false\r\nerror=" + ex.GetType().Name + ": " + ex.Message); Environment.ExitCode = 1; }
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\WeTypeVoiceCapture.2_1_4_6", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Capture());
            }
        }
    }
}
