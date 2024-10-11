
using System.Buffers;
using System.IO.Pipelines;

namespace Quiche.NET
{
    public class QuicheStream : Stream
    {
        public enum Shutdown
        {
            Read = 0,
            Write = 1,
        }

        public enum Direction
        {
            Bidirectional = 0x0,
            Unidirectional = 0x2
        }

        private const int MAX_READ_RETRIES = 50;

        private readonly Pipe? recvPipe, sendPipe;

        private readonly QuicheConnection conn;
        private readonly ulong streamId;

        private bool firstReadFlag, firstWriteFlag;

        public override bool CanRead => !conn.IsClosed && !(firstReadFlag && conn.IsStreamFinished(streamId));

        public override bool CanWrite => !conn.IsClosed && !(firstWriteFlag && conn.IsStreamFinished(streamId));

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Length => throw new NotSupportedException();

        internal QuicheStream(QuicheConnection conn, ulong streamId)
        {
            this.conn = conn;
            this.streamId = streamId;

            bool isPeerInitiated = ((streamId & 1) == 0) ^ conn.IsServer;
            bool isBidirectional = (streamId & 2) == 0;

            if (isPeerInitiated || isBidirectional)
            {
                recvPipe = new Pipe();
            }

            if (!isPeerInitiated || isBidirectional)
            {
                sendPipe = new Pipe();
            }
        }

        internal async Task ReceiveDataAsync(ReadOnlyMemory<byte> bufIn, bool finished, CancellationToken cancellationToken)
        {
            if (recvPipe is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                Memory<byte> memory = recvPipe.Writer.GetMemory(bufIn.Length);
                bufIn.CopyTo(memory); recvPipe.Writer.Advance(bufIn.Length);

                await recvPipe.Writer.FlushAsync(cancellationToken);

                if (finished)
                {
                    await recvPipe.Writer.CompleteAsync();
                }
            }
        }

        public override void Flush()
        {
            if (sendPipe?.Reader.TryRead(out ReadResult result) ?? false)
            {
                conn.sendQueue.AddOrUpdate(streamId,
                    key => result.Buffer.ToArray(),
                    (key, buf) => [.. buf, .. result.Buffer.ToArray()]
                    );
                sendPipe.Reader.AdvanceTo(result.Buffer.End);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (recvPipe is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                int bytesTotal = 0, numRetries = MAX_READ_RETRIES;
                while (CanRead && bytesTotal < count && numRetries > 0)
                {
                    if (recvPipe.Reader.TryRead(out ReadResult result))
                    {
                        int bytesRead = (int)Math.Min(result.Buffer.Length, count - bytesTotal);

                        result.Buffer.Slice(result.Buffer.Start, bytesRead).CopyTo(buffer.AsSpan(offset + bytesTotal, bytesRead));
                        recvPipe.Reader.AdvanceTo(result.Buffer.GetPosition(bytesRead));

                        bytesTotal += bytesRead;
                    }
                    else { --numRetries; Thread.Sleep(100); }
                }

                return bytesTotal;
            }
        }

        internal void SetFirstRead() => firstReadFlag = true;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (sendPipe is null)
            {
                throw new NotSupportedException();
            }
            else if (CanWrite)
            {
                Memory<byte> memory = sendPipe.Writer.GetMemory(count);
                buffer.AsMemory(offset, count).CopyTo(memory);
                sendPipe.Writer.Advance(count);

                sendPipe.Writer.FlushAsync();
            }
            else 
            {
                throw new EndOfStreamException("Cannot write to a closed stream.");
            }
        }

        internal void SetFirstWrite() => firstWriteFlag = true;

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public unsafe override void Close()
        {
            base.Close();

            try
            {
                if (recvPipe is not null && sendPipe is not null)
                {
                    recvPipe.Writer.Complete();

                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown(streamId,
                        (int)Shutdown.Read, 0x00),
                        $"Failed to shutdown reading side of stream! (ID: {streamId:X16})"
                        );

                    sendPipe.Writer.Complete();

                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown(streamId,
                        (int)Shutdown.Write, 0x00),
                        $"Failed to shutdown writing side of stream! (ID: {streamId:X16})"
                        );
                }
                else if (recvPipe is not null)
                {
                    recvPipe.Writer.Complete();

                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown(streamId,
                        (int)Shutdown.Read, 0x00),
                        $"Failed to shutdown reading side of stream! (ID: {streamId:X16})"
                        );
                }
                else if (sendPipe is not null)
                {
                    sendPipe.Writer.Complete();

                    QuicheException.ThrowIfError((QuicheError)
                        conn.NativePtr->StreamShutdown(streamId,
                        (int)Shutdown.Write, 0x00),
                        $"Failed to shutdown writing side of stream! (ID: {streamId:X16})"
                        );
                }
            }
            catch (QuicheException ex)
            when (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            { }
        }
    }
}
