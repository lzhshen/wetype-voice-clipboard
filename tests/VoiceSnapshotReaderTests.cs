using System;
using System.IO;
using System.Text;

namespace WeTypeVoiceCapture
{
    static class VoiceSnapshotReaderTests
    {
        const string OldHash = "7AAE1BD693BD1E94FD2DA9CCF577C8C92BFA88D87B719DF3B9330FA81D19C292";
        const string NewHash = "580CE85A6108B5883AFE6F25502FE4F0EF221148372DE782F49C6AB15E63A201";

        sealed class Memory
        {
            public const ulong Bridge = 0x11000, Manager = 0x13000, Engine = 0x15000, Image = 0x140000000;
            readonly byte[] bytes = new byte[0x20000];
            public readonly ulong State, Final, Finished, Session, Cancel;
            public readonly VoiceSnapshotReader Reader;
            public Action<ulong> BeforeRead;

            public Memory(bool latest)
            {
                // Independent fixtures from the two inspected binaries: deliberately
                // do not populate memory from VoiceLayout, which would hide bad offsets.
                if (latest)
                {
                    Put(Bridge, BitConverter.GetBytes(Image + 0x10b9a80));
                    Put(Bridge + 0x168, BitConverter.GetBytes(Manager));
                    State = Engine + 0x58; Final = Engine + 0x160;
                    Finished = Manager + 0xc98; Session = Manager + 0xcf0; Cancel = Bridge + 0x6c8;
                }
                else
                {
                    Put(Bridge, BitConverter.GetBytes(Image + 0xfeae58));
                    Put(Bridge + 0x158, BitConverter.GetBytes(Manager));
                    State = Engine + 0x48; Final = Engine + 0x100;
                    Finished = Manager + 0xab0; Session = Manager + 0xb08; Cancel = Bridge + 0x6a8;
                }
                Put(Manager + 0x78, BitConverter.GetBytes(Engine));
                SetString(Session, "session-123456789012345678", 0x18000);
                SetString(Final, "", 0x17000);
                SetState(1, false);
                Reader = new VoiceSnapshotReader(Read, VoiceLayout.ForHash(latest ? NewHash : OldHash), Image);
            }

            public void Put(ulong address, byte[] data) { Array.Copy(data, 0, bytes, (int)address, data.Length); }
            public void SetState(int state, bool finished)
            {
                Put(State, BitConverter.GetBytes(state)); Put(Finished, new[] { (byte)(finished ? 1 : 0) });
            }
            public void SetString(ulong address, string text, ulong buffer)
            {
                byte[] data = Encoding.UTF8.GetBytes(text), header = new byte[32];
                if (data.Length <= 15) Array.Copy(data, header, data.Length);
                else { Array.Copy(BitConverter.GetBytes(buffer), header, 8); Put(buffer, data); }
                Array.Copy(BitConverter.GetBytes((ulong)data.Length), 0, header, 16, 8);
                Array.Copy(BitConverter.GetBytes((ulong)Math.Max(15, data.Length)), 0, header, 24, 8);
                Put(address, header);
            }
            public byte[] Read(ulong address, int count)
            {
                if (BeforeRead != null) BeforeRead(address);
                if (address >= (ulong)bytes.Length || address + (ulong)count > (ulong)bytes.Length)
                    throw new IOException("Invalid fixture address");
                byte[] result = new byte[count]; Array.Copy(bytes, (int)address, result, 0, count); return result;
            }
            public Frame Snapshot() { return Reader.Snapshot(Bridge); }
        }

        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Rejects(Action action)
        {
            try { action(); } catch (IOException) { return; }
            throw new Exception("An inconsistent snapshot was accepted");
        }

        static void BothLayoutsCompleteAndCopy()
        {
            foreach (bool latest in new[] { false, true })
            foreach (string text in new[] { "short", "新版语音完成后复制到剪贴板。" })
            {
                var memory = new Memory(latest); int copies = 0; string clipboard = "previous";
                var tracker = new TranscriptCapture(delegate(string value) { copies++; clipboard = value; }, delegate { });
                DateTime now = new DateTime(2026, 10, 15);
                tracker.Observe(memory.Snapshot(), now);
                memory.SetState(3, true); memory.SetString(memory.Final, text, 0x17000);
                Frame frame = memory.Snapshot();
                Assert(frame.Session == "session-123456789012345678" && frame.Finished && frame.Text == text,
                    "Layout decoded the wrong final result or session");
                tracker.Observe(frame, now.AddMilliseconds(100));
                tracker.Observe(memory.Snapshot(), now.AddMilliseconds(700));
                tracker.Observe(memory.Snapshot(), now.AddMilliseconds(1000));
                Assert(clipboard == text && copies == 1, "Completed speech must copy exactly once");
            }
        }

        static void BothLayoutsRespectCancellation()
        {
            foreach (bool latest in new[] { false, true })
            {
                var memory = new Memory(latest); int copies = 0;
                var tracker = new TranscriptCapture(delegate { copies++; }, delegate { });
                DateTime now = new DateTime(2026, 10, 15);
                tracker.Observe(memory.Snapshot(), now);
                memory.Put(memory.Cancel, new byte[] { 1 });
                tracker.Observe(memory.Snapshot(), now.AddMilliseconds(50));
                memory.Put(memory.Cancel, new byte[] { 0 });
                memory.SetState(3, true); memory.SetString(memory.Final, "canceled", 0x17000);
                tracker.Observe(memory.Snapshot(), now.AddMilliseconds(100));
                tracker.Observe(memory.Snapshot(), now.AddMilliseconds(700));
                Assert(copies == 0, "Canceled speech was copied");
            }
        }

        static void ChangingCompletionOrStateIsRejected()
        {
            foreach (bool latest in new[] { false, true })
            foreach (bool changeFinished in new[] { false, true })
            {
                var memory = new Memory(latest); memory.SetState(3, true); int reads = 0;
                ulong changing = changeFinished ? memory.Finished : memory.State;
                memory.BeforeRead = delegate(ulong address) {
                    if (address == changing && ++reads == 2) memory.SetState(2, false);
                };
                Rejects(delegate { memory.Snapshot(); });
            }
        }

        static void LateCancellationIsObserved()
        {
            var memory = new Memory(true); int reads = 0;
            memory.BeforeRead = delegate(ulong address) {
                if (address == memory.Cancel && ++reads == 2) memory.Put(memory.Cancel, new byte[] { 1 });
            };
            Assert(memory.Snapshot().CancelPending, "Cancellation arriving during a snapshot was lost");
        }

        static void CorruptAndChangingStringsAreRejected()
        {
            var memory = new Memory(true); memory.SetState(3, true);
            memory.Put(memory.Final + 16, BitConverter.GetBytes((ulong)1000001));
            Rejects(delegate { memory.Snapshot(); });
            memory.SetString(memory.Final, "first", 0x17000); int reads = 0;
            memory.BeforeRead = delegate(ulong address) {
                if (address == memory.Final && ++reads == 2) memory.SetString(memory.Final, "changed", 0x17000);
            };
            Rejects(delegate { memory.Snapshot(); });
        }

        static void InvalidUtf8AndFlagsAreRejected()
        {
            var memory = new Memory(true); memory.SetState(3, true);
            memory.SetString(memory.Final, "a", 0x17000); memory.Put(memory.Final, new byte[] { 0xff });
            bool rejected = false;
            try { memory.Snapshot(); } catch (DecoderFallbackException) { rejected = true; }
            Assert(rejected, "Invalid UTF-8 must never reach the clipboard");
            memory.SetString(memory.Final, "", 0x17000); memory.Put(memory.Finished, new byte[] { 2 });
            Rejects(delegate { memory.Snapshot(); });
        }

        static void UnknownBinaryAndWrongLayoutAreRejected()
        {
            bool rejected = false;
            try { VoiceLayout.ForHash("unknown"); } catch (NotSupportedException) { rejected = true; }
            Assert(rejected, "Unknown executable must not be guessed");
            var memory = new Memory(true);
            var oldReader = new VoiceSnapshotReader(memory.Read, VoiceLayout.ForHash(OldHash), Memory.Image);
            Rejects(delegate { oldReader.Snapshot(Memory.Bridge); });
        }

        static int Main()
        {
            var tests = new Action[] { BothLayoutsCompleteAndCopy, BothLayoutsRespectCancellation,
                ChangingCompletionOrStateIsRejected, LateCancellationIsObserved,
                CorruptAndChangingStringsAreRejected, InvalidUtf8AndFlagsAreRejected, UnknownBinaryAndWrongLayoutAreRejected };
            int failures = 0;
            foreach (var test in tests)
            {
                try { test(); Console.WriteLine("PASS: " + test.Method.Name); }
                catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: " + test.Method.Name + ": " + error.Message); }
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
