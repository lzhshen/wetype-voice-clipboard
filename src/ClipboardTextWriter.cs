using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WeTypeVoiceCapture
{
    // Publish fully materialized Unicode text in one clipboard transaction. OLE's
    // SetDataObject + Flush path closes/reopens the clipboard and can notify a
    // remote clipboard listener while the second phase still holds it open.
    sealed class ClipboardTextWriter : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseClipboard();
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr memory);

        public ClipboardTextWriter()
        {
            // A real owner handle is required by EmptyClipboard/SetClipboardData.
            // A message-only window never activates or takes input focus.
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        static ExternalException ClipboardError()
        {
            return new ExternalException("无法写入剪贴板", Marshal.GetHRForLastWin32Error());
        }

        public void SetText(string text)
        {
            if (Handle == IntPtr.Zero) throw new ObjectDisposedException("ClipboardTextWriter");
            if (text == null) throw new ArgumentNullException("text");
            if (text.IndexOf('\0') >= 0) throw new ArgumentException("文本包含无效字符", "text");
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr memory = GlobalAlloc(0x42, (UIntPtr)bytes.Length); // MOVEABLE | ZEROINIT
            if (memory == IntPtr.Zero) throw ClipboardError();
            try
            {
                IntPtr buffer = GlobalLock(memory);
                if (buffer == IntPtr.Zero) throw ClipboardError();
                try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
                finally { GlobalUnlock(memory); }

                // Prepare memory before opening, so failures or clipboard contention
                // leave the previous contents intact. TranscriptCapture retries busy writes.
                if (!OpenClipboard(Handle)) throw ClipboardError();
                try
                {
                    if (!EmptyClipboard()) throw ClipboardError();
                    if (SetClipboardData(13, memory) == IntPtr.Zero) throw ClipboardError(); // CF_UNICODETEXT
                    memory = IntPtr.Zero; // Windows owns the allocation after success.
                }
                finally
                {
                    if (!CloseClipboard()) throw ClipboardError();
                }
            }
            finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
