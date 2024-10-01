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

    public static QuicheConnection Accept(Socket socket, QuicheConfig config, byte[]? cid = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.None, 0);
        socket.ReceiveFrom([], ref remoteEndPoint);

        var (local, local_len) = GetSocketAddress(localEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteEndPoint);

        using (local)
        using (remote)
        {
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);
            fixed (byte* scidPtr = scidBuf)
            {
                return new(quiche_accept(
                    scidPtr, (nuint)scidBuf.Length, null, 0,
                    (sockaddr*)local.Pointer, local_len,
                    (sockaddr*)remote.Pointer, remote_len,
                    config.NativePtr), socket, scidBuf);
            }
        }
    }

    public static QuicheConnection Connect(Socket socket, EndPoint remoteEndPoint,
        QuicheConfig config, string? hostname = null, byte[]? cid = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

        var (local, local_len) = GetSocketAddress(localEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteEndPoint);

        using (local)
        using (remote)
        {
            byte[] hostnameBuf = Encoding.Default.GetBytes([.. hostname?.ToCharArray(), '\u0000']);
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);

            fixed (byte* hostnamePtr = hostnameBuf)
            fixed (byte* scidPtr = scidBuf)
            {
                return new(quiche_connect(hostnamePtr,
                    scidPtr, (nuint)scidBuf.Length,
                    (sockaddr*)local.Pointer, local_len,
                    (sockaddr*)remote.Pointer, remote_len,
                    config.NativePtr), socket, scidBuf);
            }
        }
    }

    private readonly Task recvTask, sendTask;
    private readonly TaskCompletionSource connEstablishedTcs;
    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;
    private readonly Socket dgramSocket;

    internal readonly ConcurrentQueue<(long, byte[])> sendQueue;

    private readonly byte[] connectionId;
    internal ReadOnlySpan<byte> ConnectionId => connectionId;

    internal Conn* NativePtr { get; private set; }

    public Task ConnectionEstablished => connEstablishedTcs.Task;

    private QuicheConnection(Conn* nativePtr, Socket dgramSocket, ReadOnlySpan<byte> connectionId)
    {
        NativePtr = nativePtr;
        this.dgramSocket = dgramSocket;

        this.connectionId = new byte[QuicheLibrary.MAX_CONN_ID_LEN];
        connectionId.CopyTo(this.connectionId);

        sendQueue = new();
        streamMap = new();

        connEstablishedTcs = new();

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
        if (info is not null)
        {
            lock (info)
            {
                if (info.SendBuffer is not null && info.SendAddr is not null)
                {
                    fixed (byte* pktPtr = info.SendBuffer)
                    {
                        ReadOnlySpan<byte> sendBuf = new(pktPtr, (int)info.SendSize);
                        dgramSocket.SendTo(sendBuf, SocketFlags.None, info.SendAddr);
                    }
                }
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
                if (quiche_conn_is_established(NativePtr) &&
                    sendQueue.TryDequeue(out (long streamId, byte[] buf) pair))
                {
                    connEstablishedTcs.TrySetResult();
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
        catch (QuicheException ex)
        {
            connEstablishedTcs.TrySetException(ex);
            throw;
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

                fixed (byte* bufPtr = packetBuf)
                {
                    long recvCount;
                    using (to)
                    using (from)
                    {
                        recvCount = (long)quiche_conn_recv(NativePtr, bufPtr,
                            (nuint)readCount, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                    }

                    long streamId = quiche_conn_stream_readable_next(NativePtr);
                    if (streamId >= 0)
                    {
                        bool streamFinished = false;
                        long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                        recvCount = quiche_conn_stream_recv(NativePtr, (ulong)streamId, bufPtr, (nuint)recvCount,
                            (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref errorCode));
                        QuicheException.ThrowIfError((QuicheError)errorCode, "An uncaught error occured in quiche!");

                        QuicheStream stream = GetStream(streamId);
                        stream.BufferDataReceiveAsync(packetBuf, streamFinished).Wait();
                    }
                }
            }
        }
        catch(QuicheException ex)
        {
            connEstablishedTcs.TrySetException(ex);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packetBuf);
        }
    }

    public QuicheStream GetStream(long streamId) =>
        streamMap.GetOrAdd(streamId, id => new(this, id));

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
                    errorResult = quiche_conn_close(NativePtr, true, 0x0A, reasonPtr, (nuint)reasonBuf.Length);
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
