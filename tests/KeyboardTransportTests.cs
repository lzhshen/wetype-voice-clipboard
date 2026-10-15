using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WeTypeVoiceCapture
{
    // Native integration check for remote forwarding: virtual-key-only input can
    // reach local apps while omitting the physical scan codes remote clients use.
    // Sends Ctrl/V only while this test's own empty window is foreground.
    static class KeyboardTransportTests
    {
        delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        struct Event { public uint Code, Scan, Flags, Time; public UIntPtr Extra; }
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int kind, HookCallback callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        sealed class Receiver : Form
        {
            public bool? ControlAtV;
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x100 && message.WParam == (IntPtr)0x56)
                    ControlAtV = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                base.WndProc(ref message);
            }
        }
        static readonly List<Event> events = new List<Event>();
        static readonly HookCallback callback = Observe;
        static IntPtr hook, target;
        static IntPtr Observe(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && GetForegroundWindow() == target)
            {
                var item = (Event)Marshal.PtrToStructure(data, typeof(Event));
                if ((item.Flags & 0x10) != 0 && (item.Code == 0xa2 || item.Code == 0xa3 || item.Code == 0x11 || item.Code == 0x56)) events.Add(item);
            }
            return CallNextHookEx(hook, code, message, data);
        }
        [STAThread] static int Main(string[] args)
        {
            if (args.Length != 1 || args[0] != "--desktop-input") return 2;
            int result = 1, step = 0;
            using (var form = new Receiver { Text = "Voice clipboard keyboard transport test", Width = 420, Height = 160 })
            using (var timer = new Timer { Interval = 500 })
            {
                form.Controls.Add(new Label { Text = "Native Ctrl/V transport check. No text is entered or read.", Dock = DockStyle.Fill });
                form.Shown += delegate {
                    target = form.Handle;
                    hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                    form.Activate(); timer.Start();
                };
                timer.Tick += delegate {
                    if (step++ == 0)
                    {
                        if (hook == IntPtr.Zero || GetForegroundWindow() != target ||
                            !PasteKeyboard.Send(delegate { return GetForegroundWindow() == target; }))
                        { Console.Error.WriteLine("FAIL: own test window or input hook unavailable; no unguarded input sent."); form.Close(); }
                    }
                    else
                    {
                        foreach (var item in events) Console.WriteLine("native key={0:X2} scan={1:X2} flags={2:X2}", item.Code, item.Scan, item.Flags);
                        Console.WriteLine("Receiver async Ctrl state while processing V: " + form.ControlAtV);
                        bool valid = events.Count == 4 && events[0].Scan == 0x1d && events[1].Scan == 0x2f && events[2].Scan == 0x2f && events[3].Scan == 0x1d &&
                            (events[0].Flags & 0x80) == 0 && (events[1].Flags & 0x80) == 0 && (events[2].Flags & 0x80) != 0 && (events[3].Flags & 0x80) != 0;
                        Console.WriteLine(valid ? "PASS: native Ctrl/V events retain physical scan codes and balanced releases." : "FAIL: native Ctrl/V events lack the physical key identity required for remote forwarding.");
                        if (form.ControlAtV != true) Console.Error.WriteLine("FAIL: Ctrl was already released when the receiving window processed V.");
                        result = valid && form.ControlAtV == true ? 0 : 1; form.Close();
                    }
                };
                try { Application.Run(form); }
                finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }
            }
            return result;
        }
    }
}
