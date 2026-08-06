namespace SIL.Motif.Generator.Checks;

// TODO(ADR 0023 decision 5): every kind must carry a required description, and the build must fail
// without one — "never hashed, so it is free to improve forever... Mandatory, following AIP-192 and
// OpenTelemetry's `brief`." Deliberately NOT implemented yet (docs/plan-motif.md, MOT-2's own
// instruction not to add this check here): there is no description source yet — a separate task is
// harvesting FieldWorks' label vocabulary (`strings-en.xml`, `.fwlayout` slice labels, tool config
// keyed by (ownerClass, ownerField)) per ADR 0023 decision 5's harvest note — and the manifest has no
// Description column to check against. When that source lands, the check belongs here, in the same
// fail-closed shape as every other check in this namespace: for every kind name
// KindNameDerivation produces, look up a description and throw a GeneratorException naming the kind
// if none exists. Do not add a description column or a failing check before the harvest exists.
