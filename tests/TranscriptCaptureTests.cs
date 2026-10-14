using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WeTypeVoiceCapture
{
    static class TranscriptCaptureTests
    {
        sealed class Replay
        {
            public readonly TranscriptCapture Capture;
            public readonly List<string> Writes = new List<string>();
            public string Clipboard = "previous transcript";
            public bool Busy;
            public DateTime Now;

            public Replay()
            {
                Capture = new TranscriptCapture(delegate(string text) {
                    if (Busy) throw new ExternalException("Clipboard locked");
                    Clipboard = text; Writes.Add(text);
                }, delegate(string status) { });
            }

            public void Feed(int milliseconds, int state, string text, string session = "one",
                bool canceled = false, bool stop = false, bool finished = true, ulong engine = 2)
            {
                Now = new DateTime(2026, 10, 14).AddMilliseconds(milliseconds);
                Capture.Observe(new Frame { State = state, Text = text, Session = session,
                    Finished = finished, CancelPending = canceled, StopPending = stop,
                    Manager = 1, Engine = engine }, Now);
            }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void LatestWithoutStoppingFrame()
        {
            var replay = new Replay();
            replay.Feed(0, 3, "previous transcript");
            replay.Feed(200, 1, "");
            replay.Feed(225, 3, "latest transcript");
            replay.Feed(850, 3, "latest transcript");
            Assert(replay.Clipboard == "latest transcript",
                "STALE PASTE: completed latest transcript was skipped when the stopping frame fell between polls.");
            replay.Feed(1200, 3, "latest transcript");
            Assert(replay.Writes.Count == 1, "A completed session must copy exactly once.");
        }

        static void NormalCompletionAndIdenticalNextTranscript()
        {
            var replay = new Replay();
            replay.Feed(0, 1, "");
            replay.Feed(100, 2, "", stop: true);
            replay.Feed(125, 3, "same text");
            replay.Feed(750, 3, "same text");
            replay.Feed(1000, 1, "", session: "two");
            replay.Feed(1100, 2, "", session: "two");
            replay.Feed(1125, 3, "same text", session: "two");
            replay.Feed(1750, 3, "same text", session: "two");
            Assert(replay.Writes.Count == 2 && replay.Clipboard == "same text",
                "Consecutive sessions with identical text must each copy once.");
        }

        static void StartupResumeAndReconnectSkipOldText()
        {
            var replay = new Replay();
            replay.Feed(0, 3, "old text");
            replay.Feed(1000, 3, "old text");
            replay.Capture.Reset();
            replay.Feed(2000, 3, "old text");
            replay.Feed(3000, 3, "old text");
            replay.Feed(4000, 3, "other engine's old text", engine: 9);
            replay.Feed(5000, 3, "other engine's old text", engine: 9);
            Assert(replay.Writes.Count == 0, "Startup, resume and reconnect must skip existing final text.");
        }

        static void CancellationIsLatched()
        {
            foreach (bool observeStop in new[] { false, true })
            {
                var replay = new Replay();
                replay.Feed(0, 1, "");
                replay.Feed(100, observeStop ? 2 : 1, "", canceled: true);
                replay.Feed(125, 3, "canceled text");
                replay.Feed(750, 3, "canceled text");
                Assert(replay.Writes.Count == 0, "Cancellation must be remembered after its flag clears.");
            }
        }

        static void FinalTextMustFinishAndStabilize()
        {
            var replay = new Replay();
            replay.Feed(0, 1, "");
            replay.Feed(100, 2, "");
            replay.Feed(125, 3, "draft", finished: false);
            replay.Feed(1000, 3, "draft", finished: false);
            Assert(replay.Writes.Count == 0, "Unfinished text must not copy.");
            replay.Feed(1100, 3, "draft");
            replay.Feed(1600, 3, "latest transcript");
            replay.Feed(2199, 3, "latest transcript");
            Assert(replay.Writes.Count == 0, "A changed final result must restart the stability window.");
            replay.Feed(2200, 3, "latest transcript");
            Assert(replay.Clipboard == "latest transcript", "Only the stable final text may copy.");
        }

        static void NewSessionDiscardsPendingOldResult()
        {
            var replay = new Replay();
            replay.Feed(0, 1, "");
            replay.Feed(100, 2, "");
            replay.Feed(125, 3, "superseded");
            replay.Feed(200, 1, "", session: "two");
            replay.Feed(225, 3, "latest transcript", session: "two");
            replay.Feed(850, 3, "latest transcript", session: "two");
            Assert(replay.Writes.Count == 1 && replay.Clipboard == "latest transcript",
                "Starting a new session must discard the previous session's pending result.");
        }

        static void IdleEmptyAndChangedObjectsDoNotCopy()
        {
            var replay = new Replay();
            replay.Feed(0, 1, "");
            replay.Feed(100, 0, "");
            replay.Feed(125, 3, "stale after idle");
            replay.Feed(750, 3, "stale after idle");
            replay.Feed(1000, 1, "");
            replay.Feed(1100, 2, "");
            replay.Feed(1125, 3, " \t");
            replay.Feed(1750, 3, " \t");
            replay.Feed(2000, 1, "");
            replay.Feed(2100, 2, "");
            replay.Feed(2125, 3, "unrelated completed result", engine: 9);
            replay.Feed(2750, 3, "unrelated completed result", engine: 9);
            Assert(replay.Writes.Count == 0, "Idle, empty results and replacement objects must not copy.");
        }

        static void BusyClipboardRetriesAndTimesOut()
        {
            var replay = new Replay();
            replay.Feed(0, 1, "");
            replay.Feed(100, 2, "");
            replay.Feed(125, 3, "latest transcript");
            replay.Busy = true;
            try { replay.Feed(750, 3, "latest transcript"); throw new Exception("Expected clipboard lock."); }
            catch (ExternalException) { replay.Capture.ClipboardBusy(replay.Now); }
            Assert(replay.Capture.Armed, "A temporary clipboard lock must leave the latest result pending.");
            replay.Busy = false;
            replay.Feed(800, 3, "latest transcript");
            replay.Feed(900, 3, "latest transcript");
            Assert(replay.Writes.Count == 1 && replay.Clipboard == "latest transcript", "Clipboard retry lost the latest result.");

            replay.Feed(1000, 1, "", session: "two");
            replay.Feed(1100, 2, "", session: "two");
            replay.Feed(1125, 3, "blocked", session: "two");
            replay.Busy = true;
            try { replay.Feed(10000, 3, "blocked", session: "two"); throw new Exception("Expected clipboard lock."); }
            catch (ExternalException) { replay.Capture.ClipboardBusy(replay.Now); }
            Assert(!replay.Capture.Armed, "A persistent clipboard lock must time out.");
        }

        static int Main()
        {
            var tests = new Action[] { LatestWithoutStoppingFrame, NormalCompletionAndIdenticalNextTranscript,
                StartupResumeAndReconnectSkipOldText, CancellationIsLatched, FinalTextMustFinishAndStabilize,
                NewSessionDiscardsPendingOldResult, IdleEmptyAndChangedObjectsDoNotCopy, BusyClipboardRetriesAndTimesOut };
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
