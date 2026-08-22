# Initial linked-media boundary

Motif may make the same model deletion a person makes in FieldWorks, even when that removes picture or audio
references owned by the deleted object. Motif does not touch the linked files themselves; creating, replacing,
holding, restoring, or cleaning up those files needs a separate design.

## Scope

This specification covers pictures, audio, video, and every other external file referenced by a FieldWorks
project. It applies to Dry Run, Apply, Receipt, Baseline capture, Proposal portability, and cleanup.

FieldWorks stores the model and external bytes separately:

- `.fwdata` contains LibLCM objects, ownership, and paths or references;
- linked bytes live outside `.fwdata`, commonly under the project's linked-files directories.

Motif's initial operation contract covers only the model side.

## Permitted behaviour

An otherwise supported operation may delete a lexical entry or another model owner. LibLCM may consequently
delete media-reference objects owned by that object. Motif must use the ordinary Runner and LibLCM operation,
so the cascade is the same one FieldWorks performs; it must not predict or reproduce the cascade separately.

Dry Run reports the observed semantic deletion of model objects and references. Apply performs and reads back
the same model change in one LibLCM unit of work. Receipt records the model effects.

The linked file remains on disk even when the last model reference disappears. Motif does not treat an
unreferenced file as garbage in this scope.

## Prohibited behaviour

No initial operation may:

- create or import a linked file;
- replace, rename, move, or edit linked bytes;
- delete or garbage-collect an external file;
- copy linked bytes into a Baseline, Dry Run scratch, Proposal, Assessment, Receipt, archive, or worker cache;
- hold deleted bytes for undo, recovery, transport, or retention;
- promise that applying a Proposal to another project makes its media available.

If an otherwise supported LibLCM operation is shown to mutate external bytes as a side effect, Motif must
refuse that operation family until the future media contract covers the behaviour.

## Baseline and performance consequence

A Baseline contains the saved `.fwdata`, writing-system store, and only small project configuration proven
necessary for equivalent LibLCM behaviour. It excludes linked media. Therefore a half-gigabyte audio or
picture collection does not increase Baseline size or the cost of twenty Dry Runs, except for the small model
references already present in `.fwdata`.

## Operation-family admission gate

Before an operation family is admitted, its real-project conformance fixture must establish one of these:

1. the operation never reaches a model member that owns or references external media; or
2. its only media-related consequence is ordinary LibLCM deletion of model-owned references, and sentinel
   linked bytes remain byte-for-byte present after Dry Run and Apply.

The coverage inventory must classify the relevant LibLCM members. A family that can author a media reference
or cause external-byte mutation is out of scope even if the generated schema could represent it.

## Deferred media design

A later specification must settle all of the following before media authoring is enabled:

- staging and recoverable filesystem application;
- held-file storage and retention after deletion or replacement;
- content identity, collisions, deduplication, and path policy;
- atomicity across LibLCM's UOW and nontransactional filesystem writes;
- backup, restore, export, and Proposal portability;
- Receipt representation and reconciliation after partial failure;
- archive and privacy policy for large or sensitive media.

No current plan authorizes that work.
