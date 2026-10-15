using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WeTypeVoiceCapture
{
    // Opt-in real Windows clipboard tests. CI uses a disposable hosted desktop.
    // Save/restore clipboard data in memory and never silently select this mode.
    static class ClipboardTextWriterTests
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        static readonly ManualResetEvent listenerReady = new ManualResetEvent(false);
        static readonly AutoResetEvent notification = new AutoResetEvent(false);
        static Listener listener;
        static Exception listenerError;
        static volatile bool measuring;
        static volatile string expected;
        static int updates, busy, stale;
        static string lastWritten;
        static uint lastSequence;

        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Check(bool condition) { if (!condition) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        static void Write(ClipboardTextWriter writer, string text)
        {
            writer.SetText(text); lastWritten = text; lastSequence = GetClipboardSequenceNumber();
        }
        static string ReadOpenClipboard()
        {
            IntPtr memory = GetClipboardData(13);
            Assert(memory != IntPtr.Zero, "Unicode text must be immediately available, without delayed rendering.");
            IntPtr buffer = GlobalLock(memory);
            Assert(buffer != IntPtr.Zero, "Cannot read clipboard memory.");
            try { return Marshal.PtrToStringUni(buffer); }
            finally { GlobalUnlock(memory); }
        }

        static string ReadText()
        {
            Check(OpenClipboard(IntPtr.Zero));
            try { return ReadOpenClipboard(); }
            finally { CloseClipboard(); }
        }

        sealed class Listener : NativeWindow
        {
            public Listener()
            {
                CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
                Check(AddClipboardFormatListener(Handle));
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x10)
                {
                    RemoveClipboardFormatListener(Handle); DestroyHandle(); Application.ExitThread(); return;
                }
                if (message.Msg == 0x31D && measuring)
                {
                    Interlocked.Increment(ref updates);
                    try
                    {
                        if (!OpenClipboard(Handle)) Interlocked.Increment(ref busy);
                        else try { if (ReadOpenClipboard() != expected) Interlocked.Increment(ref stale); }
                        finally { CloseClipboard(); }
                    }
                    catch (Exception error) { listenerError = error; }
                    finally { notification.Set(); }
                }
                base.WndProc(ref message);
            }
        }

        static void OneNotificationContainsLatestText()
        {
            using (var writer = new ClipboardTextWriter())
            {
                string[] samples = { "first", "second", "中文语音，标点。", "emoji \U0001F34E\r\n第二行", new string('长', 8000), "same", "same", "" };
                measuring = true;
                try
                {
                    for (int i = 0; i < 32; i++)
                    {
                        expected = samples[i % samples.Length];
                        Write(writer, expected);
                        Assert(notification.WaitOne(2000), "No clipboard update was delivered.");
                        // Drain any duplicate notifications before publishing the next round.
                        Thread.Sleep(10);
                        Assert(listenerError == null, "Listener could not read materialized text.");
                        Assert(busy == 0 && stale == 0, "First clipboard notification retained stale text or could not be read.");
                        Assert(updates == i + 1, "Each speech result must publish exactly one complete clipboard update.");
                        Assert(ReadText() == expected, "Native reader did not receive the latest complete text.");
                    }
                }
                finally { measuring = false; }
            }
        }

        static void BusyClipboardRetriesWithoutLosingRound()
        {
            using (var writer = new ClipboardTextWriter())
            using (var locked = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                Write(writer, "previous");
                Exception lockError = null;
                var holder = new Thread(delegate() {
                    try
                    {
                        Check(OpenClipboard(IntPtr.Zero));
                        try { locked.Set(); release.WaitOne(5000); }
                        finally { CloseClipboard(); }
                    }
                    catch (Exception error) { lockError = error; locked.Set(); }
                });
                holder.Start();
                int copies = 0;
                var capture = new TranscriptCapture(delegate(string text) { Write(writer, text); copies++; }, delegate { });
                var active = new Frame { State = 1, Text = "", Session = "one", Manager = 1, Engine = 2, CancelPending = false, StopPending = false };
                var final = new Frame { State = 3, Finished = true, Text = "latest", Session = "one", Manager = 1, Engine = 2 };
                DateTime now = new DateTime(2026, 10, 15);
                capture.Observe(active, now); capture.Observe(final, now.AddMilliseconds(100));
                try
                {
                    Assert(locked.WaitOne(2000) && lockError == null, "Could not hold the clipboard for contention test.");
                    bool rejected = false;
                    try { capture.Observe(final, now.AddMilliseconds(750)); }
                    catch (ExternalException) { rejected = true; capture.ClipboardBusy(now.AddMilliseconds(750)); }
                    Assert(rejected && capture.Armed && copies == 0, "Busy writes must leave the round pending, without reporting success.");
                }
                finally { release.Set(); Assert(holder.Join(2000), "Clipboard lock thread did not exit."); }
                Assert(ReadText() == "previous", "A failed OpenClipboard must preserve prior contents.");
                capture.Observe(final, now.AddMilliseconds(800));
                capture.Observe(final, now.AddMilliseconds(900));
                Assert(copies == 1 && ReadText() == "latest", "Retry must publish the latest result exactly once.");
            }
        }

        static void TextSurvivesOwnerDisposalAndInvalidInput()
        {
            var writer = new ClipboardTextWriter();
            try
            {
                Write(writer, "保留文本\U0001F34C");
                bool rejected = false;
                try { writer.SetText("invalid\0truncated"); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && ReadText() == "保留文本\U0001F34C", "Invalid text must not clear the clipboard.");
            }
            finally { writer.Dispose(); writer.Dispose(); }
            Assert(ReadText() == "保留文本\U0001F34C", "Text must survive destruction of its owner window.");
            Assert(Clipboard.GetText(TextDataFormat.UnicodeText) == "保留文本\U0001F34C", "WinForms consumers must also read the Unicode text.");
            lastSequence = GetClipboardSequenceNumber();
        }

        static int RunTests()
        {
            Thread receiver = null;
            int failures = 0;
            try
            {
                receiver = new Thread(delegate() {
                    try { listener = new Listener(); listenerReady.Set(); Application.Run(); }
                    catch (Exception error) { listenerError = error; listenerReady.Set(); }
                });
                receiver.SetApartmentState(ApartmentState.STA); receiver.IsBackground = true; receiver.Start();
                Assert(listenerReady.WaitOne(2000) && listenerError == null, "Clipboard listener failed to start.");
                var runner = new Thread(delegate() {
                    try
                    {
                        var saved = new DataObject(); var data = Clipboard.GetDataObject();
                        if (data != null) foreach (string format in data.GetFormats(false))
                        {
                            object value = data.GetData(format, false);
                            if (value != null) saved.SetData(format, false, value);
                        }
                        try
                        {
                            foreach (var test in new Action[] { OneNotificationContainsLatestText, BusyClipboardRetriesWithoutLosingRound, TextSurvivesOwnerDisposalAndInvalidInput })
                            {
                                try { test(); Console.WriteLine("PASS: " + test.Method.Name); }
                                catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: " + test.Method.Name + ": " + error.Message); break; }
                            }
                        }
                        finally
                        {
                            measuring = false;
                            // Never restore over a newer external copy.
                            if (lastWritten != null && GetClipboardSequenceNumber() == lastSequence &&
                                Clipboard.GetText(TextDataFormat.UnicodeText) == lastWritten &&
                                GetClipboardSequenceNumber() == lastSequence)
                            {
                                Clipboard.SetDataObject(saved, true);
                                Console.WriteLine("PASS: original clipboard restored.");
                            }
                            else if (lastWritten != null)
                                Console.WriteLine("Clipboard changed externally; retained its newer contents.");
                        }
                    }
                    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: clipboard fixture: " + error.GetType().Name); }
                });
                runner.SetApartmentState(ApartmentState.STA); runner.IsBackground = true; runner.Start();
                Assert(runner.Join(15000), "Clipboard tests timed out.");
            }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL: clipboard fixture: " + error.Message); }
            finally
            {
                if (listener != null) PostMessage(listener.Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
                if (receiver != null && !receiver.Join(2000)) failures++;
            }
            return failures == 0 ? 0 : 1;
        }

        static int Main(string[] args)
        {
            if (args.Length != 1 || args[0] != "--desktop-clipboard")
            {
                Console.Error.WriteLine("Use --desktop-clipboard to explicitly allow temporary clipboard writes.");
                return 1;
            }
            using (var mutex = new Mutex(false, "Local\\WeTypeVoiceCapture.ClipboardTests"))
            {
                if (!mutex.WaitOne(10000)) { Console.Error.WriteLine("Clipboard tests already running."); return 1; }
                try { return RunTests(); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
