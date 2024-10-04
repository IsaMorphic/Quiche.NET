using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Quiche.NET;

public class QuicheListener : IDisposable
{
    private readonly ConcurrentDictionary<string?, QuicheConnection> connMap;
    private readonly ConcurrentBag<TaskCompletionSource<QuicheConnection>> connBag;

    private readonly Socket socket;
    private readonly QuicheConfig config;

    public QuicheListener(Socket socket, QuicheConfig config)
    {
        this.socket = socket;
        this.config = config;

        connMap = new();
        connBag = new();
    }

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] recvBuffer = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SocketReceiveFromResult recvResult = await socket.ReceiveFromAsync(recvBuffer, new IPEndPoint(IPAddress.None, 0), cancellationToken);
                ReadOnlyMemory<byte> receivedBytes = ((byte[])recvBuffer.Clone())
                        .AsMemory(0, recvResult.ReceivedBytes);

                if (connMap.TryGetValue(recvResult.RemoteEndPoint.ToString(), out QuicheConnection? connection))
                {
                    connection.recvQueue.Enqueue(receivedBytes);
                }
                else
                {
                    QuicheConnection conn = QuicheConnection.Accept(socket, recvResult.RemoteEndPoint, receivedBytes, config);
                    connMap.TryAdd(recvResult.RemoteEndPoint.ToString(), conn);

                    if (!connBag.TryTake(out TaskCompletionSource<QuicheConnection>? tcs))
                    {
                        connBag.Add(tcs = new());
                    }
                    tcs.TrySetResult(conn);
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (connBag.TryTake(out TaskCompletionSource<QuicheConnection>? tcs))
            {
                tcs.TrySetCanceled(cancellationToken);
            }
        }
    }

    public async Task<QuicheConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        if (!connBag.TryPeek(out TaskCompletionSource<QuicheConnection>? tcs))
        {
            connBag.Add(tcs = new());
        }
        return await tcs.Task.WaitAsync(cancellationToken);
    }


    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                foreach (QuicheConnection conn in connMap.Values)
                {
                    conn.Dispose();
                }

                connMap.Clear();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
