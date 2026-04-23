using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TrayMirror
{
    public class SystemInfoProvider
    {
        // ── 音量 ────────────────────────────────────────────────────
        [DllImport("winmm.dll")]
        private static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);

        public int GetVolumePercent()
        {
            try
            {
                waveOutGetVolume(IntPtr.Zero, out uint vol);
                // 低 16 位是左声道音量，0xFFFF = 100%
                return (int)((vol & 0xFFFF) * 100 / 0xFFFF);
            }
            catch { return -1; }
        }

        // ── 网络 ─────────────────────────────────────────────────────
        public bool IsNetworkAvailable()
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch { return false; }
        }

        public string GetNetworkLabel()
        {
            if (!IsNetworkAvailable()) return "离线";
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "WiFi";
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) return "以太网";
            }
            return "已连接";
        }

        // ── 时间 ─────────────────────────────────────────────────────
        public string GetTimeString() => DateTime.Now.ToString("HH:mm");
        public string GetDateString() => DateTime.Now.ToString("MM/dd ddd");
    }
}