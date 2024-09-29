using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Quiche.NativeMethods;

namespace Quiche.NET;

public unsafe class QuicheConnection : IDisposable
{
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
            byte[] scid = RandomNumberGenerator.GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);
            fixed (byte* scidPtr = scid)
            {
                return new(quiche_accept(
                    scidPtr, (nuint)scid.Length, null, 0,
                    (sockaddr*)local.Pointer, local_len,
                    (sockaddr*)remote.Pointer, remote_len,
                    config.NativePtr), localSocket, scid);
            }
        }
    }

    public static QuicheConnection Connect(string remoteHostname, EndPoint remoteEndPoint, QuicheConfig config) 
    {
        Socket localSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);

        var (local, local_len) = GetSocketAddress(localSocket.LocalEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteEndPoint);

        using (local)
        using (remote)
        {
            byte[] hostnameBuf = Encoding.Default.GetBytes([..remoteHostname.ToCharArray(), '\u0000']);
            byte[] scid = RandomNumberGenerator.GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);

            fixed (byte* hostnamePtr = hostnameBuf)
            fixed (byte* scidPtr = scid)
            {
                return new(quiche_connect(hostnamePtr,
                    scidPtr, (nuint)scid.Length,
                    (sockaddr*)local.Pointer, local_len,
                    (sockaddr*)remote.Pointer, remote_len,
                    config.NativePtr), localSocket, scid);
            }
        }
    }

    private readonly Task recvTask, sendTask;
    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;
    private readonly Socket dgramSocket;

    internal readonly ConcurrentQueue<(long, byte[])> sendQueue;

    private readonly byte[] connectionId;
    internal ReadOnlySpan<byte> ConnectionId => connectionId;

    internal Conn* NativePtr { get; private set; }

    private QuicheConnection(Conn* nativePtr, Socket dgramSocket, ReadOnlySpan<byte> connectionId)
    {
        NativePtr = nativePtr;
        this.dgramSocket = dgramSocket;

        this.connectionId = new byte[QuicheLibrary.MAX_CONN_ID_LEN];
        connectionId.CopyTo(this.connectionId);

        sendQueue = new();
        streamMap = new();

        recvTask = Task.Run(ReceiveDriver);
        sendTask = Task.Run(SendDriver);
    }

    private class SendScheduleInfo
    {
        public long SendSize { get; set; }
        public byte[]? SendBuffer { get; set; }
        public SocketAddress? SendAddr { get; set; }
    }

    private void SendPacket(object? state)
    {
        SendScheduleInfo? info = state as SendScheduleInfo;
        if (info?.SendSize is null || info?.SendBuffer is null || info?.SendAddr is null) return;

        lock (info)
        {
            fixed (byte* pktPtr = info.SendBuffer)
            {
                ReadOnlySpan<byte> sendBuf = new(pktPtr, (int)info.SendSize);
                dgramSocket.SendTo(sendBuf, SocketFlags.None, info.SendAddr);
            }
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

                lock (info)
                {
                    info.SendSize = sendCount;
                    info.SendAddr = sendAddr;
                }

                timer.Change(
                    TimeSpan.FromSeconds(Unsafe.As<timespec, CLong>
                        (ref sendInfo.at).Value) +
                    TimeSpan.FromTicks(sendInfo.at.tv_nsec.Value / 10),
                    Timeout.InfiniteTimeSpan
                    );
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

                        QuicheStream stream = streamMap.GetOrAdd(streamId, id => new(this, id));
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

    public QuicheStream GetStream(long streamId) =>
        streamMap.GetOrAdd(streamId, id => new(this, id));

    public void Close(int errorCode, string reason)
    {
        int errorResult;
        byte[] reasonBuf = Encoding.Default.GetBytes(reason);
        fixed (byte* reasonPtr = reasonBuf)
        {
            errorResult = quiche_conn_close(NativePtr, true, (ulong)errorCode, reasonPtr, (nuint)reasonBuf.Length);
        }
        QuicheException.ThrowIfError((QuicheError)errorResult, "Failed to close connection!");
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

                int errorResult;
                byte[] reasonBuf = Encoding.Default.GetBytes("Connection was implicitly closed for user initiated disposal.");
                fixed (byte* reasonPtr = reasonBuf)
                {
                    errorResult = quiche_conn_close(NativePtr, false, 0x0F, reasonPtr, (nuint)reasonBuf.Length);
                }
                QuicheException.ThrowIfError((QuicheError)errorResult, "Failed to close connection!");

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
