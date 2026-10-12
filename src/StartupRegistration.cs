using System;
using System.IO;
using Microsoft.Win32;

namespace WeTypeVoiceCapture
{
    // The Run entry is the setting: no separate preference can drift out of sync.
    sealed class StartupRegistration
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "WeTypeVoiceCapture";
        readonly string keyPath, command;

        public StartupRegistration(string executablePath) : this(executablePath, RunKeyPath) { }

        internal StartupRegistration(string executablePath, string registryKeyPath)
        {
            keyPath = registryKeyPath;
            command = "\"" + Path.GetFullPath(executablePath) + "\"";
        }

        public bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                    return key != null && key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) != null;
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    if (key == null) throw new IOException("无法打开当前用户的启动设置");
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    if (key != null) key.DeleteValue(ValueName, false);
            }
        }

        public void RefreshExecutablePath()
        {
            // Keep an existing opt-in when a portable copy is moved or upgraded.
            // Never create an entry for a user who has not enabled startup.
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                object value = key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null || String.Equals(value as string, command, StringComparison.OrdinalIgnoreCase)) return;
            }
            SetEnabled(true);
        }
    }
}
