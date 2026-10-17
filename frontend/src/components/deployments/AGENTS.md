# Deployment controls

The generated artifact is immutable. Native operations quote the normalized server
artifact, show fee/rent/value caps and the effect before admission, and save the
exact request before sending. Lost responses retry the same key and body. Server
discovery is authoritative across devices. Pending receipts never imply a deployed
contract. Continuing a stage is explicit user authorization; observation is read-only.

Metadata updates authorize the complete quoted replacement, not an on-chain compare
and swap. Closing an account returns all lamports to the quoted destination; the
quote estimate is distinct from confirmed balance changes. Network fees can be spent
on a failed transaction. Runtime invocation uses the actual caller's authority and
returns a run to observe, not a fabricated execution result.

Recovery persists the exact authorized original-budget or linked-quote command
before sending. Remounts display the saved mode, payer and economic caps without
rewriting that command. Embedded recovery input may use the original .NET PascalCase
or web camelCase layout; conflicting aliases are invalid. A lost response retains
the command for exact replay. New consent requires authoritative resolution of the
previous request, not merely a transport error or local timeout. Resource effects
and cleanup receipts remain separate in the operation view.
