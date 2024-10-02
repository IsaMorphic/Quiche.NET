
using System.IO.Pipelines;
using static Quiche.NativeMethods;

namespace Quiche.NET
{
    public class QuicheStream : Stream
    {
        public enum Shutdown
        {
            Read = 0,
            Write = 1,
        }

        private readonly Pipe? recvPipe, sendPipe;
        private readonly Stream? recvStream, sendStream;

        private readonly long streamId;
        private readonly QuicheConnection conn;

        public unsafe override bool CanRead =>
            quiche_conn_stream_readable(conn.NativePtr, (ulong)streamId);

        public unsafe override bool CanWrite =>
            quiche_conn_stream_writable(conn.NativePtr, (ulong)streamId, QuicheLibrary.MAX_DATAGRAM_LEN) !=
            (int)QuicheError.QUICHE_ERR_STREAM_STOPPED;

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Length => throw new NotSupportedException();

        internal QuicheStream(QuicheConnection conn, long streamId)
        {
            this.conn = conn;
            this.streamId = streamId;

            if (CanRead)
            {
                recvPipe = new Pipe();
                recvStream = recvPipe.Reader.AsStream(true);
            }

            if (CanWrite)
            {
                sendPipe = new Pipe();
                sendStream = sendPipe.Writer.AsStream(true);
            }
        }

        internal ValueTask<FlushResult> ReceiveDataAsync(ReadOnlyMemory<byte> bufIn, bool finished, CancellationToken cancellationToken)
        {
            if (recvPipe is null)
            {
                throw new NotSupportedException();
            }

            Memory<byte> memory = recvPipe.Writer.GetMemory((int)QuicheLibrary.MAX_DATAGRAM_LEN);

            bufIn.CopyTo(memory);
            recvPipe.Writer.Advance(bufIn.Length);

            // Make the data available to the PipeReader.
            var flushTask = recvPipe.Writer.FlushAsync(cancellationToken);
            if (finished)
            {
                recvPipe.Writer.Complete();
            }

            return flushTask;
        }

        public override void Flush()
        {
            while (sendPipe?.Reader.TryRead(out ReadResult result) ?? false)
            {
                foreach (var memory in result.Buffer)
                {
                    conn.sendQueue.Enqueue((streamId, memory.ToArray()));
                }
                sendPipe.Reader.AdvanceTo(result.Buffer.End);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            recvStream?.Read(buffer, offset, count) ?? throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (sendStream is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                sendStream.Write(buffer, offset, count);
                Flush();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public unsafe override void Close()
        {
            base.Close();

            recvPipe?.Writer.Complete();
            sendPipe?.Writer.Complete();

            if (CanRead)
            {
                QuicheException.ThrowIfError((QuicheError)
                    quiche_conn_stream_shutdown(conn.NativePtr, (ulong)streamId,
                    (int)Shutdown.Read, unchecked((ulong)QuicheError.QUICHE_ERR_DONE))
                    );
            }

            if (CanWrite)
            {
                QuicheException.ThrowIfError((QuicheError)
                    quiche_conn_stream_shutdown(conn.NativePtr, (ulong)streamId,
                    (int)Shutdown.Write, unchecked((ulong)QuicheError.QUICHE_ERR_DONE))
                    );
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                recvStream?.Dispose();
                sendStream?.Dispose();
            }
        }
    }
}
