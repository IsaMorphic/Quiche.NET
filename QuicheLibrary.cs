using System.Text;

namespace Quiche.NET
{
    public static class QuicheLibrary
    {
        public const uint PROTOCOL_VERSION = 0x00000001;

        public const uint MAX_CONN_ID_LEN = 20;

        public const uint MIN_CLIENT_INITIAL_LEN = 1200;

        public unsafe static string VersionCode 
        { 
            get 
            {
                sbyte* versionCodePtr = (sbyte*)NativeMethods.quiche_version();
                return new string(versionCodePtr);
            } 
        }
    }
}
