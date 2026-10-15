using System;

namespace WeTypeVoiceCapture
{
    sealed class RemotePasteTarget
    {
        public readonly IntPtr Window;
        public readonly uint ProcessId;
        public readonly string Title;
        public RemotePasteTarget(IntPtr window, uint processId, string title)
        { Window = window; ProcessId = processId; Title = title; }
    }

    enum PasteAttempt { Sent, ClipboardBusy, Unsafe, Failed, Pending }

    interface IPasteDesktop
    {
        RemotePasteTarget CaptureTarget();
        bool IsCurrent(RemotePasteTarget target);
        uint? InputStamp { get; }
        bool Pasting { get; }
        void CancelPaste();
        PasteAttempt Paste(RemotePasteTarget target, string text, uint inputStamp);
    }

    // Owns one round's permission to paste. A cancellation is latched until a
    // newly observed round, including an away-and-back foreground transition.
    sealed class AutoPasteController
    {
        readonly IPasteDesktop desktop;
        readonly Action<string> status;
        RemotePasteTarget target;
        string text;
        uint inputStamp;
        DateTime due, deadline;
        bool enabled, awaitingCopy;

        public AutoPasteController(IPasteDesktop desktop, Action<string> status)
        { this.desktop = desktop; this.status = status; }

        public bool Pending { get { return text != null; } }
        public bool Enabled { get { return enabled; } }
        public int TriggeredCount { get; private set; }

        public void SetEnabled(bool value) { Cancel(); enabled = value; }

        public void Cancel()
        { desktop.CancelPaste(); target = null; text = null; awaitingCopy = false; }

        public void BeginRound(bool observedStart)
        {
            Cancel();
            // Attaching/resuming in the middle of dictation cannot establish
            // which window was foreground when the user began that round.
            if (!enabled || !observedStart) return;
            target = desktop.CaptureTarget();
            awaitingCopy = target != null;
        }

        public void ForegroundChanged(IntPtr window)
        {
            if (target != null && target.Window != window)
                CancelWithStatus("窗口已切换");
        }

        public void Copied(string value, DateTime now)
        {
            if (!awaitingCopy || target == null) return;
            awaitingCopy = false;
            uint? stamp = desktop.InputStamp;
            if (!desktop.IsCurrent(target) || !stamp.HasValue) { Cancel(); return; }
            text = value; inputStamp = stamp.Value;
            // Give the remote clipboard a settling interval. This is not a
            // remote acknowledgement; never retry an already injected paste.
            due = now.AddMilliseconds(750);
            deadline = due.AddSeconds(2);
        }

        void CancelWithStatus(string reason)
        {
            bool wasPending = Pending;
            Cancel();
            if (wasPending) status("已复制；" + reason + "，自动粘贴已停止");
        }

        public void Tick(DateTime now)
        {
            if (target == null) return;
            if (!desktop.IsCurrent(target)) { CancelWithStatus("窗口已切换"); return; }
            if (!Pending) return;
            // Any new user input after copying also cancels: this catches a
            // manual paste, a click into another field, and modifier presses.
            if (!desktop.Pasting && desktop.InputStamp != inputStamp) { CancelWithStatus("检测到新操作"); return; }
            if (now < due) return;
            if (now > deadline) { CancelWithStatus("自动粘贴等待超时"); return; }
            PasteAttempt result = desktop.Paste(target, text, inputStamp);
            if (result == PasteAttempt.ClipboardBusy || result == PasteAttempt.Pending) return;
            Cancel(); // Even a partial/failed SendInput is never attempted twice.
            if (result == PasteAttempt.Sent) TriggeredCount++;
            status(result == PasteAttempt.Sent ? "已触发向日葵粘贴，等待下一轮" :
                result == PasteAttempt.Unsafe ? "已复制；自动粘贴已取消，请手动粘贴" :
                "已复制；粘贴触发未确认，请检查输入框");
        }
    }
}
