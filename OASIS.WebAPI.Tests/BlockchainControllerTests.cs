using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using OASIS.WebAPI.Controllers;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Responses;
using Xunit;

namespace OASIS.WebAPI.Tests
{
    public class BlockchainControllerTests
    {
        private readonly Mock<IBlockchainProviderFactory> _mockProviderFactory;
        private readonly Mock<ILogger<BlockchainController>> _mockLogger;
        private readonly BlockchainController _controller;

        public BlockchainControllerTests()
        {
            _mockProviderFactory = new Mock<IBlockchainProviderFactory>();
            _mockLogger = new Mock<ILogger<BlockchainController>>();
            _controller = new BlockchainController(_mockProviderFactory.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task GetBalanceAsync_ValidAddress_ReturnsSuccess()
        {
            // Arrange
            var request = new
            {
                ChainType = "Algorand",
                Address = "7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY"
            };

            var mockProvider = new Mock<IBlockchainProvider>();
            mockProvider.Setup(p => p.GetBalanceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OASISResult<string>
                {
                    Success = true,
                    Result = "100.5",
                    Message = "Balance retrieved successfully"
                });

            _mockProviderFactory.Setup(f => f.GetProvider("Algorand")).Returns(mockProvider.Object);

            // Act
            var result = await _controller.GetBalanceAsync(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<OASISResult<string>>(okResult.Value);
            Assert.True(response.Success);
            Assert.Equal("100.5", response.Result);
        }

        [Fact]
        public async Task GetBalanceAsync_EmptyAddress_ReturnsBadRequest()
        {
            // Arrange
            var request = new
            {
                ChainType = "Algorand",
                Address = ""
            };

            // Act
            var result = await _controller.GetBalanceAsync(request);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = Assert.IsType<OASISResult<string>>(badRequestResult.Value);
            Assert.False(response.Success);
            Assert.Equal("Address is required", response.Message);
        }

        [Fact]
        public async Task ValidateAddressAsync_ValidAddress_ReturnsSuccess()
        {
            // Arrange
            var request = new
            {
                ChainType = "Algorand",
                Address = "7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY"
            };

            var mockProvider = new Mock<IBlockchainProvider>();
            mockProvider.Setup(p => p.ValidateAddressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OASISResult<bool>
                {
                    Success = true,
                    Result = true,
                    Message = "Address is valid"
                });

            _mockProviderFactory.Setup(f => f.GetProvider("Algorand")).Returns(mockProvider.Object);

            // Act
            var result = await _controller.ValidateAddressAsync(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<OASISResult<bool>>(okResult.Value);
            Assert.True(response.Success);
            Assert.True(response.Result);
        }

        [Fact]
        public async Task GetChainInfoAsync_Algorand_ReturnsSuccess()
        {
            // Arrange
            var mockProvider = new Mock<IBlockchainProvider>();
            mockProvider.Setup(p => p.GetChainInfoAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OASISResult<Dictionary<string, object>>
                {
                    Success = true,
                    Result = new Dictionary<string, object>
                    {
                        { "chain", "Algorand" },
                        { "network", "Devnet" },
                        { "round", "12345678" }
                    },
                    Message = "Chain info retrieved successfully"
                });

            _mockProviderFactory.Setup(f => f.GetProvider("Algorand")).Returns(mockProvider.Object);

            // Act
            var result = await _controller.GetChainInfoAsync("Algorand");

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<OASISResult<Dictionary<string, object>>>(okResult.Value);
            Assert.True(response.Success);
            Assert.Equal("Algorand", response.Result["chain"]);
        }

        [Fact]
        public async Task TransferAsync_ValidRequest_ReturnsSuccess()
        {
            // Arrange
            var request = new
            {
                ChainType = "Algorand",
                FromAddress = "7J6ZZGF2UPNKKBCJA4DHFKVL6LXGKKDQM6KX4YZ5J5H5F7ZJGX6W4PUJJY",
                ToAddress = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRSTUVWXYZ234567",
                Amount = 100
            };

            var mockProvider = new Mock<IBlockchainProvider>();
            mockProvider.Setup(p => p.TransferAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OASISResult<string>
                {
                    Success = true,
                    Result = "TX1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
                    Message = "Transfer completed successfully"
                });

            _mockProviderFactory.Setup(f => f.GetProvider("Algorand")).Returns(mockProvider.Object);

            // Act
            var result = await _controller.TransferAsync(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<OASISResult<string>>(okResult.Value);
            Assert.True(response.Success);
            Assert.StartsWith("TX", response.Result);
        }
    }
}