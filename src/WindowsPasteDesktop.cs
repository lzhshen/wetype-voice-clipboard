using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WeTypeVoiceCapture
{
    // The only input synthesis in the program. The x64 INPUT union must include
    // MOUSEINPUT's size even though we only populate KEYBDINPUT.
    static class PasteKeyboard
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Key { public ushort Code, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct InputData { [FieldOffset(0)] public Key Key; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { public uint Type; public InputData Data; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint key, uint mode, IntPtr layout);

        static Input KeyEvent(ushort key, ushort scan, bool up)
        { return new Input { Type = 1, Data = new InputData { Key = new Key { Code = key, Scan = scan, Flags = 8u | (up ? 2u : 0u) } } }; }

        public static bool Send(Func<bool> stillSafe)
        {
            return Send(stillSafe, delegate(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; },
                delegate(Input[] inputs) { return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))); });
        }

        internal static bool Send(Func<bool> stillSafe, Func<int, bool> down, Func<Input[], uint> send)
        {
            // Never release keys held by the user, or turn Ctrl+V into another shortcut.
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x56 })
                if (down(key)) return false;
            uint process;
            IntPtr layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out process));
            uint scan = MapVirtualKeyEx(0x56, 0, layout);
            if (scan == 0 || scan > 0xff) return false;
            ushort vScan = (ushort)scan;
            if (!stillSafe()) return false;
            // Remote clients can forward scan codes rather than virtual keys.
            // VK-only SendInput produces scan=0 in real low-level events; use
            // physical left Ctrl and the foreground layout's V scan code.
            uint sent = send(new[] { KeyEvent(0x11, 0x1d, false), KeyEvent(0x56, vScan, false), KeyEvent(0x56, vScan, true), KeyEvent(0x11, 0x1d, true) });
            // Only releases are permitted after a partial insertion; never send V down again.
            if (sent > 0 && sent < 4) send(new[] { KeyEvent(0x56, vScan, true), KeyEvent(0x11, 0x1d, true) });
            return sent == 4;
        }
    }

    sealed class WindowsPasteDesktop : IPasteDesktop, IDisposable
    {
        delegate void EventCallback(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time);
        delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)]
        struct LastInput { public uint Size, Time; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder buffer, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder buffer, int length);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, EventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);

        readonly EventCallback callback;
        readonly Action<IntPtr> foregroundChanged;
        IntPtr foregroundHook, desktopHook;
        GCHandle callbackRoot;

        public WindowsPasteDesktop(Action<IntPtr> foregroundChanged)
        {
            this.foregroundChanged = foregroundChanged;
            callback = OnWindowEvent;
        }

        public bool StartMonitoring()
        {
            if (foregroundHook != IntPtr.Zero && desktopHook != IntPtr.Zero) return true;
            callbackRoot = GCHandle.Alloc(callback);
            // OUTOFCONTEXT: observe queued OS events on our message-loop thread.
            // Do not skip own-process events: opening the tray menu must cancel too.
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
            desktopHook = SetWinEventHook(0x20, 0x20, IntPtr.Zero, callback, 0, 0, 0);
            if (foregroundHook != IntPtr.Zero && desktopHook != IntPtr.Zero) return true;
            StopMonitoring(); return false;
        }

        void OnWindowEvent(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time)
        { foregroundChanged(kind == 3 ? window : IntPtr.Zero); }

        public void StopMonitoring()
        {
            if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
            if (desktopHook != IntPtr.Zero) UnhookWinEvent(desktopHook);
            foregroundHook = desktopHook = IntPtr.Zero;
            if (callbackRoot.IsAllocated) callbackRoot.Free();
        }

        static string WindowText(IntPtr window, bool className)
        {
            var buffer = new StringBuilder(512);
            if (className) GetClassName(window, buffer, buffer.Capacity);
            else GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        internal static bool IsRemoteShape(string processName, string windowClass, string title, bool pairedView)
        {
            // This allowlist is based on the observed AweSun Flutter client.
            // Its launcher uses FLUTTER_RUNNER_WIN32_WINDOW and is excluded.
            return String.Equals(processName, "AweSun", StringComparison.OrdinalIgnoreCase) &&
                (windowClass == "FlutterMultiWindow" || windowClass == "ORAY_FLUTTER_VIEW_CONTAINER") &&
                !String.IsNullOrWhiteSpace(title) && pairedView;
        }

        public RemotePasteTarget CaptureTarget()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return null;
            string windowClass = WindowText(window, true), title = WindowText(window, false);
            string companion = windowClass == "FlutterMultiWindow" ? "ORAY_FLUTTER_VIEW_CONTAINER" : "FlutterMultiWindow";
            if (windowClass != "FlutterMultiWindow" && windowClass != "ORAY_FLUTTER_VIEW_CONTAINER") return null;
            uint pid; GetWindowThreadProcessId(window, out pid);
            try
            {
                string processName;
                using (var process = Process.GetProcessById((int)pid)) processName = process.ProcessName;
                bool paired = false;
                EnumWindows(delegate(IntPtr other, IntPtr unused) {
                    uint otherPid; GetWindowThreadProcessId(other, out otherPid);
                    if (otherPid == pid && IsWindowVisible(other) && WindowText(other, true) == companion && WindowText(other, false) == title)
                        paired = true;
                    return true;
                }, IntPtr.Zero);
                if (!IsRemoteShape(processName, windowClass, title, paired)) return null;
                var target = new RemotePasteTarget(window, pid, title);
                return IsCurrent(target) ? target : null;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        public bool IsCurrent(RemotePasteTarget target)
        {
            if (GetForegroundWindow() != target.Window || !IsWindowVisible(target.Window) || IsIconic(target.Window)) return false;
            uint pid; GetWindowThreadProcessId(target.Window, out pid);
            return pid == target.ProcessId && WindowText(target.Window, false) == target.Title;
        }

        public uint? InputStamp
        {
            get
            {
                var info = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
                return GetLastInputInfo(ref info) ? (uint?)info.Time : null;
            }
        }

        public PasteAttempt Paste(RemotePasteTarget target, string text, uint inputStamp)
        {
            RemotePasteTarget current = CaptureTarget();
            if (current == null || current.Window != target.Window || current.ProcessId != target.ProcessId ||
                current.Title != target.Title || InputStamp != inputStamp) return PasteAttempt.Unsafe;
            uint sequence;
            if (!OpenClipboard(IntPtr.Zero)) return PasteAttempt.ClipboardBusy;
            try
            {
                sequence = GetClipboardSequenceNumber();
                IntPtr memory = GetClipboardData(13);
                if (memory == IntPtr.Zero || GlobalSize(memory).ToUInt64() < ((ulong)text.Length + 1) * 2) return PasteAttempt.Unsafe;
                IntPtr buffer = GlobalLock(memory);
                if (buffer == IntPtr.Zero) return PasteAttempt.Unsafe;
                try
                {
                    if (Marshal.PtrToStringUni(buffer, text.Length + 1) != text + "\0") return PasteAttempt.Unsafe;
                }
                finally { GlobalUnlock(memory); }
            }
            finally { CloseClipboard(); }
            bool sent = PasteKeyboard.Send(delegate {
                return IsCurrent(target) && InputStamp == inputStamp && GetClipboardSequenceNumber() == sequence;
            });
            return sent ? PasteAttempt.Sent : PasteAttempt.Failed;
        }

        public void Dispose() { StopMonitoring(); }
    }
}
