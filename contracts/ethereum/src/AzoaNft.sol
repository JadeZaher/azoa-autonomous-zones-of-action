// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;

import {ERC721} from "@openzeppelin/contracts/token/ERC721/ERC721.sol";
import {ECDSA} from "@openzeppelin/contracts/utils/cryptography/ECDSA.sol";
import {EIP712} from "@openzeppelin/contracts/utils/cryptography/EIP712.sol";
import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

/// @notice Custody-authorized NFT effects and native fees settle in one transaction.
contract AzoaNft is ERC721, EIP712, ReentrancyGuard {
    struct Execution {
        bytes32 lifecycle;
        uint8 operation;
        uint8 stage;
        uint32 generation;
        uint256 tokenId;
        address from;
        address to;
        address actor;
        address issuer;
        address beneficiary;
        bool soulbound;
        bool finalEffect;
        string metadata;
        uint256 fee;
        uint256 quotedFee;
        address treasury;
        uint256 revision;
        address payer;
        uint256 deadline;
    }

    bytes32 private constant EXECUTION_TYPEHASH = keccak256("Execution(bytes32 payloadHash)");
    address public immutable credentialAuthority;
    mapping(bytes32 => bool) public consumedStages;
    mapping(bytes32 => bool) public consumedEffects;
    mapping(bytes32 => bytes32) public lifecycleIdentity;
    mapping(bytes32 => address) public acceptedRecipient;
    mapping(uint256 => bool) public everMinted;
    mapping(uint256 => address) public issuerOf;
    mapping(uint256 => bool) public soulbound;
    mapping(uint256 => bool) public delivered;
    mapping(uint256 => string) private _metadata;
    bool private _executing;
    uint8 private _stage;

    event EffectExecuted(bytes32 indexed lifecycle, uint8 indexed stage, uint256 indexed tokenId, address owner, uint256 fee);
    error InvalidAuthorization();
    error InvalidEffect();
    error DirectMutationDisabled();

    constructor(address authority) ERC721("AZOA NFT", "AZOA") EIP712("AZOA NFT", "1") {
        if (authority == address(0)) revert InvalidAuthorization();
        credentialAuthority = authority;
    }

    function authorizationDigest(bytes calldata payload) public view returns (bytes32) {
        return _hashTypedDataV4(keccak256(abi.encode(EXECUTION_TYPEHASH, keccak256(payload))));
    }

    function execute(bytes calldata payload, bytes calldata effectPermit, bytes calldata policyPermit) external payable nonReentrant {
        Execution memory e = abi.decode(payload, (Execution));
        if (e.lifecycle == bytes32(0) || e.actor == address(0) || e.tokenId == 0 || e.revision == 0
            || e.payer != msg.sender || e.deadline < block.timestamp || msg.value != e.fee
            || (e.quotedFee > 0 && e.treasury == address(0))
            || (e.finalEffect && e.fee > 0 && e.payer != e.actor)) revert InvalidAuthorization();
        bytes32 digest = authorizationDigest(payload);
        if (ECDSA.recover(digest, policyPermit) != credentialAuthority) revert InvalidAuthorization();
        bytes32 stageKey = keccak256(abi.encode(e.lifecycle, e.operation, e.stage, e.generation));
        bytes32 effectKey = keccak256(abi.encode(e.lifecycle, e.operation));
        if (consumedStages[stageKey] || consumedEffects[effectKey]) revert InvalidEffect();
        bytes32 identity = keccak256(abi.encode(e.operation, e.tokenId, e.actor, e.issuer, e.beneficiary,
            e.soulbound, keccak256(bytes(e.metadata)), e.quotedFee, e.treasury, e.revision));
        if (lifecycleIdentity[effectKey] != bytes32(0) && lifecycleIdentity[effectKey] != identity) revert InvalidEffect();
        lifecycleIdentity[effectKey] = identity;
        _validateStage(e);
        address authority = _effectAuthority(e);
        if (authority == address(0) || ECDSA.recover(digest, effectPermit) != authority) revert InvalidAuthorization();
        consumedStages[stageKey] = true;
        if (e.finalEffect) consumedEffects[effectKey] = true;
        _executing = true;
        _stage = e.stage;
        if (e.stage == 0) {
            if (everMinted[e.tokenId] || _ownerOf(e.tokenId) != address(0) || e.from != address(0) || e.to == address(0)) revert InvalidEffect();
            everMinted[e.tokenId] = true;
            if (e.soulbound && (e.issuer != e.actor || e.to != credentialAuthority)) revert InvalidAuthorization();
            soulbound[e.tokenId] = e.soulbound;
            issuerOf[e.tokenId] = e.soulbound ? e.issuer : address(0);
            _metadata[e.tokenId] = e.metadata;
            _safeMint(e.to, e.tokenId);
        } else if (e.stage == 1) {
            if (_ownerOf(e.tokenId) != e.from || e.to == address(0) || e.to != e.beneficiary) revert InvalidEffect();
            acceptedRecipient[effectKey] = e.to;
        } else if (e.stage == 2 || e.stage == 3 || e.stage == 4) {
            if (_ownerOf(e.tokenId) != e.from || e.to == address(0)) revert InvalidEffect();
            if (e.stage == 2 && (acceptedRecipient[effectKey] != e.to || e.to != e.beneficiary)) revert InvalidAuthorization();
            _safeTransfer(e.from, e.to, e.tokenId);
            if (e.stage == 2) delivered[e.tokenId] = true;
        } else {
            if (_ownerOf(e.tokenId) != e.from || e.to != address(0)) revert InvalidEffect();
            _burn(e.tokenId);
        }
        _executing = false;
        if (e.fee > 0) {
            (bool paid,) = payable(e.treasury).call{value: e.fee}("");
            if (!paid) revert InvalidEffect();
        }
        emit EffectExecuted(e.lifecycle, e.stage, e.tokenId, _ownerOf(e.tokenId), e.fee);
    }

    function _validateStage(Execution memory e) private view {
        bool finalStage;
        if (e.stage == 0) {
            if (e.operation != 0) revert InvalidEffect();
            finalStage = !e.soulbound;
        } else {
            if (_ownerOf(e.tokenId) == address(0) || soulbound[e.tokenId] != e.soulbound
                || keccak256(bytes(_metadata[e.tokenId])) != keccak256(bytes(e.metadata))) revert InvalidEffect();
            if (e.soulbound && issuerOf[e.tokenId] != e.issuer) revert InvalidAuthorization();
            if (e.stage == 1) {
                if (e.operation > 4 || e.operation == 3) revert InvalidEffect();
            } else if (e.stage == 2) {
                if (e.operation != 0 || !e.soulbound || delivered[e.tokenId] || e.from != credentialAuthority) revert InvalidEffect();
                finalStage = true;
            } else if (e.stage == 3) {
                if ((e.operation != 1 && e.operation != 4) || e.soulbound) revert InvalidEffect();
                finalStage = true;
            } else if (e.stage == 4) {
                if (e.operation != 2 || e.soulbound) revert InvalidEffect();
            } else if (e.stage == 5) {
                if (e.operation != 2 || e.soulbound) revert InvalidEffect();
                finalStage = true;
            } else if (e.stage == 6) {
                if (e.operation != 3 || !e.soulbound) revert InvalidEffect();
                finalStage = true;
            } else revert InvalidEffect();
        }
        if (e.finalEffect != finalStage || e.fee != (finalStage ? e.quotedFee : 0)) revert InvalidEffect();
    }

    function _effectAuthority(Execution memory e) private view returns (address) {
        if (e.stage == 0) return e.actor;
        if (e.stage == 1) return e.to;
        if (e.stage == 2 || e.stage == 6) return issuerOf[e.tokenId];
        return _ownerOf(e.tokenId);
    }

    function tokenURI(uint256 tokenId) public view override returns (string memory) {
        _requireOwned(tokenId);
        return _metadata[tokenId];
    }

    function approve(address, uint256) public pure override { revert DirectMutationDisabled(); }
    function setApprovalForAll(address, bool) public pure override { revert DirectMutationDisabled(); }
    function transferFrom(address, address, uint256) public pure override { revert DirectMutationDisabled(); }
    function safeTransferFrom(address, address, uint256, bytes memory) public pure override { revert DirectMutationDisabled(); }

    function _update(address to, uint256 tokenId, address auth) internal override returns (address) {
        if (!_executing) revert DirectMutationDisabled();
        address from = _ownerOf(tokenId);
        if (soulbound[tokenId] && from != address(0) && to != address(0)
            && (_stage != 2 || delivered[tokenId] || from != credentialAuthority)) revert InvalidEffect();
        return super._update(to, tokenId, auth);
    }
}
