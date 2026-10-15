// Read-only adapter for hash-verified WeType versions. Optional remote paste is
// isolated in WindowsPasteDesktop; input-method memory is never modified.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WeTypeVoiceCapture
{
    sealed class Source : IDisposable
    {
        readonly VoiceLayout layout;
        readonly VoiceSnapshotReader reader;
        readonly Process process;
        readonly ulong imageBase;
        IntPtr handle;
        ulong bridge;

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
                layout = VoiceLayout.ForHash(hash);
                imageBase = (ulong)target.MainModule.BaseAddress.ToInt64();
                handle = OpenProcess(0x410, false, target.Id); // QUERY_INFORMATION | VM_READ only
                if (handle == IntPtr.Zero) throw new IOException("无法只读访问微信输入法");
                reader = new VoiceSnapshotReader(Read, layout, imageBase);
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
                            if (BitConverter.ToUInt64(chunk, i) != imageBase + layout.Table) continue;
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
        public string Version { get { return layout.Version; } }

        public Frame Snapshot()
        {
            if (process.HasExited) throw new IOException("微信输入法已退出");
            return reader.Snapshot(bridge);
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
            process.Dispose();
        }
    }

    sealed class Capture : ApplicationContext
    {
        readonly string appTitle = "微信语音复制 v" + Application.ProductVersion;
        readonly NotifyIcon tray;
        readonly Icon trayIcon;
        readonly ToolStripMenuItem stateItem, pauseItem, startupItem, autoPasteItem;
        readonly StartupRegistration startup;
        readonly AutoPasteSettings autoPasteSettings = new AutoPasteSettings();
        readonly WindowsPasteDesktop pasteDesktop;
        readonly AutoPasteController autoPaste;
        readonly System.Windows.Forms.Timer timer;
        readonly string statusFile;
        readonly TranscriptCapture transcripts;
        readonly ClipboardTextWriter clipboard = new ClipboardTextWriter();
        Source source;
        bool paused;
        string lastStatus;
        DateTime nextAttach, errorSince = DateTime.MinValue;
        int copyCount;

        public Capture()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeTypeVoiceCapture");
            Directory.CreateDirectory(folder);
            statusFile = Path.Combine(folder, "status.txt");
            pasteDesktop = new WindowsPasteDesktop(delegate(IntPtr window) { autoPaste.ForegroundChanged(window); });
            autoPaste = new AutoPasteController(pasteDesktop, Status);
            transcripts = new TranscriptCapture(CopyText, TranscriptStatus, autoPaste.BeginRound, autoPaste.Cancel);
            startup = new StartupRegistration(Application.ExecutablePath);
            var menu = new ContextMenuStrip();
            stateItem = new ToolStripMenuItem("正在连接微信输入法") { Enabled = false };
            pauseItem = new ToolStripMenuItem("暂停复制");
            pauseItem.Click += delegate {
                paused = !paused; pauseItem.Text = paused ? "恢复复制" : "暂停复制";
                transcripts.Reset();
                Status(paused ? "已暂停复制" : "已恢复，等待下一轮语音");
            };
            startupItem = new ToolStripMenuItem("开机启动");
            startupItem.Click += ToggleStartup;
            autoPasteItem = new ToolStripMenuItem("向日葵自动粘贴");
            autoPasteItem.Click += ToggleAutoPaste;
            menu.Opening += delegate { RefreshStartupMenu(); };
            menu.Items.Add(new ToolStripMenuItem(appTitle) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(stateItem); menu.Items.Add(pauseItem); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autoPasteItem); menu.Items.Add(startupItem); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            using (var stream = typeof(Capture).Assembly.GetManifestResourceStream("WeTypeVoiceCapture.AppIcon.ico"))
            using (var icon = new Icon(stream, SystemInformation.SmallIconSize))
                trayIcon = (Icon)icon.Clone();
            tray = new NotifyIcon { Icon = trayIcon, Text = appTitle, ContextMenuStrip = menu, Visible = true };
            try { startup.RefreshExecutablePath(); }
            catch (Exception ex)
            {
                if (!IsStartupError(ex)) throw;
                tray.ShowBalloonTip(5000, "开机启动设置未更新", "无法更新启动路径，请在托盘菜单中重新设置开机启动。", ToolTipIcon.Warning);
            }
            RefreshStartupMenu();
            try
            {
                if (autoPasteSettings.Enabled)
                {
                    if (!pasteDesktop.StartMonitoring()) throw new IOException("无法监听前台窗口变化");
                    autoPaste.SetEnabled(true); autoPasteItem.Checked = true;
                }
            }
            catch (Exception ex)
            {
                if (!IsStartupError(ex)) throw;
                tray.ShowBalloonTip(5000, "自动粘贴未开启", "仍可正常复制，请在托盘菜单重新开启向日葵自动粘贴。", ToolTipIcon.Warning);
            }
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += Tick; timer.Start();
            Status("正在连接微信输入法");
        }

        static bool IsStartupError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
        }

        void RefreshStartupMenu()
        {
            try
            {
                startupItem.Checked = startup.Enabled;
                startupItem.Enabled = true;
                startupItem.Text = "开机启动";
            }
            catch (Exception ex)
            {
                if (!IsStartupError(ex)) throw;
                startupItem.Checked = false;
                startupItem.Enabled = false;
                startupItem.Text = "开机启动（无法读取设置）";
            }
        }

        void ToggleStartup(object sender, EventArgs args)
        {
            try { startup.SetEnabled(!startup.Enabled); }
            catch (Exception ex)
            {
                if (!IsStartupError(ex)) throw;
                MessageBox.Show("无法更改开机启动设置：" + ex.Message, "微信语音复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshStartupMenu();
        }

        void ToggleAutoPaste(object sender, EventArgs args)
        {
            bool enable = !autoPaste.Enabled;
            autoPaste.SetEnabled(false); pasteDesktop.StopMonitoring();
            try
            {
                if (enable && !pasteDesktop.StartMonitoring()) throw new IOException("无法监听前台窗口变化");
                autoPasteSettings.SetEnabled(enable);
                autoPaste.SetEnabled(enable);
                Status(enable ? "向日葵自动粘贴已开启，从下一轮生效" : "向日葵自动粘贴已关闭");
            }
            catch (Exception ex)
            {
                if (!IsStartupError(ex)) throw;
                pasteDesktop.StopMonitoring();
                MessageBox.Show("本次运行已关闭自动粘贴，但设置未能保存：" + ex.Message,
                    "微信语音复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            autoPasteItem.Checked = autoPaste.Enabled;
        }

        void TranscriptStatus(string value)
        {
            Status(autoPaste.Pending && value.StartsWith("已复制 ", StringComparison.Ordinal) ?
                "已复制，等待向日葵自动粘贴" : value);
        }

        void Status(string value)
        {
            if (value == lastStatus) return;
            lastStatus = value; stateItem.Text = value;
            string tooltip = appTitle + "：" + value;
            tray.Text = tooltip.Substring(0, Math.Min(63, tooltip.Length));
            // Only operational status is saved; transcripts and audio are never logged.
            try { File.WriteAllText(statusFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + value +
                Environment.NewLine + "本次运行已复制次数：" + copyCount + Environment.NewLine +
                "向日葵自动粘贴：" + (autoPaste.Enabled ? "开启" : "关闭") + Environment.NewLine +
                "本次运行已触发自动粘贴次数：" + autoPaste.TriggeredCount, new UTF8Encoding(false)); }
            catch (IOException) { }
        }

        void CopyText(string text)
        {
            clipboard.SetText(text);
            copyCount++;
            autoPaste.Copied(text, DateTime.UtcNow);
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
                try { source = new Source(processes[0]); transcripts.Reset(); Status("已连接 " + source.Version + "，等待语音"); }
                catch (NotSupportedException ex) { nextAttach = DateTime.UtcNow.AddSeconds(60); Status(ex.Message); return; }
                catch (Exception) { Status("连接暂不可用，稍后自动重试"); return; }
            }
            try
            {
                Frame frame = source.Snapshot();
                errorSince = DateTime.MinValue;
                transcripts.Observe(frame, DateTime.UtcNow);
                autoPaste.Tick(DateTime.UtcNow);
                timer.Interval = transcripts.Armed || frame.Active || autoPaste.Pending ? 25 : 200;
            }
            catch (ExternalException)
            {
                transcripts.ClipboardBusy(DateTime.UtcNow);
            }
            catch (Exception)
            {
                autoPaste.Cancel();
                if (errorSince == DateTime.MinValue) errorSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - errorSince).TotalSeconds >= 2)
                {
                    source.Dispose(); source = null; transcripts.Reset();
                    nextAttach = DateTime.UtcNow.AddSeconds(3); timer.Interval = 250;
                    Status("语音连接已变化，正在重新连接");
                }
            }
        }
        protected override void ExitThreadCore()
        {
            timer.Stop(); timer.Dispose();
            if (source != null) source.Dispose();
            autoPaste.Cancel(); pasteDesktop.Dispose();
            clipboard.Dispose();
            Status("已退出"); tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--probe-autopaste")
            {
                using (var desktop = new WindowsPasteDesktop(delegate { }))
                {
                    bool monitoring = desktop.StartMonitoring();
                    var target = desktop.CaptureTarget();
                    File.WriteAllText(args[1], "monitoring_available=" + monitoring + "\r\nremote_foreground=" + (target != null));
                }
                return; // No keyboard, clipboard, focus or settings operations.
            }
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
                        File.WriteAllText(output, "source_valid=true\r\nsource_version=" + source.Version + "\r\nstate=" + frame.State + "\r\nfinished=" + frame.Finished + "\r\nfinal_length=" + frame.Text.Length);
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
