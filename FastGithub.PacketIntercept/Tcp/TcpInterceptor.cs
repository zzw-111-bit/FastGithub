﻿using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using WinDivertSharp;

namespace FastGithub.PacketIntercept.Tcp
{
    /// <summary>
    /// tcp拦截器
    /// </summary>   
    [SupportedOSPlatform("windows")]
    abstract class TcpInterceptor : ITcpInterceptor
    {
        private readonly string filter;
        private readonly ushort oldServerPort;
        private readonly ushort newServerPort;
        private readonly ILogger logger;

        /// <summary>
        /// tcp拦截器
        /// </summary>
        /// <param name="oldServerPort">修改前的服务器端口</param>
        /// <param name="newServerPort">修改后的服务器端口</param>
        /// <param name="logger"></param>
        public TcpInterceptor(int oldServerPort, int newServerPort, ILogger logger)
        {
            this.filter = $"loopback and (tcp.DstPort == {oldServerPort} or tcp.SrcPort == {newServerPort})";
            this.oldServerPort = BinaryPrimitives.ReverseEndianness((ushort)oldServerPort);
            this.newServerPort = BinaryPrimitives.ReverseEndianness((ushort)newServerPort);
            this.logger = logger;
        }

        /// <summary>
        /// 拦截指定端口的数据包
        /// </summary>
        /// <param name="cancellationToken"></param>
        public async Task InterceptAsync(CancellationToken cancellationToken)
        {
            if (this.oldServerPort == this.newServerPort)
            {
                return;
            }

            await Task.Yield();

            var handle = WinDivert.WinDivertOpen(this.filter, WinDivertLayer.Network, 0, WinDivertOpenFlags.None);
            if (handle == IntPtr.MaxValue || handle == IntPtr.Zero)
            {
                this.logger.LogError($"打开驱动失败");
                return;
            }

            this.logger.LogInformation($"tcp://{IPAddress.Loopback}:{BinaryPrimitives.ReverseEndianness(this.oldServerPort)} => tcp://{IPAddress.Loopback}:{BinaryPrimitives.ReverseEndianness(this.newServerPort)}");
            cancellationToken.Register(hwnd => WinDivert.WinDivertClose((IntPtr)hwnd!), handle);

            var packetLength = 0U;
            using var winDivertBuffer = new WinDivertBuffer();
            var winDivertAddress = new WinDivertAddress();

            while (cancellationToken.IsCancellationRequested == false)
            {
                if (WinDivert.WinDivertRecv(handle, winDivertBuffer, ref winDivertAddress, ref packetLength))
                {
                    try
                    {
                        this.ModifyTcpPacket(winDivertBuffer, ref winDivertAddress, ref packetLength);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex.Message);
                    }
                    finally
                    {
                        WinDivert.WinDivertSend(handle, winDivertBuffer, packetLength, ref winDivertAddress);
                    }
                }
                else
                {
                    var exception = new Win32Exception(Marshal.GetLastWin32Error());
                    this.logger.LogError(exception.Message);
                }
            }
        }

        /// <summary>
        /// 修改tcp数据端口的端口
        /// </summary>
        /// <param name="winDivertBuffer"></param>
        /// <param name="winDivertAddress"></param>
        /// <param name="packetLength"></param> 
        unsafe private void ModifyTcpPacket(WinDivertBuffer winDivertBuffer, ref WinDivertAddress winDivertAddress, ref uint packetLength)
        {
            var packet = WinDivert.WinDivertHelperParsePacket(winDivertBuffer, packetLength);
            if (packet.TcpHeader->DstPort == oldServerPort)
            {
                packet.TcpHeader->DstPort = this.newServerPort;
            }
            else
            {
                packet.TcpHeader->SrcPort = oldServerPort;
            }
            winDivertAddress.Impostor = true;
            WinDivert.WinDivertHelperCalcChecksums(winDivertBuffer, packetLength, ref winDivertAddress, WinDivertChecksumHelperParam.All);
        }
    }
}