using System;
using System.Net;
using System.Net.Sockets;

namespace ExHyperV.Tools
{
    public class HyperVEndPoint : EndPoint
    {
        public Guid VmId { get; set; }
        public Guid ServiceId { get; set; }
        public override AddressFamily AddressFamily => (AddressFamily)34; // AF_HYPERV

        public HyperVEndPoint(Guid vmId, Guid serviceId)
        {
            VmId = vmId;
            ServiceId = serviceId;
        }

        public override SocketAddress Serialize()
        {
            // SOCKADDR_HV 结构体长度为 36 字节
            var sa = new SocketAddress(this.AddressFamily, 36);

            // 跳过 Family 字段，设置 Reserved 字段
            sa[2] = 0;
            sa[3] = 0;

            byte[] vmIdBytes = VmId.ToByteArray();
            byte[] svcIdBytes = ServiceId.ToByteArray();

            // 写入 VmId (偏移量 4, 16 字节)
            for (int i = 0; i < 16; i++) sa[4 + i] = vmIdBytes[i];

            // 写入 ServiceId (偏移量 20, 16 字节)
            for (int i = 0; i < 16; i++) sa[20 + i] = svcIdBytes[i];

            return sa;
        }
    }
}