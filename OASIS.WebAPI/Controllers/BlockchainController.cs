using Microsoft.AspNetCore.Mvc;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Responses;
using OASIS.WebAPI.Providers.Blockchain;

namespace OASIS.WebAPI.Controllers;

[ApiController]
[Route("api/blockchain")]
public class BlockchainController : ControllerBase
{
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly ILogger<BlockchainController> _logger;

    public BlockchainController(
        IBlockchainProviderFactory providerFactory,
        ILogger<BlockchainController> logger)
    {
        _providerFactory = providerFactory;
        _logger = logger;
    }

    [HttpPost("balance")]
    public async Task<IActionResult> GetBalanceAsync([FromBody] BalanceRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Address))
            {
                return BadRequest(new OASISResult<string>
                {
                    Success = false,
                    Message = "Address is required"
                });
            }

            var provider = _providerFactory.GetProvider(request.ChainType);
            var result = await provider.GetBalanceAsync(request.Address, request.TokenId);

            _logger.LogInformation("Balance retrieved for {Address} on {ChainType}", request.Address, request.ChainType);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving balance for {Address}", request.Address);
            return StatusCode(500, new OASISResult<string>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }

    [HttpPost("validate-address")]
    public async Task<IActionResult> ValidateAddressAsync([FromBody] AddressValidationRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Address))
            {
                return BadRequest(new OASISResult<bool>
                {
                    Success = false,
                    Message = "Address is required"
                });
            }

            var provider = _providerFactory.GetProvider(request.ChainType);
            var result = await provider.ValidateAddressAsync(request.Address);

            _logger.LogInformation("Address validation result for {Address}: {IsValid}", request.Address, result.Result);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating address {Address}", request.Address);
            return StatusCode(500, new OASISResult<bool>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> TransferAsync([FromBody] TransferRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.FromAddress) || string.IsNullOrWhiteSpace(request.ToAddress))
            {
                return BadRequest(new OASISResult<string>
                {
                    Success = false,
                    Message = "From and to addresses are required"
                });
            }

            if (request.Amount <= 0)
            {
                return BadRequest(new OASISResult<string>
                {
                    Success = false,
                    Message = "Amount must be positive"
                });
            }

            var provider = _providerFactory.GetProvider(request.ChainType);
            var result = await provider.TransferAsync(
                request.TokenId,
                request.FromAddress,
                request.ToAddress,
                request.Amount);

            _logger.LogInformation("Transfer initiated from {From} to {To} on {ChainType}", 
                request.FromAddress, request.ToAddress, request.ChainType);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing transfer from {From} to {To}", 
                request.FromAddress, request.ToAddress);
            return StatusCode(500, new OASISResult<string>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }

    [HttpPost("transaction-status")]
    public async Task<IActionResult> GetTransactionStatusAsync([FromBody] TransactionStatusRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.TxHash))
            {
                return BadRequest(new OASISResult<Dictionary<string, object>>
                {
                    Success = false,
                    Message = "Transaction hash is required"
                });
            }

            var provider = _providerFactory.GetProvider(request.ChainType);
            var result = await provider.GetTransactionStatusAsync(request.TxHash);

            _logger.LogInformation("Transaction status retrieved for {TxHash} on {ChainType}", 
                request.TxHash, request.ChainType);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving transaction status for {TxHash}", request.TxHash);
            return StatusCode(500, new OASISResult<Dictionary<string, object>>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }

    [HttpPost("token-metadata")]
    public async Task<IActionResult> GetTokenMetadataAsync([FromBody] TokenMetadataRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.TokenId))
            {
                return BadRequest(new OASISResult<Dictionary<string, object>>
                {
                    Success = false,
                    Message = "Token ID is required"
                });
            }

            var provider = _providerFactory.GetProvider(request.ChainType);
            var result = await provider.GetTokenMetadataAsync(request.TokenId);

            _logger.LogInformation("Token metadata retrieved for {TokenId} on {ChainType}", 
                request.TokenId, request.ChainType);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving token metadata for {TokenId}", request.TokenId);
            return StatusCode(500, new OASISResult<Dictionary<string, object>>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }

    [HttpPost("tokens-by-owner")]
    public async Task<IActionResult> GetTokensByOwnerAsync([FromBody] TokensByOwnerRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.OwnerAddress))
            {
                return BadRequest(new OASISResult<List<Dictionary<string, object>>>
                {
                    Success = false,
                    Message = "Owner address is required"
                });
            }

            var provider = _providerFactory.GetProvider(request.ChainType);
            var result = await provider.GetTokensByOwnerAsync(request.OwnerAddress);

            _logger.LogInformation("Tokens retrieved for owner {OwnerAddress} on {ChainType}", 
                request.OwnerAddress, request.ChainType);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tokens for owner {OwnerAddress}", request.OwnerAddress);
            return StatusCode(500, new OASISResult<List<Dictionary<string, object>>>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }

    [HttpGet("chain-info/{chainType}")]
    public async Task<IActionResult> GetChainInfoAsync(string chainType)
    {
        try
        {
            var provider = _providerFactory.GetProvider(chainType);
            var result = await provider.GetChainInfoAsync();

            _logger.LogInformation("Chain info retrieved for {ChainType}", chainType);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving chain info for {ChainType}", chainType);
            return StatusCode(500, new OASISResult<Dictionary<string, object>>
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }
}

// Request DTOs
public class BalanceRequest
{
    public string ChainType { get; set; } = "Algorand";
    public string Address { get; set; } = string.Empty;
    public string? TokenId { get; set; }
}

public class AddressValidationRequest
{
    public string ChainType { get; set; } = "Algorand";
    public string Address { get; set; } = string.Empty;
}

public class TransferRequest
{
    public string ChainType { get; set; } = "Algorand";
    public string? TokenId { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public int Amount { get; set; }
}

public class TransactionStatusRequest
{
    public string ChainType { get; set; } = "Algorand";
    public string TxHash { get; set; } = string.Empty;
}

public class TokenMetadataRequest
{
    public string ChainType { get; set; } = "Algorand";
    public string TokenId { get; set; } = string.Empty;
}

public class TokensByOwnerRequest
{
    public string ChainType { get; set; } = "Algorand";
    public string OwnerAddress { get; set; } = string.Empty;
}