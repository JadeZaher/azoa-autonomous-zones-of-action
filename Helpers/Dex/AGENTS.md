# DEX request identity

Swap operation IDs hash a versioned domain plus authenticated actor/tenant and an
already hashed opaque request key. Preparation keys bind the operation, exact
stage, generation and immutable intent hash. These are application identities,
never invented blockchain transaction IDs. Provider code derives transaction IDs
from actual signed wire.

Stage observations are supplied only by the provider's authoritative observation
path, never client-submitted proof. The shared shape validator binds the saved
transaction IDs and network, enforces canonical unsigned costs within the saved
reservation, and requires immutable structured terminal evidence. Failed and
expired-absent stages cannot claim economic output or a platform fee; expired
absence cannot claim network expenditure. Pending data carries no economic
completion fields. A confirmed prerequisite resolves only its stage; operation
completion still requires the provider's complete stage history and settlement.

A confirmed stage may report unknown incremental rent only when it retains a
nonzero original reservation. Actual rent stays null, while the operation tracks
the full unresolved reservation separately from known actual costs. A later
provider history proof appends immutable rent accounting once; paired stage and
operation CAS removes exactly that reservation and adds only the proven actual.
Effect confirmation and accounting completeness are separate public statuses.
