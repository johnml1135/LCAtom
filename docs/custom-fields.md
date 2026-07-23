# Custom-field contract

## Why custom fields are a separate operation family

A LibLCM custom field consists of:

1. a runtime metadata definition; and
2. typed values attached to ordinary LibLCM objects.

Values are stored in `LcmCache.CustomProperties`, keyed internally by `(object, flid)`, and
serialized with the project. The `flid` is assigned by LibLCM for a particular project and is not a
portable identity.

Custom fields can contain booleans, integers, dates, GUIDs, binary data, Unicode, rich strings,
multilingual alternatives, owning atomic/collection/sequence relationships, and reference
atomic/collection/sequence relationships. They are not an untyped JSON property bag.

## Actual use cases

### Frequent: discovery and reading

Applications enumerate or resolve a field, validate its definition, obtain the cache-local `flid`,
and read values. Examples include workflow status, corpus evidence, parser annotations, external
IDs, or project-specific lexical classifications.

### Frequent: editing values

Change Sets set or clear typed scalar/text alternatives, attach/detach references, or edit
collections and sequences. Every operation validates the live definition, applicable owner class,
destination type, cardinality, and writing system.

### Occasional: feature/schema installation

An application or project installs a required field during setup, import, or the first Change Set
that uses it. Definition should have ensure semantics:

- absent: create;
- present and structurally compatible: reuse and report no-op/metadata differences;
- same class/name with incompatible structure: conflict;
- similar label or content: never infer a match.

### Rare: metadata update or destructive migration

LibLCM supports updating only help text, writing-system selector, and user-facing label after
installation. Internal name, owner class, type, destination class, assigned ID, and list root are
not supported in-place changes in the inspected LibLCM API and are immutable in this contract.

A structural change is an explicit migration:

```text
define replacement → explicitly copy/transform values → validate → delete old definition
```

The runner may execute authored migration operations but never invents conversions.

Definition deletion is destructive. It removes values and can delete objects owned through owning
custom fields. It requires a complete expected-effects assessment and changed-cascade warning.

## Identity model

There are three distinct identities.

### Runtime identity

The `flid` is valid only within the loaded project/cache. It may appear in a mutation plan,
diagnostic, or receipt but never in portable intent.

### Physical portable locator

The authoritative locator is:

```text
(owning LibLCM class, immutable internal field name)
```

Example:

```json
{
  "ownerClass": "LexEntry",
  "name": "GrammarReviewStatus",
  "expectedDefinition": {
    "type": "MultiUnicode",
    "writingSystemSelector": "analysis"
  }
}
```

Labels are mutable, localized, and potentially non-unique, so they are never identity.

`ownerClass` is the exact LibLCM metadata class name returned by the supported metadata cache, not
a CLR fully-qualified type name, translated label, or numeric class ID. Owner class and internal
field name compare as ordinal, case-sensitive strings after successful LibLCM metadata lookup; the
runner applies no independent Unicode normalization. The definition is declared on that class,
while LibLCM metadata determines its applicability to subclasses.

Every value operation must supply the expected Cellar property type. Object-valued operations must
also supply the expected destination class. The immutable structural signature is property type,
destination class where applicable, and list root where applicable. Writing-system selector is
mutable metadata: an optional expectation mismatch warns but does not redefine physical identity.
V1 treats list root as immutable because the supported LibLCM update API does not update it.

Although `FieldDescription` exposes a historical `CustomId`, the inspected current LibLCM
creation/persistence path initializes it to `Guid.Empty`, and public `AddCustomField` does not
accept a caller-supplied GUID. V1 must not pretend custom definitions have durable GUID identity.

### Optional external logical contract key

Shared applications may add non-authoritative metadata such as:

```text
org.sil.gramtrans/grammar-review-status/v1
```

This can help applications and future registries recognize a convention across projects. LibLCM
does not persist or enforce it, so resolution still uses class/name and verifies the structural
signature. It must not be represented as a LibLCM entity GUID.

Whether a named logical-contract-key property belongs in the v1 canonical schema was not settled.
Until it is explicitly decided, clients may carry such a key only inside non-semantic
`extensions`; the runner preserves but does not interpret it. It is not part of field resolution
or the intent digest.

## Definition operations

### `customField/define`

Carries:

- owner class;
- immutable internal name;
- Cellar property type;
- destination class where applicable;
- list root where applicable;
- writing-system selector;
- label;
- help;
- optional client metadata in `extensions`; no normative v1 logical key has yet been approved.

It validates using LibLCM metadata rules and resolves the assigned `flid` during apply.

### `customField/updateDisplayMetadata`

May change only:

- user-facing label;
- help;
- writing-system selector.

Changing the selector may affect interpretation/display and must appear in effects and warnings.

### `customField/delete`

Use the high-level `FieldDescription.MarkForDeletion`/`UpdateCustomField` behavior or a proven
semantic equivalent, because it removes stored values and handles owned-object deletion before
metadata removal. Do not call low-level `DeleteCustomField(flid)` without performing its required
cleanup.

Preview and receipt enumerate:

- objects carrying values;
- removed alternatives/values;
- deleted owned objects;
- affected references;
- definition removal.

## Value operations

Final names follow the contract-wide naming review, but semantic families include:

- scalar set/clear;
- Unicode and rich-string set/clear;
- multilingual alternative set/clear;
- atomic reference attach/detach;
- reference collection add/remove;
- reference sequence insert/move/remove;
- owned entity create/delete/placement through the ordinary entity vocabulary.

A field definition must exist before the first value operation executes. It may be defined earlier
in the same Change Set. Later operations reference it by class/name, not by its newly allocated
`flid`.

## Mechanical diff

Normalize definitions before their values.

- Match definitions only by owner class plus internal name.
- Ignore runtime `flid` differences.
- Emit define for a target-only definition.
- Emit delete for a source-only definition.
- Emit supported metadata updates for label/help/selector differences.
- Treat incompatible type, destination, class, internal name, or list-root changes as explicit
  migration requirements, not in-place updates.
- Compare values according to declared Cellar property type and LibLCM normalization.

Two unrelated projects can independently use the same class/name and compatible structure for
different human purposes. Mechanical diff will treat them as the same physical field because it
does not infer linguistic meaning. Applications needing stronger cross-project assurance must
provide governance or an external logical-key mapping; the runner will not guess.

## Resolved interface shape

The implementation should expose concepts equivalent to:

```csharp
IEnumerable<CustomFieldDefinition> EnumerateCustomFields();
ResolvedCustomField Resolve(CustomFieldRef reference);
CustomFieldValue Read(ICmObject target, ResolvedCustomField field);
Assessment PlanDefine(CustomFieldDefinition definition);
Assessment PlanDelete(CustomFieldRef reference);
```

`ResolvedCustomField` is cache-scoped and may contain a `flid`; it is not serializable canonical
intent.

Hosts decide who may approve schema installation or deletion. The runner reports semantics and
effects, not user permissions.
