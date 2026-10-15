using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WeTypeVoiceCapture
{
    // Each layout is tied to an exact executable, not just a product version.
    sealed class VoiceLayout
    {
        public readonly string Version;
        public readonly ulong Table, Manager, Engine, State, Text, Finished, Session, Cancel, Stop;

        VoiceLayout(string version, ulong table, ulong manager, ulong state, ulong text,
            ulong finished, ulong session, ulong cancel, ulong stop)
        {
            Version = version; Table = table; Manager = manager; Engine = 0x78;
            State = state; Text = text; Finished = finished; Session = session;
            Cancel = cancel; Stop = stop;
        }

        public static VoiceLayout ForHash(string hash)
        {
            switch (hash)
            {
                case "7AAE1BD693BD1E94FD2DA9CCF577C8C92BFA88D87B719DF3B9330FA81D19C292":
                    return new VoiceLayout("2.1.4.6", 0xfeae58, 0x158, 0x48, 0x100, 0xab0, 0xb08, 0x6a8, 0x6b0);
                case "580CE85A6108B5883AFE6F25502FE4F0EF221148372DE782F49C6AB15E63A201":
                    return new VoiceLayout("3.0.0.34", 0x10b9a80, 0x168, 0x58, 0x160, 0xc98, 0xcf0, 0x6c8, 0x6d0);
                default:
                    throw new NotSupportedException("微信输入法版本已变化，需要重新适配");
            }
        }
    }

    // Shared by live process reads and deterministic memory-layout regression tests.
    sealed class VoiceSnapshotReader
    {
        readonly Func<ulong, int, byte[]> read;
        readonly VoiceLayout layout;
        readonly ulong imageBase;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public VoiceSnapshotReader(Func<ulong, int, byte[]> read, VoiceLayout layout, ulong imageBase)
        {
            this.read = read; this.layout = layout; this.imageBase = imageBase;
        }

        ulong U64(ulong address) { return BitConverter.ToUInt64(read(address, 8), 0); }
        int I32(ulong address) { return BitConverter.ToInt32(read(address, 4), 0); }
        bool Flag(ulong address)
        {
            byte value = read(address, 1)[0];
            if (value > 1) throw new IOException("语音状态无效");
            return value == 1;
        }

        string ReadString(ulong address)
        {
            byte[] before = read(address, 32);
            ulong length = BitConverter.ToUInt64(before, 16), capacity = BitConverter.ToUInt64(before, 24);
            if (length > capacity || length > 65536 || capacity > 1000000)
                throw new IOException("语音文本长度无效");
            ulong pointer = capacity <= 15 ? address : BitConverter.ToUInt64(before, 0);
            byte[] text = length == 0 ? new byte[0] : read(pointer, (int)length);
            if (!before.SequenceEqual(read(address, 32)) || (length > 0 && !text.SequenceEqual(read(pointer, (int)length))))
                throw new IOException("语音文本尚未稳定");
            string value = StrictUtf8.GetString(text);
            if (value.IndexOf('\0') >= 0) throw new IOException("语音文本包含无效字符");
            return value;
        }

        public Frame Snapshot(ulong bridge)
        {
            if (U64(bridge) != imageBase + layout.Table) throw new IOException("语音对象已变化");
            ulong manager = U64(bridge + layout.Manager), engine = U64(manager + layout.Engine);
            int state = I32(engine + layout.State);
            if (state < 0 || state > 3) throw new IOException("未知语音状态");
            string id = ReadString(manager + layout.Session);
            var frame = new Frame {
                State = state, Session = id, Manager = manager, Engine = engine,
                Finished = Flag(manager + layout.Finished), CancelPending = Flag(bridge + layout.Cancel),
                StopPending = Flag(bridge + layout.Stop), Text = state == 3 ? ReadString(engine + layout.Text) : ""
            };
            if (U64(bridge) != imageBase + layout.Table || U64(bridge + layout.Manager) != manager ||
                U64(manager + layout.Engine) != engine || I32(engine + layout.State) != state ||
                ReadString(manager + layout.Session) != id || Flag(manager + layout.Finished) != frame.Finished)
                throw new IOException("语音轮次正在变化");
            if (Flag(bridge + layout.Cancel)) frame.CancelPending = true;
            return frame;
        }
    }
}
