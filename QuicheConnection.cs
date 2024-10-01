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

public class QuicheConnection : IDisposable
{
    private static (MemoryHandle, int) GetSocketAddress(EndPoint? endPoint)
    {
        Memory<byte>? buf = endPoint?.Serialize().Buffer;
        return buf is null ? default : (buf.Value.Pin(), buf.Value.Length);
    }

    public unsafe static QuicheConnection Accept(Socket socket,
        EndPoint remoteEndPoint, ReadOnlyMemory<byte> initialData,
        QuicheConfig config, byte[]? cid = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

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
                    config.NativePtr),
                    socket, remoteEndPoint,
                    initialData, scidBuf);
            }
        }
    }

    public unsafe static QuicheConnection Connect(Socket socket, EndPoint remoteEndPoint,
        QuicheConfig config, string? hostname = null, byte[]? cid = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

        var (local, local_len) = GetSocketAddress(localEndPoint);
        var (remote, remote_len) = GetSocketAddress(remoteEndPoint);

        using (local)
        using (remote)
        {
            byte[] hostnameBuf = Encoding.UTF8.GetBytes([.. hostname?.ToCharArray(), '\u0000']);
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);

            fixed (byte* hostnamePtr = hostnameBuf)
            fixed (byte* scidPtr = scidBuf)
            {
                return new(quiche_connect(hostnamePtr,
                    scidPtr, (nuint)scidBuf.Length,
                    (sockaddr*)local.Pointer, local_len,
                    (sockaddr*)remote.Pointer, remote_len,
                    config.NativePtr),
                    socket, remoteEndPoint,
                    Memory<byte>.Empty, scidBuf);
            }
        }
    }

    private readonly Task recvTask, sendTask;
    private readonly TaskCompletionSource establishedTcs;
    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;

    private readonly Socket socket;
    private readonly EndPoint remoteEndPoint;

    internal readonly ConcurrentQueue<(long, byte[])> sendQueue;

    private readonly byte[] connectionId;
    internal ReadOnlySpan<byte> ConnectionId => connectionId;

    internal unsafe Conn* NativePtr { get; private set; }

    public Task EstablishedTask => establishedTcs.Task;

    private unsafe QuicheConnection(Conn* nativePtr, Socket socket, EndPoint remoteEndPoint, ReadOnlyMemory<byte> initialData, ReadOnlyMemory<byte> connectionId)
    {
        NativePtr = nativePtr;

        this.socket = socket;
        this.remoteEndPoint = remoteEndPoint;

        this.connectionId = new byte[QuicheLibrary.MAX_CONN_ID_LEN];
        connectionId.CopyTo(this.connectionId);

        sendQueue = new();
        streamMap = new();

        establishedTcs = new();

        recvTask = Task.Run(() => ReceiveDriver(initialData));
        sendTask = Task.Run(SendDriver);
    }

    private class SendScheduleInfo
    {
        public int SendCount { get; set; }
        public byte[]? SendBuffer { get; set; }
        public SocketAddress? SendAddr { get; set; }
    }

    private unsafe void SendPacket(object? state)
    {
        SendScheduleInfo? info = state as SendScheduleInfo;
        if (info is not null)
        {
            lock (info)
            {
                if (info.SendBuffer is not null && info.SendAddr is not null)
                {
                    socket.SendTo(info.SendBuffer.AsSpan(0, info.SendCount), SocketFlags.None, info.SendAddr);
                }
            }
        }
    }

    private unsafe void SendDriver()
    {
        byte[] packetBuf = new byte[4096];

        SendScheduleInfo info = new() { SendBuffer = packetBuf };
        Timer timer = new Timer(SendPacket, info, Timeout.Infinite, Timeout.Infinite);

        try
        {
            for (; ; )
            {
                long resultOrError;
                if (quiche_conn_is_established(NativePtr) &&
                    (establishedTcs.TrySetResult() || EstablishedTask.IsCompleted) &&
                    sendQueue.TryDequeue(out (long streamId, byte[] buf) pair))
                {
                    fixed (byte* bufPtr = pair.buf)
                    {
                        long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                        QuicheStream stream = GetStream(pair.streamId);
                        resultOrError = quiche_conn_stream_send(
                            NativePtr, (ulong)pair.streamId,
                            bufPtr, (nuint)pair.buf.Length,
                            stream.CanWrite, (ulong*)
                            Unsafe.AsPointer(ref errorCode)
                            );
                        QuicheException.ThrowIfError((QuicheError)errorCode, "An uncaught error occured in quiche!");
                    }
                }

                SendInfo sendInfo = default;
                fixed (byte* pktPtr = info.SendBuffer)
                {
                    resultOrError = (long)quiche_conn_send(NativePtr, pktPtr,
                        (nuint)packetBuf.Length, (SendInfo*)Unsafe.AsPointer(ref sendInfo));
                    QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                }

                SocketAddress sendAddr = new((AddressFamily)sendInfo.to.ss_family, sendInfo.to_len);

                Span<byte> sendAddrSpan = new Span<byte>((byte*)Unsafe.AsPointer(ref sendInfo.to), sendInfo.to_len);
                sendAddrSpan.CopyTo(sendAddr.Buffer.Span);

                lock (info)
                {
                    info.SendAddr = sendAddr;
                    info.SendCount = (int)resultOrError;
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
            establishedTcs.TrySetException(ex);
            throw;
        }
    }

    private unsafe void ReceiveDriver(ReadOnlyMemory<byte> initialData)
    {
        byte[] packetBuf = new byte[4096];
        initialData.CopyTo(packetBuf);

        int readCount = initialData.Length;
        EndPoint remoteEndPoint = this.remoteEndPoint;
        try
        {
            for (; ; )
            {
                var (to, to_len) = GetSocketAddress(socket.LocalEndPoint);
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
                    long resultOrError;
                    using (to)
                    using (from)
                    {
                        resultOrError = (long)quiche_conn_recv(NativePtr, bufPtr,
                           (nuint)readCount, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                        QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                    }

                    long streamId = quiche_conn_stream_readable_next(NativePtr);
                    if (streamId >= 0)
                    {
                        bool streamFinished = false;

                        long recvCount = (long)quiche_conn_stream_recv(NativePtr, (ulong)streamId, bufPtr, (nuint)resultOrError,
                            (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref resultOrError));

                        QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");

                        QuicheStream stream = GetStream(streamId);
                        stream.BufferDataReceiveAsync(packetBuf[..(int)--recvCount], streamFinished).Wait();
                    }
                }

                readCount = socket.ReceiveFrom(packetBuf, ref remoteEndPoint);
            }
        }
        catch (QuicheException ex)
        {
            establishedTcs.TrySetException(ex);
            throw;
        }
    }

    public QuicheStream GetStream(long streamId) =>
        streamMap.GetOrAdd(streamId, id => new(this, id));

    private bool disposedValue;

    protected unsafe virtual void Dispose(bool disposing)
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
                byte[] reasonBuf = Encoding.UTF8.GetBytes("Connection was implicitly closed for user initiated disposal.");
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
