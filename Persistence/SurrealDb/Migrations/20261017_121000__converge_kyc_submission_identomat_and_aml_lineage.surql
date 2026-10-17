-- kyc_submission ships through its immutable compatibility baseline, so the generated golden is
-- not what a deployment applies. Converge the baseline to the current POCO: Identomat is an
-- admitted provider (without it every Identomat KYC write fails the ASSERT) and the AML gate
-- reads aml_lineage_key. Both are additive. See ../AGENTS.md §compatibility-baselines.
DEFINE PARAM OVERWRITE $kyc_submission_provider VALUE ["MANUAL", "VERIFF", "GENERIC_HOSTED", "IDENTOMAT"];
DEFINE FIELD IF NOT EXISTS aml_lineage_key ON TABLE kyc_submission TYPE option<string>;
