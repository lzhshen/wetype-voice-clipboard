using System;
using System.IO;
using Microsoft.Win32;

namespace WeTypeVoiceCapture
{
    sealed class AutoPasteSettings
    {
        readonly string keyPath;
        const string ValueName = "SunloginAutoPaste";
        public AutoPasteSettings() : this(@"Software\WeTypeVoiceCapture") { }
        internal AutoPasteSettings(string path) { keyPath = path; }
        public bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    object value = key == null ? null : key.GetValue(ValueName);
                    return value is int && (int)value == 1;
                }
            }
        }
        public void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    if (key != null) key.DeleteValue(ValueName, false);
                return;
            }
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (key == null) throw new IOException("无法打开自动粘贴设置");
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            }
        }
    }
}
