# Blockchain Devnet Providers — Specification

## Goal
Implement real Algorand and Solana devnet provider functionality to replace the current stub implementations with actual blockchain interactions.

## Motivation
The current providers return mock data and need to be replaced with real blockchain interactions for:
- Actual balance retrieval from devnet
- Real address validation
- Transaction simulation and status checking
- Token metadata fetching
- Proper error handling for network issues

## Architecture
Enhance the existing `IBlockchainProvider` implementations with real devnet connectivity using official SDKs.

### Modified Files
| Layer | File | Action |
|---|---|---|
| Providers | `Providers/Blockchain/AlgorandProvider.cs` | Replace stub methods with real Algorand SDK calls |
| Providers | `Providers/Blockchain/SolanaProvider.cs` | Replace stub methods with real Solana RPC calls |
| Models | `Models/Responses/BlockchainResult.cs` | Add new response types for devnet operations |
| Config | `appsettings.json` | Add devnet configuration settings |
| Tests | `AZOA.WebAPI.Tests/BlockchainProviderTests.cs` | Update tests for real provider behavior |

## Algorand Devnet Implementation

### Required Dependencies
```xml
<PackageReference Include="Algorand.Net" Version="2.0.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.0" />
```

### Configuration
```json
{
  "Blockchain": {
    "Algorand": {
      "Devnet": {
        "NodeUrl": "https://testnet-api.algonode.cloud",
        "ApiToken": "your-algorand-api-token",
        "TimeoutMs": 30000
      }
    }
  }
}
```

### Real Implementation Methods
```csharp
public async Task<AZOAResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default)
{
    try
    {
        var accountInfo = await _algodClient.GetAccountInformationAsync(address);
        var balance = accountInfo.Amount / Algorand.Utils.Algo.AlgosToMicroAlgos;
        
        return new AZOAResult<string>
        {
            Result = balance.ToString(),
            Message = $"Retrieved balance {balance} ALGO for address {address}"
        };
    }
    catch (Exception ex)
    {
        return new AZOAResult<string>
        {
            Success = false,
            Error = ex.Message,
            Message = "Failed to retrieve balance from Algorand devnet"
        };
    }
}

public async Task<AZOAResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default)
{
    try
    {
        // Algorand addresses are base32 encoded and typically 58 characters
        var isValid = address.Length == 58 && address.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c));
        
        if (isValid)
        {
            // Try to get account info to validate address exists
            await _algodClient.GetAccountInformationAsync(address);
        }
        
        return new AZOAResult<bool>
        {
            Result = isValid,
            Message = isValid ? "Valid Algorand address" : "Invalid Algorand address format"
        };
    }
    catch (Exception ex)
    {
        return new AZOAResult<bool>
        {
            Result = false,
            Success = false,
            Error = ex.Message,
            Message = "Address validation failed"
        };
    }
}
```

## Solana Devnet Implementation

### Required Dependencies
```xml
<PackageReference Include="Solnet.Rpc" Version="0.42.0" />
<PackageReference Include="Solnet.Wallet" Version="0.42.0" />
```

### Configuration
```json
{
  "Blockchain": {
    "Solana": {
      "Devnet": {
        "NodeUrl": "https://api.devnet.solana.com",
        "TimeoutMs": 30000
      }
    }
  }
}
```

### Real Implementation Methods
```csharp
public async Task<AZOAResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default)
{
    try
    {
        var balanceResult = await _rpcClient.GetBalanceAsync(address);
        
        if (balanceResult.WasSuccessful)
        {
            var balance = balanceResult.Result.Value / (decimal)LamportsPerSol;
            
            return new AZOAResult<string>
            {
                Result = balance.ToString(),
                Message = $"Retrieved balance {balance} SOL for address {address}"
            };
        }
        else
        {
            return new AZOAResult<string>
            {
                Success = false,
                Error = balanceResult.Reason,
                Message = "Failed to retrieve balance from Solana devnet"
            };
        }
    }
    catch (Exception ex)
    {
        return new AZOAResult<string>
        {
            Success = false,
            Error = ex.Message,
            Message = "Balance retrieval failed"
        };
    }
}

public async Task<AZOAResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default)
{
    try
    {
        // Solana addresses are base58 encoded
        var isValid = address.Length >= 32 && address.Length <= 44 && 
                     address.All(c => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c));
        
        if (isValid)
        {
            // Try to get balance to validate address exists
            var balanceResult = await _rpcClient.GetBalanceAsync(address);
            isValid = balanceResult.WasSuccessful;
        }
        
        return new AZOAResult<bool>
        {
            Result = isValid,
            Message = isValid ? "Valid Solana address" : "Invalid Solana address or address not found"
        };
    }
    catch (Exception ex)
    {
        return new AZOAResult<bool>
        {
            Result = false,
            Success = false,
            Error = ex.Message,
            Message = "Address validation failed"
        };
    }
}
```

## Enhanced Error Handling
```csharp
public class BlockchainProviderException : Exception
{
    public BlockchainProviderException(string message, Exception? innerException = null) 
        : base(message, innerException) { }
}

public async Task<AZOAResult<string>> GetTransactionStatusAsync(string txHash, CancellationToken ct = default)
{
    try
    {
        AZOAResult<Dictionary<string, object>> result;
        
        if (ChainType == "Algorand")
        {
            var txInfo = await _algodClient.GetTransactionInformationAsync(txHash);
            result = new AZOAResult<Dictionary<string, object>>
            {
                Result = new Dictionary<string, object>
                {
                    ["txHash"] = txHash,
                    ["status"] = txInfo.Confirmed ? "confirmed" : "pending",
                    ["block"] = txInfo.Confirmed ? txInfo.ConfirmedRound.ToString() : null,
                    ["fee"] = txInfo.Fee.ToString()
                },
                Message = "Transaction status retrieved"
            };
        }
        else if (ChainType == "Solana")
        {
            var txResult = await _rpcClient.GetTransactionAsync(txHash);
            result = new AZOAResult<Dictionary<string, object>>
            {
                Result = new Dictionary<string, object>
                {
                    ["txHash"] = txHash,
                    ["status"] = txResult.WasSuccessful ? "confirmed" : "not_found",
                    ["block"] = txResult.Result?.BlockTime?.ToString(),
                    ["fee"] = txResult.Result?.Meta?.Fee?.ToString()
                },
                Message = "Transaction status retrieved"
            };
        }
        else
        {
            throw new BlockchainProviderException($"Unsupported chain type: {ChainType}");
        }
        
        return result as AZOAResult<string> ?? throw new BlockchainProviderException("Unexpected response type");
    }
    catch (Exception ex)
    {
        return new AZOAResult<string>
        {
            Success = false,
            Error = ex.Message,
            Message = "Failed to get transaction status"
        };
    }
}
```

## Configuration Management
```csharp
public class BlockchainNetworkConfig
{
    public string NodeUrl { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 30000;
    public bool EnableLogging { get; set; } = false;
}

public enum ChainNetwork
{
    Devnet,
    Testnet,
    Mainnet
}
```

## Acceptance Criteria
- [ ] Algorand provider retrieves real balances from devnet
- [ ] Solana provider retrieves real balances from devnet
- [ ] Address validation works for both chains
- [ ] Transaction status checking works for both chains
- [ ] Proper error handling for network timeouts and failures
- [ ] Configuration system supports devnet/testnet/mainnet
- [ ] All provider methods return real data instead of stubs
- [ ] Unit tests cover real provider behavior
- [ ] Integration tests validate devnet connectivity
- [ ] Performance benchmarks meet requirements