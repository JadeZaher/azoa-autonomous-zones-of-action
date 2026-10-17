"""Deploys the pinned Wormhole core + token bridge TEAL on AlgoKit LocalNet with the local guardian set.

Runs inside the pinned Python 3.10 container with the pinned upstream algorand/ sources on sys.path and drives the
upstream PortalCore helpers directly (never admin.py's devnet boot, which reaches a public testnet compiler).
Usage: deploy.py <algod-url> <kmd-url> <guardian-set-upgrade-vaa-hex> <state-out.json>. See ../AGENTS.md §Algorand.
"""
import argparse
import base64
import hashlib
import json
import sys
from urllib.parse import urlparse

from algosdk.encoding import decode_address
from algosdk.future.transaction import LogicSig
from algosdk.logic import get_application_address

from admin import PortalCore
from vaa_verify import get_vaa_verify

algod_url, kmd_url, boot_vaa_hex, state_out = sys.argv[1:5]
for url in (algod_url, kmd_url):
    if urlparse(url).hostname not in ("127.0.0.1", "host.containers.internal"):
        raise SystemExit("LocalNet endpoints must be loopback (or the container's host loopback alias).")

portal = PortalCore()
portal.args = argparse.Namespace(core_approve="teal/core_approve.teal", core_clear="teal/core_clear.teal",
                                 token_approve="teal/token_approve.teal", token_clear="teal/token_clear.teal",
                                 testnet=False, env="/nonexistent")
portal.ALGOD_ADDRESS, portal.KMD_ADDRESS = algod_url, kmd_url
portal.devnet = True
portal.client = portal.getAlgodClient()
portal.vaa_verify = portal.client.compile(get_vaa_verify())
portal.vaa_verify["lsig"] = LogicSig(base64.b64decode(portal.vaa_verify["result"]))
portal.foundation = portal.getTemporaryAccount(portal.client)

portal.coreid = portal.createPortalCoreApp(client=portal.client, sender=portal.foundation)
portal.tokenid = portal.createTokenBridgeApp(portal.client, portal.foundation)
portal.bootGuardians(bytes.fromhex(boot_vaa_hex), portal.client, portal.foundation, portal.coreid)


def program_sha256(app_id):
    params = portal.client.application_info(app_id)["params"]
    return hashlib.sha256(base64.b64decode(params["approval-program"])).hexdigest()


genesis = portal.client.versions()
state = {
    "algod": algod_url, "wormholeChainId": 8, "genesisId": genesis["genesis_id"], "genesisHash": genesis["genesis_hash_b64"],
    "coreAppId": str(portal.coreid), "tokenBridgeAppId": str(portal.tokenid),
    "coreApprovalSha256": program_sha256(portal.coreid), "tokenBridgeApprovalSha256": program_sha256(portal.tokenid),
    "vaaVerifyHash": portal.vaa_verify["hash"],
    "tokenBridgeEmitterHex": decode_address(get_application_address(portal.tokenid)).hex(),
    "foundationMnemonic": portal.foundation.getMnemonic(), "registered": {},
}
with open(state_out, "w", encoding="utf-8") as handle:
    json.dump(state, handle, indent=2)
print(json.dumps({key: value for key, value in state.items() if key != "foundationMnemonic"}))
