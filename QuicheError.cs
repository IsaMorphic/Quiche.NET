namespace Quiche.NET
{
    public enum QuicheError
    {
        // All OK!
        QUICHE_ERR_NONE = 0,

        // There is no more work to do.
        QUICHE_ERR_DONE = -1,

        // The provided buffer is too short.
        QUICHE_ERR_BUFFER_TOO_SHORT = -2,

        // The provided packet cannot be parsed because its version is unknown.
        QUICHE_ERR_UNKNOWN_VERSION = -3,

        // The provided packet cannot be parsed because it contains an invalid
        // frame.
        QUICHE_ERR_INVALID_FRAME = -4,

        // The provided packet cannot be parsed.
        QUICHE_ERR_INVALID_PACKET = -5,

        // The operation cannot be completed because the connection is in an
        // invalid state.
        QUICHE_ERR_INVALID_STATE = -6,

        // The operation cannot be completed because the stream is in an
        // invalid state.
        QUICHE_ERR_INVALID_STREAM_STATE = -7,

        // The peer's transport params cannot be parsed.
        QUICHE_ERR_INVALID_TRANSPORT_PARAM = -8,

        // A cryptographic operation failed.
        QUICHE_ERR_CRYPTO_FAIL = -9,

        // The TLS handshake failed.
        QUICHE_ERR_TLS_FAIL = -10,

        // The peer violated the local flow control limits.
        QUICHE_ERR_FLOW_CONTROL = -11,

        // The peer violated the local stream limits.
        QUICHE_ERR_STREAM_LIMIT = -12,

        // The specified stream was stopped by the peer.
        QUICHE_ERR_STREAM_STOPPED = -15,

        // The specified stream was reset by the peer.
        QUICHE_ERR_STREAM_RESET = -16,

        // The received data exceeds the stream's final size.
        QUICHE_ERR_FINAL_SIZE = -13,

        // Error in congestion control.
        QUICHE_ERR_CONGESTION_CONTROL = -14,

        // Too many identifiers were provided.
        QUICHE_ERR_ID_LIMIT = -17,

        // Not enough available identifiers.
        QUICHE_ERR_OUT_OF_IDENTIFIERS = -18,

        // Error in key update.
        QUICHE_ERR_KEY_UPDATE = -19,

        // The peer sent more data in CRYPTO frames than we can buffer.
        QUICHE_ERR_CRYPTO_BUFFER_EXCEEDED = -20,
    }
}