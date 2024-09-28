using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using static Quiche.NativeMethods;

namespace Quiche.NET;

public unsafe class QuicheConnection
{
    private Conn* nativePtr;

    private Socket dgramSocket;

    private readonly ConcurrentQueue<(long, byte[])> sendQueue;

    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;


    private QuicheConnection(Conn* nativePtr, Socket dgramSocket)
    {
        this.nativePtr = nativePtr;
        this.dgramSocket = dgramSocket;

        sendQueue = new();
        streamMap = new();

        _ = Task.Run(ReceiveDriver);
    }

    private class SendScheduleInfo
    {
        public int sendSize;
        public byte[] sendBuffer;
        public SocketAddress sendAddr;
    }

    private void SendPacket(object? state)
    {
        if (state is null) return;
        SendScheduleInfo info = (SendScheduleInfo)state;

        fixed (byte* pktPtr = info.sendBuffer)
        {
            ReadOnlySpan<byte> sendBuf = new(pktPtr, info.sendSize);
            dgramSocket.SendTo(sendBuf, SocketFlags.None, info.sendAddr);
        }
    }

    private void SendDriver()
    {
        byte[] packetBuf = ArrayPool<byte>.Shared.Rent(512);
        SendScheduleInfo info = new() { sendBuffer = packetBuf };
        System.Threading.Timer timer = new Timer(SendPacket, info, 0, Timeout.Infinite);

        for (; ; )
        {
            if (sendQueue.TryDequeue(out (long streamId, byte[] buf) pair))
            {
                fixed (byte* bufPtr = pair.buf)
                {
                    long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                    quiche_conn_stream_send(nativePtr,
                        (ulong)pair.streamId, bufPtr,
                        (nuint)pair.buf.Length, false /* TODO: Figure out when streams close! */,
                        (ulong*)Unsafe.AsPointer(ref errorCode));
                }

                SendInfo sendInfo = default;
                long sendCount;
                fixed (byte* pktPtr = info.sendBuffer)
                {
                    sendCount = (long)quiche_conn_send(nativePtr, pktPtr, 
                        (nuint)packetBuf.Length, (SendInfo*)Unsafe.AsPointer(ref sendInfo));
                }

                SocketAddress sendAddr = new((AddressFamily)sendInfo.to.ss_family, sendInfo.to_len);
                Span<byte> sendAddrSpan = new Span<byte>((byte*)Unsafe.AsPointer(ref sendInfo.to), sendInfo.to_len);
                sendAddrSpan.CopyTo(sendAddr.Buffer.Span);

                info.sendSize = (int)sendCount;
                info.sendAddr = sendAddr;

                timer.Change(TimeSpan.FromTicks(sendInfo.at.tv_nsec.Value / 10), Timeout.InfiniteTimeSpan);
            }
        }
        // TODO: break loop at some point
        ArrayPool<byte>.Shared.Return(packetBuf);
    }

    private void ReceiveDriver()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(512);

        for (; ; )
        {
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.None, 0);
            int readCount = dgramSocket.ReceiveFrom(buffer, ref remoteEndPoint);

            var (to, to_len) = GetSocketAddress(dgramSocket.LocalEndPoint);
            var (from, from_len) = GetSocketAddress(remoteEndPoint);

            RecvInfo recvInfo = new RecvInfo
            {
                to = (sockaddr*)to.Pointer,
                to_len = to_len,

                from = (sockaddr*)from.Pointer,
                from_len = from_len,
            };

            long streamId;
            long recvCount;
            fixed (byte* bufPtr = buffer)
            {
                using (to)
                using (from)
                {
                    recvCount = (long)quiche_conn_recv(nativePtr, bufPtr,
                        (nuint)readCount, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                }

                bool flag;
                streamId = quiche_conn_stream_readable_next(nativePtr);
                if ((flag = streamId < 0) && (streamId = quiche_conn_stream_writable_next(nativePtr)) < 0)
                {
                    SendInfo sendInfo = default;
                    long sendCount = (long)quiche_conn_send(nativePtr, bufPtr, (nuint)buffer.Length,
                        (SendInfo*)Unsafe.AsPointer(ref sendInfo));

                    SocketAddress sendAddr = new((AddressFamily)sendInfo.to.ss_family, sendInfo.to_len);
                    Span<byte> sendAddrSpan = new Span<byte>((byte*)Unsafe.AsPointer(ref sendInfo.to), sendInfo.to_len);
                    sendAddrSpan.CopyTo(sendAddr.Buffer.Span);

                    ReadOnlySpan<byte> sendBuf = new(bufPtr, (int)sendCount);
                    dgramSocket.SendTo(sendBuf, SocketFlags.None, sendAddr);
                    continue;
                }

                if (!flag)
                {
                    bool streamFinished = false;
                    long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                    recvCount = quiche_conn_stream_recv(nativePtr, (ulong)streamId, bufPtr, (nuint)recvCount,
                        (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref errorCode));
                    QuicheException.ThrowIfError((QuicheError)errorCode, "An unexpected error occured in quiche!");

                    QuicheStream stream = streamMap.GetOrAdd(streamId, id => new(this, streamId));
                    _ = stream.BufferDataReceiveAsync(buffer, streamFinished);
                }
            }
        }
    }

    private static (MemoryHandle, int) GetSocketAddress(EndPoint? endPoint)
    {
        Memory<byte>? buf = endPoint?.Serialize().Buffer;
        return buf is null ? default : (buf.Value.Pin(), buf.Value.Length);
    }

    public static QuicheConnection Accept(QuicheConfig config)
    {
        Socket localSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
        Socket remoteSocket = localSocket.Accept();

        var (local, local_len) = GetSocketAddress(localSocket.LocalEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteSocket.RemoteEndPoint);

        using (local)
        using (remote)
        {
            return new(quiche_accept(null, 0, null, 0,
                (sockaddr*)local.Pointer, local_len,
                (sockaddr*)remote.Pointer, remote_len,
                config.NativePtr), localSocket);
        }
    }
}
