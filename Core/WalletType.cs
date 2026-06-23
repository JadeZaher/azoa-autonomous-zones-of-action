namespace AZOA.WebAPI.Core;

/// <summary>
/// Denotes how a wallet was created or connected.
/// </summary>
public enum WalletType
{
    /// <summary>Connected via external browser wallet (MetaMask, Ghost, Pera, etc.)</summary>
    External = 0,

    /// <summary>Generated and managed by the AZOA platform (keys stored encrypted)</summary>
    Platform = 1
}
