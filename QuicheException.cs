namespace Quiche.NET
{
    public class QuicheException : Exception
    {
        private const string DEFAULT_MESSAGE = "An unexpected error was raised by Quiche!";

        public QuicheError ErrorCode { get; }

        private QuicheException(QuicheError errorCode, string? message) 
            : base($"{message ?? DEFAULT_MESSAGE}\n{errorCode}")
        {
            ErrorCode = errorCode;
        }

        public static void ThrowIfError(QuicheError errorCode, string? message = null)
        {
            if (errorCode == QuicheError.QUICHE_ERR_NONE) 
            {
                return;
            }

            throw new QuicheException(errorCode, message);
        }
    }
}
