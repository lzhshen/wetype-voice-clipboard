using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WeTypeVoiceCapture
{
    static class AutoPasteTests
    {
        sealed class Desktop : IPasteDesktop
        {
            public RemotePasteTarget Current = new RemotePasteTarget((IntPtr)101, 7, "remote one");
            public uint? Stamp = 1;
            public PasteAttempt Result = PasteAttempt.Sent;
            public readonly List<string> Pastes = new List<string>();
            public int Captures, Attempts;
            public bool Pasting { get; private set; }
            public void CancelPaste() { Pasting = false; }
            public RemotePasteTarget CaptureTarget() { Captures++; return Current; }
            public bool IsCurrent(RemotePasteTarget target)
            { return Current != null && Current.Window == target.Window && Current.ProcessId == target.ProcessId && Current.Title == target.Title; }
            public uint? InputStamp { get { return Stamp; } }
            public PasteAttempt Paste(RemotePasteTarget target, string text, uint stamp)
            { Attempts++; if (Result == PasteAttempt.Pending) { Pasting = true; Stamp = 999; }
                if (Result == PasteAttempt.Sent) Pastes.Add(text); return Result; }
        }

        sealed class Replay
        {
            public readonly Desktop Desktop = new Desktop();
            public readonly AutoPasteController Auto;
            public readonly TranscriptCapture Voice;
            public readonly List<string> Copies = new List<string>();
            public string Clipboard = "old";
            public bool CopyBusy;
            DateTime now;
            public Replay(bool enabled = true)
            {
                Auto = new AutoPasteController(Desktop, delegate { });
                Auto.SetEnabled(enabled);
                Voice = new TranscriptCapture(delegate(string text) {
                    if (CopyBusy) throw new ExternalException("busy");
                    Clipboard = text; Copies.Add(text); Auto.Copied(text, now);
                }, delegate { }, Auto.BeginRound, Auto.Cancel);
            }
            public void Feed(int ms, int state, string text = "latest", string session = "one", bool cancel = false, ulong engine = 2)
            {
                now = new DateTime(2026, 10, 15).AddMilliseconds(ms);
                try { Voice.Observe(new Frame { State = state, Finished = true, Session = session, Text = text,
                    Manager = 1, Engine = engine, CancelPending = cancel, StopPending = false }, now); }
                catch (ExternalException) { Voice.ClipboardBusy(now); }
                Auto.Tick(now);
            }
            public void Tick(int ms) { Auto.Tick(new DateTime(2026, 10, 15).AddMilliseconds(ms)); }
            public void Complete(string text = "latest")
            { Feed(0, 0); Feed(100, 1); Feed(200, 3, text); Feed(800, 3, text); }
        }

        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        static void DefaultOffAndLocalOnlyCopy()
        {
            var off = new Replay(false); off.Complete(); off.Tick(2000);
            Assert(off.Clipboard == "latest" && off.Copies.Count == 1 && off.Desktop.Attempts == 0 && off.Desktop.Captures == 0,
                "Disabled mode must retain the original clipboard path without acquiring a paste target.");
            var local = new Replay(); local.Desktop.Current = null; local.Complete(); local.Tick(2000);
            Assert(local.Clipboard == "latest" && local.Copies.Count == 1 && local.Desktop.Attempts == 0,
                "Local dictation must copy without adding a paste, even with the option enabled.");
        }

        static void RemoteCompletionPastesOnceAfterCopy()
        {
            var r = new Replay(); r.Complete("中文😀\r\nsecond line");
            Assert(r.Auto.Pending && r.Desktop.Pastes.Count == 0 && r.Copies.Count == 1, "Paste must follow successful copying.");
            r.Tick(1549); Assert(r.Desktop.Attempts == 0, "Remote clipboard settling interval was skipped.");
            r.Tick(1550); r.Tick(2000); r.Feed(3000, 3, "中文😀\r\nsecond line");
            Assert(r.Desktop.Attempts == 1 && r.Desktop.Pastes[0] == r.Clipboard && r.Copies.Count == 1, "One round must trigger exactly one paste.");
        }

        static void AwayAndBackDuringSpeechStaysCanceled()
        {
            var r = new Replay(); r.Feed(0, 0); r.Feed(100, 1);
            r.Auto.ForegroundChanged((IntPtr)999); r.Auto.ForegroundChanged((IntPtr)101);
            r.Feed(200, 3); r.Feed(800, 3); r.Tick(2000);
            Assert(r.Desktop.Attempts == 0 && r.Clipboard == "latest", "An away-and-back between polls must cancel this round only, not copying.");
            r.Feed(2100, 1, session: "two"); r.Feed(2200, 3, session: "two"); r.Feed(2800, 3, session: "two"); r.Tick(3550);
            Assert(r.Desktop.Pastes.Count == 1, "A later round in the target must still work.");
        }

        static void WindowSwitchAfterCopyAndOtherRemoteCancel()
        {
            foreach (bool eventDelivered in new[] { false, true })
            {
                var r = new Replay(); r.Complete();
                r.Desktop.Current = new RemotePasteTarget((IntPtr)102, 7, "remote two");
                if (eventDelivered) r.Auto.ForegroundChanged((IntPtr)102);
                r.Tick(1000); r.Desktop.Current = new RemotePasteTarget((IntPtr)101, 7, "remote one"); r.Tick(2000);
                Assert(r.Desktop.Attempts == 0 && r.Clipboard == "latest", "Switching to another remote window must cancel, with or without a delivered event.");
            }
            var title = new Replay(); title.Complete(); title.Desktop.Current = new RemotePasteTarget((IntPtr)101, 7, "changed target"); title.Tick(2000);
            Assert(title.Desktop.Attempts == 0, "Reusing a window for another target must cancel.");
        }

        static void ToggleAndPauseDoNotRevivePendingRounds()
        {
            var r = new Replay(); r.Complete(); r.Auto.SetEnabled(false); r.Auto.SetEnabled(true); r.Tick(2000);
            Assert(r.Desktop.Attempts == 0, "Toggling back on must not revive a pending paste.");
            var mid = new Replay(false); mid.Feed(0, 0); mid.Feed(100, 1); mid.Auto.SetEnabled(true); mid.Feed(200, 3); mid.Feed(800, 3); mid.Tick(2000);
            Assert(mid.Desktop.Attempts == 0 && mid.Copies.Count == 1, "Enabling mid-round must only affect a new round.");
            var paused = new Replay(); paused.Complete(); paused.Voice.Reset(); paused.Tick(2000); paused.Feed(3000, 3);
            Assert(paused.Desktop.Attempts == 0 && paused.Copies.Count == 1, "Pause/reset must cancel pending paste and skip old text on resume.");
        }

        static void StartupAndReconnectNeverPasteAnExistingRound()
        {
            var r = new Replay(); r.Feed(0, 1); r.Feed(200, 3); r.Feed(800, 3); r.Tick(2000);
            Assert(r.Copies.Count == 1 && r.Desktop.Attempts == 0, "Starting mid-dictation may copy but cannot establish a safe paste target.");
            var changed = new Replay(); changed.Complete(); changed.Feed(900, 1, engine: 9); changed.Feed(1000, 3, engine: 9); changed.Feed(1600, 3, engine: 9); changed.Tick(2500);
            Assert(changed.Desktop.Attempts == 0, "Changing input-method objects must cancel and skip the in-progress round.");
        }

        static void CanceledEmptyAndIdleRoundsNeverPaste()
        {
            var canceled = new Replay(); canceled.Feed(0, 0); canceled.Feed(100, 1); canceled.Feed(150, 1, cancel: true);
            canceled.Feed(200, 3); canceled.Feed(800, 3); canceled.Tick(2000);
            Assert(canceled.Copies.Count == 0 && canceled.Desktop.Attempts == 0, "Canceled dictation must not copy or paste.");
            var empty = new Replay(); empty.Complete("  "); empty.Tick(2000);
            Assert(empty.Copies.Count == 0 && empty.Desktop.Attempts == 0, "Empty dictation must not paste.");
            var idle = new Replay(); idle.Feed(0, 0); idle.Feed(100, 1); idle.Feed(150, 0); idle.Feed(200, 3); idle.Feed(800, 3); idle.Tick(2000);
            Assert(idle.Copies.Count == 0 && idle.Desktop.Attempts == 0, "An abandoned round must not paste.");
        }

        static void ConsecutiveIdenticalRoundsAndNewRoundSupersession()
        {
            var r = new Replay(); r.Complete("same"); r.Tick(1550);
            r.Feed(2000, 1, session: "two"); r.Feed(2100, 3, "same", "two"); r.Feed(2700, 3, "same", "two"); r.Tick(3450); r.Tick(4000);
            Assert(r.Copies.Count == 2 && r.Desktop.Pastes.Count == 2, "Identical text in distinct rounds must each paste once.");
            var newer = new Replay(); newer.Complete("old round"); newer.Feed(900, 1, session: "two"); newer.Tick(1600);
            newer.Feed(1700, 3, "new round", "two"); newer.Feed(2300, 3, "new round", "two"); newer.Tick(3050);
            Assert(newer.Copies.Count == 2 && newer.Desktop.Pastes.Count == 1 && newer.Desktop.Pastes[0] == "new round", "A new round must replace the old pending paste.");
        }

        static void ClipboardCopyRetryDoesNotPasteEarlyOrTwice()
        {
            var r = new Replay(); r.CopyBusy = true; r.Complete(); r.Tick(1600);
            Assert(r.Copies.Count == 0 && r.Desktop.Attempts == 0, "Failed clipboard publication must not schedule a paste.");
            r.CopyBusy = false; r.Feed(1700, 3); r.Tick(2450); r.Tick(3000);
            Assert(r.Copies.Count == 1 && r.Desktop.Pastes.Count == 1, "A successful clipboard retry must schedule only one paste.");
            var timeout = new Replay(); timeout.CopyBusy = true; timeout.Complete(); timeout.Feed(9000, 3);
            timeout.CopyBusy = false; timeout.Feed(9100, 3); timeout.Tick(10000);
            Assert(timeout.Copies.Count == 0 && timeout.Desktop.Attempts == 0, "Clipboard write timeout must not revive a stale round.");
        }

        static void PendingInputClipboardAndSendFailuresStaySafe()
        {
            foreach (uint? stamp in new uint?[] { 2, null })
            {
                var r = new Replay(); r.Complete(); r.Desktop.Stamp = stamp; r.Tick(2000);
                Assert(r.Desktop.Attempts == 0, "New manual input or unreadable input state must cancel.");
            }
            foreach (var result in new[] { PasteAttempt.Unsafe, PasteAttempt.Failed })
            {
                var r = new Replay(); r.Complete(); r.Desktop.Result = result; r.Tick(1550); r.Desktop.Result = PasteAttempt.Sent; r.Tick(1600);
                Assert(r.Desktop.Attempts == 1 && r.Desktop.Pastes.Count == 0 && r.Copies.Count == 1, "Changed clipboard or a failed/partial send must not retry or recopy.");
            }
            var busy = new Replay(); busy.Complete(); busy.Desktop.Result = PasteAttempt.ClipboardBusy; busy.Tick(1550);
            busy.Desktop.Result = PasteAttempt.Sent; busy.Tick(1600); busy.Tick(1700);
            Assert(busy.Desktop.Pastes.Count == 1, "Clipboard-read contention may retry before any key was sent.");
            var expired = new Replay(); expired.Complete(); expired.Desktop.Result = PasteAttempt.ClipboardBusy; expired.Tick(1550); expired.Tick(3551);
            expired.Desktop.Result = PasteAttempt.Sent; expired.Tick(4000);
            Assert(expired.Desktop.Pastes.Count == 0 && !expired.Auto.Pending, "Read contention must expire without a delayed paste.");
        }

        static void OnlyRecognizedRemoteWindowsQualify()
        {
            Assert(WindowsPasteDesktop.IsRemoteShape("AweSun", "FlutterMultiWindow", "remote", true), "Observed remote window should qualify.");
            Assert(WindowsPasteDesktop.IsRemoteShape("AweSun", "ORAY_FLUTTER_VIEW_CONTAINER", "remote", true), "Observed view container should qualify.");
            Assert(!WindowsPasteDesktop.IsRemoteShape("AweSun", "FLUTTER_RUNNER_WIN32_WINDOW", "remote", true), "Launcher must never qualify.");
            Assert(!WindowsPasteDesktop.IsRemoteShape("notepad", "FlutterMultiWindow", "remote", true), "Another process must never qualify.");
            Assert(!WindowsPasteDesktop.IsRemoteShape("AweSun", "FlutterMultiWindow", "remote", false), "Unpaired dialogs must never qualify.");
            Assert(!WindowsPasteDesktop.IsRemoteShape("AweSun", "FlutterMultiWindow", "", true), "Unnamed windows must never qualify.");
        }

        static void KeyboardSequenceGuardsAndPartialCleanup()
        {
            Assert(Marshal.SizeOf(typeof(PasteKeyboard.Input)) == 40 && Marshal.OffsetOf(typeof(PasteKeyboard.Input), "Data").ToInt32() == 8,
                "Native INPUT layout must match Windows x64.");
            var r = new KeysReplay();
            Assert(r.Step(0) == PasteAttempt.Pending && r.Held.Contains(0x11) && r.VPresses == 0, "First step must hold Ctrl without V.");
            r.Step(99); Assert(r.Batches.Count == 1, "Ctrl needs a lead interval.");
            Assert(r.Step(100) == PasteAttempt.Pending && r.VPresses == 1 && r.Held.Contains(0x11), "Ctrl must stay down after sending V.");
            r.Step(199); Assert(r.Held.Contains(0x11), "Do not release Ctrl before the receiver can process V.");
            Assert(r.Step(200) == PasteAttempt.Sent && !r.Held.Contains(0x11), "Completion must release Ctrl.");
            r.Step(300); r.Sequence.Dispose();
            Assert(r.VPresses == 1 && r.Batches.Count == 3, "Completed sequence must never be repeated.");
            foreach (var batch in r.Batches) foreach (var input in batch)
                Assert(input.Data.Key.Scan != 0 && (input.Data.Key.Flags & 8) != 0 && input.Data.Key.Extra == (UIntPtr)33,
                    "Every input needs a physical scan code and its own-event marker.");
            foreach (int held in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x56 })
            {
                var heldReplay = new KeysReplay(); heldReplay.Held.Add(held);
                Assert(heldReplay.Step(0) == PasteAttempt.Unsafe && heldReplay.Batches.Count == 0 && heldReplay.Held.Contains(held), "Keys already held by the user must be untouched.");
            }
            foreach (uint partial in new uint[] { 0, 1 })
            {
                var failed = new KeysReplay(); failed.Step(0); failed.NextSendCount = partial;
                Assert(failed.Step(100) == PasteAttempt.Failed && failed.Held.Count == 0, "Partial V insertion must release our keys.");
                int batches = failed.Batches.Count; failed.Step(200);
                Assert(failed.Batches.Count == batches && failed.VPresses <= 1, "Partial failure must not retry V.");
            }
        }

        sealed class KeysReplay
        {
            public readonly List<PasteKeyboard.Input[]> Batches = new List<PasteKeyboard.Input[]>();
            public readonly HashSet<int> Held = new HashSet<int>();
            public readonly PasteKeySequence Sequence;
            public bool Safe = true, Interrupted, GuardAvailable = true, UserTookControl;
            public int VPresses;
            public uint? NextSendCount;
            public KeysReplay()
            {
                Sequence = new PasteKeySequence(0x2f, (UIntPtr)33, Held.Contains,
                    delegate(PasteKeyboard.Input[] batch) {
                        Batches.Add(batch); uint count = NextSendCount ?? (uint)batch.Length; NextSendCount = null;
                        for (int i = 0; i < count; i++)
                        {
                            var key = batch[i].Data.Key;
                            if ((key.Flags & 2) != 0) Held.Remove(key.Code);
                            else { Held.Add(key.Code); if (key.Code == 0x56) VPresses++; }
                        }
                        return count;
                    }, delegate { return GuardAvailable; }, delegate { }, delegate { return Interrupted; },
                    delegate(int key) { return key != 0xa2 || !UserTookControl; });
            }
            public PasteAttempt Step(int ms)
            { return Sequence.Step(new DateTime(2026, 10, 15).AddMilliseconds(ms), delegate { return Safe; }); }
        }

        static void SplitSequenceCancellationReleasesOwnedKeys()
        {
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                var r = new KeysReplay(); r.Step(0);
                if (mode == 0) { r.Safe = false; r.Step(50); }
                if (mode == 1) { r.Interrupted = true; r.Step(50); }
                if (mode == 2) r.Step(1001);
                if (mode == 3) r.Sequence.Dispose();
                Assert(r.Held.Count == 0 && r.VPresses == 0, "Focus, new input, timeout and disposal must release Ctrl without V.");
            }
            var noGuard = new KeysReplay(); noGuard.GuardAvailable = false;
            Assert(noGuard.Step(0) == PasteAttempt.Unsafe && noGuard.Batches.Count == 0, "Unavailable input guard must block all synthesis.");
            var lostCtrl = new KeysReplay(); lostCtrl.Step(0); lostCtrl.Held.Remove(0x11); lostCtrl.Step(100);
            Assert(lostCtrl.VPresses == 0, "Do not send bare V if Ctrl did not become held.");
            var user = new KeysReplay(); user.Step(0); user.Interrupted = true; user.UserTookControl = true; user.Step(50);
            Assert(user.Held.Contains(0x11) && user.VPresses == 0, "Do not release Ctrl newly held by the user during synthesis.");
            var failedCtrl = new KeysReplay(); failedCtrl.NextSendCount = 0;
            Assert(failedCtrl.Step(0) == PasteAttempt.Failed && failedCtrl.Batches.Count == 1, "Failed Ctrl down must not send V or a spurious release.");
        }

        static void InFlightPasteRetainsCancellationAndOwnInputGuard()
        {
            var r = new Replay(); r.Complete(); r.Desktop.Result = PasteAttempt.Pending; r.Tick(1550);
            Assert(r.Desktop.Pasting && r.Auto.Pending, "Partial sequence must remain pending.");
            r.Desktop.Result = PasteAttempt.Sent; r.Tick(1575);
            Assert(r.Desktop.Pastes.Count == 1 && !r.Desktop.Pasting, "Our own Ctrl event must not cancel our sequence.");
            foreach (bool toggle in new[] { false, true })
            {
                var cancel = new Replay(); cancel.Complete(); cancel.Desktop.Result = PasteAttempt.Pending; cancel.Tick(1550);
                if (toggle) cancel.Auto.SetEnabled(false); else { cancel.Auto.ForegroundChanged((IntPtr)999); cancel.Auto.ForegroundChanged((IntPtr)101); }
                cancel.Desktop.Result = PasteAttempt.Sent; cancel.Tick(1575);
                Assert(!cancel.Desktop.Pasting && cancel.Desktop.Pastes.Count == 0, "Focus change or disable must cancel the native sequence too.");
            }
        }

        static void SettingsDefaultOffPersistAndPreserveNeighbors()
        {
            string path = @"Software\WeTypeVoiceCapture\Tests\" + Guid.NewGuid().ToString("N");
            try
            {
                var settings = new AutoPasteSettings(path);
                Assert(!settings.Enabled, "Absent setting must default to off."); settings.SetEnabled(false);
                using (var missing = Registry.CurrentUser.OpenSubKey(path)) Assert(missing == null, "Reading/disabling must not create a key.");
                using (var key = Registry.CurrentUser.CreateSubKey(path)) { key.SetValue("Other", "keep"); key.SetValue("SunloginAutoPaste", "1"); }
                Assert(!settings.Enabled, "Malformed settings must fail closed.");
                settings.SetEnabled(true); Assert(new AutoPasteSettings(path).Enabled, "Opt-in must persist.");
                settings.SetEnabled(false); Assert(!new AutoPasteSettings(path).Enabled, "Opt-out must persist.");
                using (var key = Registry.CurrentUser.OpenSubKey(path)) Assert((string)key.GetValue("Other") == "keep", "Unrelated settings must survive.");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        }

        static int Main()
        {
            var tests = new Action[] { DefaultOffAndLocalOnlyCopy, RemoteCompletionPastesOnceAfterCopy,
                AwayAndBackDuringSpeechStaysCanceled, WindowSwitchAfterCopyAndOtherRemoteCancel,
                ToggleAndPauseDoNotRevivePendingRounds, StartupAndReconnectNeverPasteAnExistingRound,
                CanceledEmptyAndIdleRoundsNeverPaste, ConsecutiveIdenticalRoundsAndNewRoundSupersession,
                ClipboardCopyRetryDoesNotPasteEarlyOrTwice, PendingInputClipboardAndSendFailuresStaySafe,
                OnlyRecognizedRemoteWindowsQualify, KeyboardSequenceGuardsAndPartialCleanup,
                SplitSequenceCancellationReleasesOwnedKeys, InFlightPasteRetainsCancellationAndOwnInputGuard,
                SettingsDefaultOffPersistAndPreserveNeighbors };
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
