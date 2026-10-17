// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;

import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";

contract DexTestToken is ERC20 {
    constructor() ERC20("DEX protocol fixture", "FIX") {}
    function mint(address recipient, uint256 amount) external { _mint(recipient, amount); }
}

contract DexNativeReceiver {
    address private target;
    bytes private callbackData;
    uint256 private callbackValue;
    bool private rejectPayment;
    bool public attempted;
    bool public succeeded;

    function configure(address executor, bytes calldata data, uint256 value, bool reject) external {
        target = executor;
        callbackData = data;
        callbackValue = value;
        rejectPayment = reject;
    }

    function run(bytes calldata data, uint256 value) external payable {
        (bool ok,) = target.call{value: value}(data);
        require(ok, "outer execution failed");
    }

    receive() external payable {
        require(!rejectPayment, "payment refused");
        attempted = true;
        (succeeded,) = target.call{value: callbackValue}(callbackData);
    }
}
