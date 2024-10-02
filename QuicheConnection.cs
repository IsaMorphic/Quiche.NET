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
                    ReadOnlyMemory<byte>.Empty, scidBuf);
            }
        }
    }

    private readonly Task recvTask, sendTask;
    private readonly CancellationTokenSource cts;

    private readonly TaskCompletionSource establishedTcs;
    private readonly ConcurrentDictionary<long, QuicheStream> streamMap;

    private readonly Socket socket;
    private readonly EndPoint remoteEndPoint;

    internal readonly ConcurrentQueue<(long, byte[])> sendQueue;

    private readonly byte[] connectionId;
    internal ReadOnlySpan<byte> ConnectionId => connectionId;

    internal unsafe Conn* NativePtr { get; private set; }

    public Task ConnectionEstablished => establishedTcs.Task;

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

        cts = new();
        recvTask = Task.Run(() => ReceiveAsync(initialData, cts.Token));
        sendTask = Task.Run(() => SendDriverAsync(cts.Token));
    }

    private class SendScheduleInfo
    {
        public int SendCount { get; set; }
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
                    int bytesSent = 0;
                    while (bytesSent < info.SendCount)
                    {
                        var packetSpan = info.SendBuffer.AsSpan(bytesSent, info.SendCount);
                        bytesSent += socket.SendTo(packetSpan, SocketFlags.None, info.SendAddr);
                    }
                }
            }
        }
    }

    private async Task SendDriverAsync(CancellationToken cancellationToken)
    {
        byte[] packetBuf = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];

        SendScheduleInfo info = new() { SendBuffer = packetBuf };
        Timer timer = new Timer(SendPacket, info, Timeout.Infinite, Timeout.Infinite);

        while(!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long resultOrError = packetBuf.Length;
                unsafe
                {
                    if (NativePtr->IsEstablished() &&
                        (establishedTcs.TrySetResult() || ConnectionEstablished.IsCompleted) &&
                        sendQueue.TryDequeue(out (long streamId, byte[] buf) pair))
                    {
                        fixed (byte* bufPtr = pair.buf)
                        {
                            long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                            QuicheStream stream = GetStream(pair.streamId);
                            resultOrError = NativePtr->StreamSend(
                                (ulong)pair.streamId, bufPtr, (nuint)pair.buf.Length,
                                stream.CanWrite, (ulong*)Unsafe.AsPointer(ref errorCode)
                                );
                            QuicheException.ThrowIfError((QuicheError)errorCode, "An uncaught error occured in quiche!");
                        }
                        pair.buf.AsSpan(0, (int)resultOrError).CopyTo(info.SendBuffer.AsSpan());
                    }
                }

                SocketAddress sendAddr;
                SendInfo sendInfo = default;
                unsafe
                {
                    NativePtr->OnTimeout();
                    fixed (byte* pktPtr = info.SendBuffer)
                    {
                        resultOrError = (long)NativePtr->Send(
                            pktPtr, (nuint)resultOrError, 
                            (SendInfo*)Unsafe.AsPointer(ref sendInfo)
                            );
                        QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                    }

                    sendAddr = new((AddressFamily)sendInfo.to.ss_family, sendInfo.to_len);
                    Span<byte> sendAddrSpan = new Span<byte>((byte*)Unsafe.AsPointer(ref sendInfo.to), sendInfo.to_len);
                    sendAddrSpan.CopyTo(sendAddr.Buffer.Span);
                }

                lock (info)
                {
                    info.SendAddr = sendAddr;
                    info.SendCount = (int)resultOrError;
                }

                timer.Change(
                    TimeSpan.FromSeconds(Unsafe.As<timespec, CLong>
                        (ref sendInfo.at).Value) +
                    TimeSpan.FromTicks(sendInfo.at.tv_nsec.Value / 100),
                    Timeout.InfiniteTimeSpan
                    );
            }
            catch (QuicheException ex) when
                (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            { await Task.Delay(75, cancellationToken); continue; }
            catch (QuicheException ex)
            {
                establishedTcs.TrySetException(ex);
                throw;
            }
        }
    }

    private async Task ReceiveAsync(ReadOnlyMemory<byte> initialData, CancellationToken cancellationToken)
    {
        byte[] packetBuf = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];
        initialData.CopyTo(packetBuf);

        SocketReceiveFromResult recieveResult = new() 
        { 
            ReceivedBytes = initialData.Length, 
            RemoteEndPoint = remoteEndPoint 
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long resultOrError;
                bool isConnEstablished;
                unsafe
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
                        using (to)
                        using (from)
                        {
                            resultOrError = (long)quiche_conn_recv(NativePtr, bufPtr,
                               (nuint)recieveResult.ReceivedBytes, (RecvInfo*)Unsafe.AsPointer(ref recvInfo));
                            QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                        }
                    }

                    isConnEstablished = NativePtr->IsEstablished();
                }

                if (isConnEstablished && (establishedTcs.TrySetResult() || ConnectionEstablished.IsCompleted))
                {
                    long streamIdOrNone;
                    unsafe { streamIdOrNone = NativePtr->StreamReadableNext(); }

                    if (streamIdOrNone >= 0)
                    {
                        long recvCount;
                        bool streamFinished = false;
                        unsafe
                        {
                            fixed (byte* bufPtr = packetBuf)
                            {
                                recvCount = (long)NativePtr->StreamRecv((ulong)streamIdOrNone, bufPtr, (nuint)resultOrError,
                                    (bool*)Unsafe.AsPointer(ref streamFinished), (ulong*)Unsafe.AsPointer(ref resultOrError));
                                QuicheException.ThrowIfError((QuicheError)resultOrError, "An uncaught error occured in quiche!");
                            }
                        }

                        QuicheStream stream = GetStream(streamIdOrNone);
                        await stream.ReceiveDataAsync(
                            packetBuf.AsMemory(0, (int)recvCount), 
                            streamFinished, cancellationToken
                            );
                    }
                }

                do
                {
                    recieveResult = await socket.ReceiveFromAsync(packetBuf, remoteEndPoint, cancellationToken);
                } while (recieveResult.RemoteEndPoint != remoteEndPoint);
            }
            catch (QuicheException ex) when
                (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            { await Task.Delay(75, cancellationToken); continue; }
            catch (QuicheException ex)
            {
                establishedTcs.TrySetException(ex);
                throw;
            }
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

                try
                {
                    int errorResult;
                    byte[] reasonBuf = Encoding.UTF8.GetBytes("Connection was implicitly closed for user initiated disposal.");
                    fixed (byte* reasonPtr = reasonBuf)
                    {
                        errorResult = quiche_conn_close(NativePtr, true, 0xFFFF, reasonPtr, (nuint)reasonBuf.Length);
                        QuicheException.ThrowIfError((QuicheError)errorResult, "Failed to close connection!");
                    }
                }
                catch (QuicheException ex)
                when (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE) { }
                finally 
                {
                    cts.Dispose();
                }

                try
                {
                    Task.WhenAll(recvTask, sendTask).Wait();
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(
                    x => x is OperationCanceledException || x is QuicheException q && 
                    q.ErrorCode == QuicheError.QUICHE_ERR_DONE))
                { }
                finally
                {
                    streamMap.Clear();
                    sendQueue.Clear();

                    socket.Dispose();
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
