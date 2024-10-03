using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Quiche.NET;

public class QuicheListener : IDisposable
{
    private readonly ConcurrentDictionary<EndPoint, QuicheConnection> connMap;
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
        byte[] recvBuffer = new byte[QuicheLibrary.MAX_DATAGRAM_LEN];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult recvResult = await socket.ReceiveFromAsync(recvBuffer, socket.LocalEndPoint);
            ReadOnlyMemory<byte> receivedBytes = ((byte[])recvBuffer.Clone())
                    .AsMemory(0, recvResult.ReceivedBytes);

            if (connMap.TryGetValue(recvResult.RemoteEndPoint, out QuicheConnection? connection))
            {
                connection.recvQueue.Enqueue(receivedBytes);
            }
            else if (connBag.TryTake(out TaskCompletionSource<QuicheConnection>? tcs))
            {
                QuicheConnection conn = QuicheConnection.Accept(socket, recvResult.RemoteEndPoint, receivedBytes, config);
                connMap.TryAdd(recvResult.RemoteEndPoint, conn);
                tcs.TrySetResult(conn);
            }
        }
    }

    public async Task<QuicheConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<QuicheConnection> tcs = new(); connBag.Add(tcs);
        return await tcs.Task.WaitAsync(cancellationToken);;
    }


    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                foreach(QuicheConnection conn in connMap.Values)
                {
                    conn.Dispose();
                }

                connMap.Clear();
                connBag.Clear();
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
