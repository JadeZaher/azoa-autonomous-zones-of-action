"""Submits one locally signed RegisterChain VAA to the LocalNet token bridge through the pinned upstream submitVAA.

Usage: register.py <state.json> <register-chain-vaa-hex>. See ../AGENTS.md §Algorand.
"""
import argparse
import json
import sys

from algosdk import mnemonic

from admin import Account, PortalCore

state_path, vaa_hex = sys.argv[1:3]
with open(state_path, encoding="utf-8") as handle:
    state = json.load(handle)

portal = PortalCore()
portal.args = argparse.Namespace(testnet=False, env="/nonexistent")
portal.ALGOD_ADDRESS = state["algod"]
portal.devnet = True
portal.client = portal.getAlgodClient()
portal.coreid, portal.tokenid = int(state["coreAppId"]), int(state["tokenBridgeAppId"])
portal.foundation = Account(mnemonic.to_private_key(state["foundationMnemonic"]))
vaa = bytes.fromhex(vaa_hex)
payload = vaa[6 + vaa[5] * 66 + 51:]
chain, emitter = int.from_bytes(payload[35:37], "big"), payload[37:69].hex()
portal.submitVAA(vaa, portal.client, portal.foundation, portal.tokenid)
state["registered"][str(chain)] = emitter
with open(state_path, "w", encoding="utf-8") as handle:
    json.dump(state, handle, indent=2)
print(json.dumps({"chain": chain, "emitter": emitter}))
