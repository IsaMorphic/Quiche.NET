using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using static Quiche.NativeMethods;

namespace Quiche.NET;

public unsafe class QuicheConnection : IDisposable
{
    private readonly Task recvTask, sendTask;
    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;
    private readonly Socket dgramSocket;

    internal readonly ConcurrentQueue<(long, byte[])> sendQueue;
    internal Conn* NativePtr { get; private set; }

    private QuicheConnection(Conn* nativePtr, Socket dgramSocket)
    {
        NativePtr = nativePtr;
        this.dgramSocket = dgramSocket;

        sendQueue = new();
        streamMap = new();

        recvTask = Task.Run(ReceiveDriver);
        sendTask = Task.Run(SendDriver);
    }

    private class SendScheduleInfo
    {
        public int SendSize { get; set; }
        public byte[]? SendBuffer { get; set; }
        public SocketAddress? SendAddr { get; set; }
    }

    private void SendPacket(object? state)
    {
        SendScheduleInfo? info = state as SendScheduleInfo;
        if (info?.SendSize is null || info?.SendBuffer is null || info?.SendAddr is null) return;

        fixed (byte* pktPtr = info.SendBuffer)
        {
            ReadOnlySpan<byte> sendBuf = new(pktPtr, info.SendSize);
            dgramSocket.SendTo(sendBuf, SocketFlags.None, info.SendAddr);
        }
    }

    private void SendDriver()
    {
        byte[] packetBuf = ArrayPool<byte>.Shared.Rent(1024);

        SendScheduleInfo info = new() { SendBuffer = packetBuf };
        Timer timer = new Timer(SendPacket, info, Timeout.Infinite, Timeout.Infinite);

        try
        {
            for (; ; )
            {
                long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                if (sendQueue.TryDequeue(out (long streamId, byte[] buf) pair))
                {
                    fixed (byte* bufPtr = pair.buf)
                    {
                        quiche_conn_stream_send(
                            NativePtr, (ulong)pair.streamId,
                            bufPtr, (nuint)pair.buf.Length,
                            streamMap[pair.streamId].CanWrite,
                            (ulong*)Unsafe.AsPointer(ref errorCode)
                            );
                    }
                }

                QuicheException.ThrowIfError((QuicheError)errorCode, "An uncaught error occured in quiche!");

                long sendCount;
                SendInfo sendInfo = default;
                fixed (byte* pktPtr = info.SendBuffer)
                {
                    sendCount = (long)quiche_conn_send(NativePtr, pktPtr,
                        (nuint)packetBuf.Length, (SendInfo*)Unsafe.AsPointer(ref sendInfo));
                }

                SocketAddress sendAddr = new((AddressFamily)sendInfo.to.ss_family, sendInfo.to_len);
                Span<byte> sendAddrSpan = new Span<byte>((byte*)Unsafe.AsPointer(ref sendInfo.to), sendInfo.to_len);
                sendAddrSpan.CopyTo(sendAddr.Buffer.Span);

                info.SendSize = (int)sendCount;
                info.SendAddr = sendAddr;

                timer.Change(TimeSpan.FromTicks(sendInfo.at.tv_nsec.Value / 10), Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packetBuf);
        }
    }

    private void ReceiveDriver()
    {
        byte[] packetBuf = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            for (; ; )
            {
                EndPoint remoteEndPoint = new IPEndPoint(IPAddress.None, 0);
                int readCount = dgramSocket.ReceiveFrom(packetBuf, ref remoteEndPoint);

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
                fixed (byte* bufPtr = packetBuf)
                {
                    using (to)
                    using (from)
                    {
                        recvCount = (long)quiche_conn_recv(NativePtr, bufPtr,
                            (nuint)readCount, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                    }

                    bool flag;
                    streamId = quiche_conn_stream_readable_next(NativePtr);
                    if ((flag = streamId < 0) && (streamId = quiche_conn_stream_writable_next(NativePtr)) < 0)
                    {
                        SendInfo sendInfo = default;
                        long sendCount = (long)quiche_conn_send(NativePtr, bufPtr, (nuint)packetBuf.Length,
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
                        recvCount = quiche_conn_stream_recv(NativePtr, (ulong)streamId, bufPtr, (nuint)recvCount,
                            (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref errorCode));
                        QuicheException.ThrowIfError((QuicheError)errorCode, "An uncaught error occured in quiche!");

                        QuicheStream stream = streamMap.GetOrAdd(streamId, id => new(this, streamId));
                        stream.BufferDataReceiveAsync(packetBuf, streamFinished).Wait();
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packetBuf);
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

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                foreach (var (_, stream) in streamMap)
                {
                    stream.Dispose();
                }

                try
                {
                    Task.WhenAll(recvTask, sendTask).Wait();
                }
                catch (AggregateException ex) when (
                    ex.InnerExceptions.All(x => x is QuicheException q &&
                    q.ErrorCode == QuicheError.QUICHE_ERR_DONE))
                { }
                finally
                {
                    streamMap.Clear();
                    sendQueue.Clear();
                }
            }

            NativePtr->Free();
            NativePtr = null;

            disposedValue = true;
        }
    }

    ~QuicheConnection()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
