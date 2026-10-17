# Deployment identities and composition

Identity hashes domain-separate durable operation records from signing keys. Native
authorization hashes freeze caller, target, inputs and aggregate fee/rent/value caps.

PredicateBindingRewriter follows the action predicate lexer's root and post-dot
segment grammar. It preserves quotes, escapes and whitespace, and consumes complete
paths so nested fields named run are never treated as selectors.

DeploymentGuaranteedOutputs proves outputs only for unconditional entry/control
paths that cannot be replaced by successful failure handling. Occurrence boundaries
wait for all selected paths, so skipped branches do not imply an available output.
Explicit cross-occurrence mappings must pass this proof; ambiguous mappings reject.
