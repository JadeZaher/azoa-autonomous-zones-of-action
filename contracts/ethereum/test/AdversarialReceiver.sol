// SPDX-License-Identifier: MIT
pragma solidity 0.8.30;

contract AdversarialReceiver {
    bool public reject;
    bool public blocked;
    address private target;
    bytes private attack;

    function configure(address target_, bytes calldata attack_, bool reject_) external {
        target = target_;
        attack = attack_;
        reject = reject_;
    }
    function _callback() private {
        require(!reject, "callback rejected");
        if (target != address(0)) {
            (bool succeeded,) = target.call(attack);
            require(!succeeded, "reentrancy escaped");
            blocked = true;
        }
    }
    receive() external payable { _callback(); }
    function onERC721Received(address, address, uint256, bytes calldata) external returns (bytes4) {
        _callback();
        return this.onERC721Received.selector;
    }
}
