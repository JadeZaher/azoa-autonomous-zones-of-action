// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;

import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

interface IAzoaClassicRouter {
    struct ExactInputSingleParams {
        address tokenIn;
        address tokenOut;
        uint24 fee;
        address recipient;
        uint256 deadline;
        uint256 amountIn;
        uint256 amountOutMinimum;
        uint160 sqrtPriceLimitX96;
    }
    function factory() external view returns (address);
    function WETH9() external view returns (address);
    function exactInputSingle(ExactInputSingleParams calldata params) external payable returns (uint256 amountOut);
}

interface IAzoaV3Factory {
    function getPool(address tokenA, address tokenB, uint24 fee) external view returns (address);
}

interface IAzoaWrappedNative is IERC20 {
    function deposit() external payable;
    function withdraw(uint256 amount) external;
}

/// @notice Policy-bound exact-input swaps and input-denominated fees settle atomically.
contract AzoaDexExecutor is EIP712, ReentrancyGuard {
    using SafeERC20 for IERC20;

    struct Execution {
        bytes32 operation;
        bytes32 quoteHash;
        address owner;
        address recipient;
        address tokenIn;
        address tokenOut;
        address pool;
        address treasury;
        uint256 grossInput;
        uint256 platformFee;
        uint256 minimumOutput;
        uint24 poolFee;
        uint256 deadline;
        uint256 feeRevision;
        bytes32 policyKeyVersion;
        bytes32 inputCodeHash;
        bytes32 outputCodeHash;
        bool nativeInput;
        bool nativeOutput;
    }

    bytes32 private constant EXECUTION_TYPEHASH = keccak256("Execution(bytes32 payloadHash)");
    address public immutable router;
    address public immutable factory;
    address public immutable wrappedNative;
    address public immutable policySigner;
    bytes32 public immutable policyKeyVersion;
    bytes32 public immutable routerCodeHash;
    bytes32 public immutable factoryCodeHash;
    bytes32 public immutable wrappedNativeCodeHash;
    mapping(bytes32 => bool) public consumedOperations;

    event SwapExecuted(bytes32 indexed operation, bytes32 indexed quoteHash, address indexed owner,
        address recipient, address pool, address tokenIn, address tokenOut, uint256 grossInput,
        uint256 platformFee, uint256 output, address treasury, uint256 feeRevision);
    error InvalidAuthorization();
    error InvalidProtocol();
    error InvalidSettlement();

    constructor(address classicRouter, address poolFactory, address weth, address policy, bytes32 keyVersion)
        EIP712("AZOA DEX", "1")
    {
        if (classicRouter.code.length == 0 || poolFactory.code.length == 0 || weth.code.length == 0 ||
            policy == address(0) || keyVersion == bytes32(0) ||
            IAzoaClassicRouter(classicRouter).factory() != poolFactory || IAzoaClassicRouter(classicRouter).WETH9() != weth)
            revert InvalidProtocol();
        router = classicRouter;
        factory = poolFactory;
        wrappedNative = weth;
        policySigner = policy;
        policyKeyVersion = keyVersion;
        routerCodeHash = classicRouter.codehash;
        factoryCodeHash = poolFactory.codehash;
        wrappedNativeCodeHash = weth.codehash;
    }

    function authorizationDigest(bytes calldata payload) public view returns (bytes32) {
        return _hashTypedDataV4(keccak256(abi.encode(EXECUTION_TYPEHASH, keccak256(payload))));
    }

    function execute(bytes calldata payload, bytes calldata policyPermit) external payable nonReentrant returns (uint256 output) {
        Execution memory e = abi.decode(payload, (Execution));
        if (keccak256(payload) != keccak256(abi.encode(e)) || e.operation == bytes32(0) || e.quoteHash == bytes32(0) ||
            e.owner != msg.sender || e.recipient == address(0) || e.recipient == address(this) ||
            e.grossInput == 0 || e.platformFee >= e.grossInput || e.minimumOutput == 0 || e.feeRevision == 0 ||
            e.deadline < block.timestamp || e.policyKeyVersion != policyKeyVersion ||
            (e.platformFee != 0 && (e.treasury == address(0) || e.treasury == address(this))) ||
            consumedOperations[e.operation] || ECDSA.recover(authorizationDigest(payload), policyPermit) != policySigner)
            revert InvalidAuthorization();
        if (e.tokenIn == e.tokenOut || e.tokenIn.code.length == 0 || e.tokenOut.code.length == 0 ||
            e.tokenIn.codehash != e.inputCodeHash || e.tokenOut.codehash != e.outputCodeHash ||
            router.codehash != routerCodeHash || factory.codehash != factoryCodeHash || wrappedNative.codehash != wrappedNativeCodeHash ||
            e.pool == address(0) || IAzoaV3Factory(factory).getPool(e.tokenIn, e.tokenOut, e.poolFee) != e.pool ||
            (e.nativeInput && e.tokenIn != wrappedNative) || (e.nativeOutput && e.tokenOut != wrappedNative) ||
            msg.value != (e.nativeInput ? e.grossInput : 0)) revert InvalidProtocol();

        consumedOperations[e.operation] = true;
        IERC20 input = IERC20(e.tokenIn);
        IERC20 outputToken = IERC20(e.tokenOut);
        uint256 inputBefore = input.balanceOf(address(this));
        uint256 outputBefore = outputToken.balanceOf(address(this));
        uint256 nativeBefore = address(this).balance - msg.value;
        if (e.nativeInput) IAzoaWrappedNative(wrappedNative).deposit{value: e.grossInput}();
        else {
            uint256 ownerBefore = input.balanceOf(e.owner);
            input.safeTransferFrom(e.owner, address(this), e.grossInput);
            if (input.balanceOf(e.owner) + e.grossInput != ownerBefore) revert InvalidSettlement();
        }
        if (input.balanceOf(address(this)) != inputBefore + e.grossInput) revert InvalidSettlement();

        if (e.platformFee != 0) {
            if (e.nativeInput) {
                IAzoaWrappedNative(wrappedNative).withdraw(e.platformFee);
                _sendNative(e.treasury, e.platformFee);
            } else {
                uint256 treasuryBefore = input.balanceOf(e.treasury);
                input.safeTransfer(e.treasury, e.platformFee);
                if (input.balanceOf(e.treasury) != treasuryBefore + e.platformFee) revert InvalidSettlement();
            }
        }
        uint256 netInput = e.grossInput - e.platformFee;
        if (input.balanceOf(address(this)) != inputBefore + netInput || input.allowance(address(this), router) != 0)
            revert InvalidSettlement();
        input.forceApprove(router, netInput);
        if (input.allowance(address(this), router) != netInput) revert InvalidSettlement();
        output = IAzoaClassicRouter(router).exactInputSingle(IAzoaClassicRouter.ExactInputSingleParams(
            e.tokenIn, e.tokenOut, e.poolFee, address(this), e.deadline, netInput, e.minimumOutput, 0));
        input.forceApprove(router, 0);
        if (input.allowance(address(this), router) != 0 || input.balanceOf(address(this)) != inputBefore ||
            output < e.minimumOutput || outputToken.balanceOf(address(this)) != outputBefore + output)
            revert InvalidSettlement();
        if (e.nativeOutput) {
            IAzoaWrappedNative(wrappedNative).withdraw(output);
            _sendNative(e.recipient, output);
        } else {
            uint256 recipientBefore = outputToken.balanceOf(e.recipient);
            outputToken.safeTransfer(e.recipient, output);
            if (outputToken.balanceOf(e.recipient) != recipientBefore + output) revert InvalidSettlement();
        }
        if (input.balanceOf(address(this)) != inputBefore || outputToken.balanceOf(address(this)) != outputBefore ||
            address(this).balance != nativeBefore) revert InvalidSettlement();
        emit SwapExecuted(e.operation, e.quoteHash, e.owner, e.recipient, e.pool, e.tokenIn, e.tokenOut,
            e.grossInput, e.platformFee, output, e.treasury, e.feeRevision);
    }

    function _sendNative(address recipient, uint256 amount) private {
        (bool sent,) = payable(recipient).call{value: amount}("");
        if (!sent) revert InvalidSettlement();
    }

    receive() external payable {
        if (msg.sender != wrappedNative) revert InvalidAuthorization();
    }
}
