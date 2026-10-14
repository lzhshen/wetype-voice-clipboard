using System;

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

    // The same session logic is used by the tray application and frame replay tests.
    sealed class TranscriptCapture
    {
        readonly Action<string> copy, status;
        Frame previous;
        bool armed, canceled;
        string candidate;
        DateTime stableSince;

        public TranscriptCapture(Action<string> copy, Action<string> status)
        {
            this.copy = copy;
            this.status = status;
        }

        public bool Armed { get { return armed; } }

        public void Reset()
        {
            previous = null;
            armed = false;
            candidate = null;
        }

        public void Observe(Frame frame, DateTime now)
        {
            if (previous == null || previous.Engine != frame.Engine || previous.Manager != frame.Manager)
            {
                // Completed text left over before startup/resume is never copied.
                armed = frame.Active;
                canceled = frame.CancelPending; candidate = null;
            }
            else if (frame.Active && (!previous.Active ||
                (frame.State == 1 && previous.State == 2) ||
                (frame.Session.Length > 0 && previous.Session.Length > 0 && frame.Session != previous.Session)))
            {
                armed = true; canceled = false; candidate = null;
            }
            if (armed)
            {
                canceled |= frame.CancelPending;
                if (frame.Active) status(frame.State == 1 ? "正在识别，等待结束" : "正在等待最终结果");
                if (frame.State == 3 && frame.Finished)
                {
                    if (candidate != frame.Text) { candidate = frame.Text; stableSince = now; }
                    else if ((now - stableSince).TotalMilliseconds >= 600)
                    {
                        // State 2 / StopPending can begin and end between timer polls.
                        // An observed active round followed by a stable, finished result
                        // is sufficient; requiring that transient stop loses the round.
                        if (!canceled && !String.IsNullOrWhiteSpace(candidate))
                        {
                            copy(candidate);
                            status("已复制 " + candidate.Length + " 字，等待下一轮");
                        }
                        else status(canceled ? "本轮已取消，未复制" : "本轮没有可复制的最终结果");
                        armed = false; candidate = null;
                    }
                }
                else if (frame.State == 0) { armed = false; candidate = null; status("本轮结束，未复制"); }
            }
            previous = frame;
        }

        public void ClipboardBusy(DateTime now)
        {
            if ((now - stableSince).TotalSeconds > 8)
            { armed = false; candidate = null; status("剪贴板持续被占用，本轮未复制"); }
            else status("剪贴板正忙，正在重试");
        }
    }
}
