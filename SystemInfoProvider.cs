using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TrayMirror
{
    public class SystemInfoProvider
    {
        private const int CLSCTX_ALL = 23;

        public int GetVolumePercent()
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioEndpointVolume? endpointVolume = null;

            try
            {
                enumerator = new MMDeviceEnumeratorComObject() as IMMDeviceEnumerator;
                if (enumerator == null)
                    return -1;

                Marshal.ThrowExceptionForHR(
                    enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object endpointVolumeObj));

                endpointVolume = (IAudioEndpointVolume)endpointVolumeObj;
                Marshal.ThrowExceptionForHR(endpointVolume.GetMute(out bool isMuted));
                if (isMuted)
                    return 0;

                Marshal.ThrowExceptionForHR(endpointVolume.GetMasterVolumeLevelScalar(out float level));
                return (int)Math.Round(level * 100, MidpointRounding.AwayFromZero);
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

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

        public string GetTimeString() => DateTime.Now.ToString("HH:mm");
        public string GetDateString() => DateTime.Now.ToString("MM/dd ddd");

        private enum EDataFlow
        {
            eRender,
            eCapture,
            eAll,
        }

        private enum ERole
        {
            eConsole,
            eMultimedia,
            eCommunications,
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out object devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out uint channelCount);
            int SetMasterVolumeLevel(float levelDB, Guid eventContext);
            int SetMasterVolumeLevelScalar(float level, Guid eventContext);
            int GetMasterVolumeLevel(out float levelDB);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint channelNumber, float levelDB, Guid eventContext);
            int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);
            int GetChannelVolumeLevel(uint channelNumber, out float levelDB);
            int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, Guid eventContext);
            int GetMute(out bool isMuted);
            int GetVolumeStepInfo(out uint step, out uint stepCount);
            int VolumeStepUp(Guid eventContext);
            int VolumeStepDown(Guid eventContext);
            int QueryHardwareSupport(out uint hardwareSupportMask);
            int GetVolumeRange(out float volumeMindB, out float volumeMaxdB, out float volumeIncrementdB);
        }
    }
}
