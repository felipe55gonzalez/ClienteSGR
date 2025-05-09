using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ClienteSGR
{
    public static class WinTun
    {
        [DllImport("wintun.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr WintunCreateAdapter(string name, string tunnelType, string requestedGUID);

        [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern void WintunCloseAdapter(IntPtr adapter);

        [DllImport("wintun.dll")]
        public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

        [DllImport("wintun.dll")]
        public static extern void WintunEndSession(IntPtr session);

        [DllImport("wintun.dll")]
        public static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

        [DllImport("wintun.dll")]
        public static extern void WintunSendPacket(IntPtr session, IntPtr packet);

        [DllImport("wintun.dll")]
        public static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

        [DllImport("wintun.dll")]
        public static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    }
}
