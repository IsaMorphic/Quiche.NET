using System.Runtime.CompilerServices;
using System.Text;
using static Quiche.NativeMethods;
using static Quiche.NET.QuicheLibrary;

namespace Quiche.NET
{
    public unsafe class QuicheConfig : IDisposable
    {
        // quiche_config handle

        internal Config* NativePtr { get; private set; }

        // quiche_config properties

        public long AcknowledgementDelayExponent
        {
            set
            {
                NativePtr->SetAckDelayExponent((ulong)value);
            }
        }

        public long ActiveConnectionIdLimit 
        {
            set 
            {
                NativePtr->SetActiveConnectionIdLimit((ulong)value);
            }
        }

        public QuicheCcAlgorithm CcAlgorithm 
        {
            set 
            {
                NativePtr->SetCcAlgorithm((int)value);
            }
        }

        public int InitialCongestionWindowPackets
        {
            set 
            {
                NativePtr->SetInitialCongestionWindowPackets((nuint)value);
            }
        }

        public bool IsActiveMigrationDisabled
        {
            set
            {
                NativePtr->SetDisableActiveMigration(value);
            }
        }

        public bool IsHyStartEnabled
        {
            set
            {
                NativePtr->EnableHystart(value);
            }
        }

        public bool IsPacingEnabled
        {
            set
            {
                NativePtr->EnablePacing(value);
            }
        }

        public long MaxAcknowledgementDelay
        {
            set
            {
                NativePtr->SetMaxAckDelay((ulong)value);
            }
        }

        public int MaxAmplificationFactor
        {
            set
            {
                NativePtr->SetMaxAmplificationFactor((nuint)value);
            }
        }

        public long MaxIdleTimeout
        {
            set
            {
                NativePtr->SetMaxIdleTimeout((ulong)value);
            }
        }

        public long MaxInitialBidiStreams 
        {
            set 
            {
                NativePtr->SetInitialMaxStreamsBidi((ulong)value);
            }
        }

        public long MaxInitialDataSize
        {
            set
            {
                NativePtr->SetInitialMaxData((ulong)value);
            }
        }

        public long MaxInitialLocalBidiStreamDataSize
        {
            set
            {
                NativePtr->SetInitialMaxStreamDataBidiLocal((ulong)value);
            }
        }

        public long MaxInitialRemoteBidiStreamDataSize
        {
            set
            {
                NativePtr->SetInitialMaxStreamDataBidiRemote((ulong)value);
            }
        }

        public long MaxInitialUniStreamDataSize
        {
            set
            {
                NativePtr->SetInitialMaxStreamDataUni((ulong)value);
            }
        }

        public long MaxInitialUniStreams
        {
            set
            {
                NativePtr->SetInitialMaxStreamsBidi((ulong)value);
            }
        }

        public long MaxPacingRate
        {
            set 
            {
                NativePtr->SetMaxPacingRate((ulong)value);
            }
        }

        public int MaxReceiveUdpPayloadSize
        {
            set
            {
                NativePtr->SetMaxRecvUdpPayloadSize((nuint)value);
            }
        }

        public int MaxSendUdpPayloadSize
        {
            set
            {
                NativePtr->SetMaxSendUdpPayloadSize((nuint)value);
            }
        }

        public bool ShouldDiscoverPathMtu
        {
            set
            {
                NativePtr->DiscoverPmtu(value);
            }
        }

        public bool ShouldSendGrease
        {
            set
            {
                NativePtr->Grease(value);
            }
        }

        public bool ShouldVerifyPeer
        {
            set
            {
                NativePtr->VerifyPeer(value);
            }
        }

        public QuicheConfig(
            bool isEarlyDataEnabled = false,
            bool shouldLogKeys = false
            )
        {
            NativePtr = quiche_config_new(PROTOCOL_VERSION);

            if (isEarlyDataEnabled)
            {
                NativePtr->EnableEarlyData();
            }

            if (shouldLogKeys)
            {
                NativePtr->LogKeys();
            }
        }

        public void LoadCertificateChainFromPemFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.UTF8.GetBytes([.. filePath.ToCharArray(), '\u0000']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadCertChainFromPemFile(filePathPtr),
                    "Failed to load certificate chain from provided PEM file!"
                    );
            }
        }

        public void LoadPrivateKeyFromPemFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.UTF8.GetBytes([..filePath.ToCharArray(), '\u0000']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadPrivKeyFromPemFile(filePathPtr),
                    "Failed to load private key from provided PEM file!"
                    );
            }
        }

        public void LoadVerifyLocationsFromDirectory(string path)
        {
            fixed (byte* pathPtr = Encoding.UTF8.GetBytes([.. path.ToCharArray(), '\u0000']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadVerifyLocationsFromDirectory(pathPtr),
                    "Failed to load trusted CA locations from provided directory!"
                    );
            }
        }

        public void LoadVerifyLocationsFromFile(string filePath)
        {
            fixed (byte* filePathPtr = Encoding.UTF8.GetBytes([.. filePath.ToCharArray(), '\u0000']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadVerifyLocationsFromFile(filePathPtr),
                    "Failed to load trusted CA locations from provided file!"
                    );
            }
        }

        public void SetApplicationProtocols(params string[] protos)
        {
            List<byte> protoList = new();
            foreach (string proto in protos)
            {
                protoList.AddRange([(byte)proto.Length, .. Encoding.UTF8.GetBytes(proto)]);
            }

            fixed (byte* protosPtr = protoList.ToArray())
            {
                QuicheException.ThrowIfError((QuicheError)NativePtr->
                    SetApplicationProtos(protosPtr, (nuint)protoList.Count),
                    "Failed to set application protocols for this instance.");
            }
        }

        public void SetTicketKey(byte[] keyBytes)
        {
            fixed (byte* keyBytesPtr = keyBytes)
            {
                QuicheException.ThrowIfError((QuicheError)NativePtr->
                    SetTicketKey(keyBytesPtr, (nuint)keyBytes.Length),
                    "Failed to set ticket key contents for this instance.");
            }
        }

        #region IDisposable

        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                NativePtr->Free();
                NativePtr = null;

                disposedValue = true;
            }
        }

        ~QuicheConfig()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
