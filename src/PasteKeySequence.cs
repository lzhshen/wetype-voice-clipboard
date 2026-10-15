using System;

namespace WeTypeVoiceCapture
{
    // Advances on the existing message-loop timer, never by sleeping on the UI
    // thread. Receivers that query async modifier state must see Ctrl still down
    // when they process V, rather than four events already released in one batch.
    sealed class PasteKeySequence : IDisposable
    {
        readonly Func<int, bool> down, mayRelease;
        readonly Func<PasteKeyboard.Input[], uint> send;
        readonly Func<bool> startGuard, interrupted;
        readonly Action stopGuard;
        readonly ushort vScan;
        readonly UIntPtr marker;
        int phase;
        bool controlOwned, vOwned;
        DateTime started, due;
        PasteAttempt outcome = PasteAttempt.Failed;

        public PasteKeySequence(ushort vScan, UIntPtr marker, Func<int, bool> down,
            Func<PasteKeyboard.Input[], uint> send, Func<bool> startGuard, Action stopGuard,
            Func<bool> interrupted, Func<int, bool> mayRelease)
        {
            this.vScan = vScan; this.marker = marker; this.down = down; this.send = send;
            this.startGuard = startGuard; this.stopGuard = stopGuard;
            this.interrupted = interrupted; this.mayRelease = mayRelease;
        }

        public bool Running { get { return phase == 1 || phase == 2; } }

        PasteKeyboard.Input Key(ushort code, ushort scan, bool up)
        {
            return new PasteKeyboard.Input { Type = 1, Data = new PasteKeyboard.InputData {
                Key = new PasteKeyboard.Key { Code = code, Scan = scan, Flags = 8u | (up ? 2u : 0u), Extra = marker } } };
        }

        bool OtherKeysDown()
        {
            foreach (int key in new[] { 0x10, 0x12, 0x5b, 0x5c, 0x56 }) if (down(key)) return true;
            return false;
        }

        PasteAttempt Finish(PasteAttempt result)
        {
            // Mark completed before cleanup: even re-entry must not send V again.
            phase = 3; outcome = result;
            try
            {
                if (vOwned)
                {
                    vOwned = false;
                    if (mayRelease(0x56) && send(new[] { Key(0x56, vScan, true) }) != 1) outcome = PasteAttempt.Failed;
                }
                if (controlOwned)
                {
                    controlOwned = false;
                    if (mayRelease(0xa2) && send(new[] { Key(0x11, 0x1d, true) }) != 1) outcome = PasteAttempt.Failed;
                }
            }
            finally { stopGuard(); }
            return outcome;
        }

        public PasteAttempt Step(DateTime now, Func<bool> stillSafe)
        {
            if (phase == 3) return outcome;
            if (phase == 0)
            {
                if (vScan == 0 || OtherKeysDown() || down(0x11) || !stillSafe() || !startGuard())
                    return Finish(PasteAttempt.Unsafe);
                // Recheck after installing guards to close the setup gap.
                if (interrupted() || OtherKeysDown() || down(0x11) || !stillSafe()) return Finish(PasteAttempt.Unsafe);
                controlOwned = send(new[] { Key(0x11, 0x1d, false) }) == 1;
                if (!controlOwned) return Finish(PasteAttempt.Failed);
                started = now; due = now.AddMilliseconds(100); phase = 1;
                return PasteAttempt.Pending;
            }
            if (interrupted() || !stillSafe()) return Finish(PasteAttempt.Unsafe);
            if ((now - started).TotalSeconds > 1) return Finish(PasteAttempt.Failed);
            if (now < due) return PasteAttempt.Pending;
            if (phase == 1)
            {
                if (!down(0x11) || OtherKeysDown()) return Finish(PasteAttempt.Unsafe);
                uint sent = send(new[] { Key(0x56, vScan, false), Key(0x56, vScan, true) });
                vOwned = sent == 1;
                if (sent != 2) return Finish(PasteAttempt.Failed);
                due = now.AddMilliseconds(100); phase = 2;
                return PasteAttempt.Pending;
            }
            return Finish(PasteAttempt.Sent);
        }

        public void Dispose() { if (phase != 3) Finish(PasteAttempt.Unsafe); }
    }
}
