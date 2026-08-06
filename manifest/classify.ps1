# manifest/classify.ps1
#
# Adds the classification columns to manifest/liblcm-inventory.tsv, turning it into the type
# system the kind generator reads. See docs/api-surface-layer1.md, docs/api-surface-hc.md,
# docs/change-set-contract.md, docs/hc-grammar-map.md, docs/issues.md (B7, D1, D2, E3) for the
# decisions this script mechanizes. Re-run after any inventory regeneration; do not hand-edit
# the output TSV.
#
# New columns appended, blank/"n/a" for Scope != in:
#   Construct, Group, Classification, ComparisonClass, Verbs, HcReachable,
#   EnumValues, Rationale

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$inventoryPath = Join-Path $PSScriptRoot 'liblcm-inventory.tsv'
$surfacePath   = Join-Path $PSScriptRoot 'hcloader-surface.tsv'

$rows    = Import-Csv -Path $inventoryPath -Delimiter "`t"
$surface = Import-Csv -Path $surfacePath -Delimiter "`t"

# ---------------------------------------------------------------------------
# 1. Per-class default Construct + Group.
#    Used directly for `basic` and `rel` fields (declaring class owns the kind).
#    Used as the REDIRECT TARGET for `owning` fields: an owning field's Construct
#    is the owned child's construct, because `create` targets what is created
#    (naming rule: "create/delete drop Noun when the construct implies one
#    owning field" -- api-surface-layer1.md#naming).
#
#    The CmPossibility entry is the deliberate exception: its value IS the
#    multi-construct fanout string, because CmPossibility's own fields (Name,
#    Abbreviation, ...) are reached through every named subclass that inherits
#    them, per the inherited-field policy proof case (api-surface-layer1.md
#    "Proof that this is required, not stylistic").
# ---------------------------------------------------------------------------

$possibilityFanout = 'possibility|partOfSpeech|lexRefType|lexEntryType|lexEntryInflType|morphType|phonRuleFeat'

$ClassConstruct = @{
    # lists / possibility family
    'CmPossibility'      = $possibilityFanout
    'CmPossibilityList'  = 'possibilityList'
    'CmMedia'            = 'media'
    'CmPicture'          = 'picture'
    'CmTranslation'      = 'translation'
    'CmResource'         = 'resource'
    'StText'             = 'richText'
    'StPara'             = 'richText'
    'TextTag'            = 'textTag'
    'WfiWordSet'         = 'testSet'

    # project / system
    'LangProject'        = 'project'

    # feature system (grammar)
    'FsFeatDefn'         = 'featureDefn'
    'FsClosedFeature'    = 'featureDefn'
    'FsComplexFeature'   = 'featureDefn'
    'FsOpenFeature'      = 'featureDefn'
    'FsFeatureSpecification' = 'featureValue'
    'FsClosedValue'      = 'featureValue'
    'FsComplexValue'     = 'featureValue'
    'FsDisjunctiveValue' = 'featureValue'
    'FsNegatedValue'     = 'featureValue'
    'FsOpenValue'        = 'featureValue'
    'FsSharedValue'      = 'featureValue'
    'FsFeatStruc'        = 'featureStructure'
    'FsFeatStrucDisj'    = 'featureStructure'
    'FsAbstractStructure'= 'featureStructure'
    'FsFeatStrucType'    = 'featureStrucType'
    'FsFeatureSystem'    = 'featureSystem'
    'FsSymFeatVal'       = 'symbolicFeatureValue'

    # lexicon
    'LexDb'              = 'lexicon'
    'LexEntry'            = 'lexEntry'
    'LexEntryRef'         = 'lexEntryRef'
    'LexEntryType'        = 'lexEntryType'
    'LexEntryInflType'    = 'lexEntryInflType'
    'LexRefType'          = 'lexRefType'
    'LexReference'        = 'lexReference'
    'LexSense'            = 'lexSense'
    'LexEtymology'        = 'lexEtymology'
    'LexExampleSentence'  = 'lexExampleSentence'
    'LexExtendedNote'     = 'lexExtendedNote'
    'LexPronunciation'    = 'lexPronunciation'
    'LexAppendix'         = 'lexAppendix'
    'ReversalIndex'       = 'reversalIndex'
    'ReversalIndexEntry'  = 'reversalIndexEntry'

    # lexical forms (rooted at LexEntry, per api-surface-layer1's domain roots)
    'MoForm'              = 'allomorph'
    'MoAffixForm'         = 'allomorph'
    'MoStemAllomorph'     = 'allomorph'
    'MoAffixAllomorph'    = 'allomorph'
    'MoMorphType'         = 'morphType'

    # morphological rules (grammar)
    'MoAffixProcess'      = 'affixProcessRule'
    'MoRuleMapping'       = 'addOutputStep'
    'MoCopyFromInput'     = 'addOutputStep'
    'MoInsertNC'          = 'addOutputStep'
    'MoInsertPhones'      = 'addOutputStep'
    'MoModifyFromInput'   = 'addOutputStep'
    'MoReferralRule'      = 'referralRule'
    'MoStemName'          = 'stemName'
    'MoStratum'           = 'stratum'
    'MoInflClass'         = 'inflectionClass'
    'MoInflAffixSlot'     = 'affixTemplate'
    'MoInflAffixTemplate' = 'affixTemplate'
    'MoAdhocProhib'       = 'coOccurrenceRule'
    'MoAdhocProhibGr'     = 'coOccurrenceRule'
    'MoAlloAdhocProhib'   = 'coOccurrenceRule'
    'MoMorphAdhocProhib'  = 'coOccurrenceRule'
    'MoCompoundRule'      = 'compoundRule'
    'MoBinaryCompoundRule'= 'compoundRule'
    'MoEndoCompound'      = 'compoundRule'
    'MoExoCompound'       = 'compoundRule'
    'MoMorphData'         = 'parserParameters'
    'MoGlossItem'         = 'glossItem'
    'MoGlossSystem'       = 'glossItem'

    # MSAs (grammar - "MSA subtype is the primary grammatical assertion")
    'MoMorphSynAnalysis'  = 'msa'
    'MoStemMsa'           = 'msa'
    'MoDerivAffMsa'       = 'msa'
    'MoInflAffMsa'        = 'msa'
    'MoUnclassifiedAffixMsa' = 'msa'

    # part of speech (grammar, "stands alone")
    'PartOfSpeech'        = 'partOfSpeech'

    # phonology (grammar)
    'PhPhonData'          = 'phonologicalData'
    'PhPhoneme'           = 'phoneme'
    'PhTerminalUnit'      = 'phoneme'
    'PhPhonemeSet'        = 'phonemeSet'
    'PhCode'              = 'phoneme'
    'PhNaturalClass'      = 'naturalClass'
    'PhNCSegments'        = 'naturalClass'
    'PhNCFeatures'        = 'naturalClass'
    'PhEnvironment'       = 'environment'
    'PhFeatureConstraint' = 'ruleContext'
    'PhContextOrVar'      = 'ruleContext'
    'PhPhonContext'       = 'ruleContext'
    'PhSequenceContext'   = 'ruleContext'
    'PhIterationContext'  = 'ruleContext'
    'PhSimpleContext'     = 'ruleContext'
    'PhSimpleContextBdry' = 'ruleContext'
    'PhSimpleContextNC'   = 'ruleContext'
    'PhSimpleContextSeg'  = 'ruleContext'
    'PhSegmentRule'       = 'rewriteRule'
    'PhRegularRule'       = 'rewriteRule'
    'PhMetathesisRule'    = 'metathesisRule'
    'PhSegRuleRHS'        = 'rewriteRule'
    'PhPhonRuleFeat'      = 'phonRuleFeat'
}

$ClassGroup = @{
    'CmPossibility'='lists'; 'CmPossibilityList'='lists'; 'CmMedia'='lexical'; 'CmPicture'='lexical'
    'CmTranslation'='lexical'; 'CmResource'='system'; 'StText'='lists'; 'StPara'='lists'
    'TextTag'='system'; 'WfiWordSet'='grammar'
    'LangProject'='system'
    'FsFeatDefn'='grammar'; 'FsClosedFeature'='grammar'; 'FsComplexFeature'='grammar'; 'FsOpenFeature'='grammar'
    'FsFeatureSpecification'='grammar'; 'FsClosedValue'='grammar'; 'FsComplexValue'='grammar'
    'FsDisjunctiveValue'='grammar'; 'FsNegatedValue'='grammar'; 'FsOpenValue'='grammar'; 'FsSharedValue'='grammar'
    'FsFeatStruc'='grammar'; 'FsFeatStrucDisj'='grammar'; 'FsAbstractStructure'='grammar'
    'FsFeatStrucType'='grammar'; 'FsFeatureSystem'='grammar'; 'FsSymFeatVal'='grammar'
    'LexDb'='lexical'; 'LexEntry'='lexical'; 'LexEntryRef'='lexical'; 'LexEntryType'='lexical'
    'LexEntryInflType'='lexical'; 'LexRefType'='lexical'; 'LexReference'='lexical'; 'LexSense'='lexical'
    'LexEtymology'='lexical'; 'LexExampleSentence'='lexical'; 'LexExtendedNote'='lexical'
    'LexPronunciation'='lexical'; 'LexAppendix'='lexical'; 'ReversalIndex'='lexical'; 'ReversalIndexEntry'='lexical'
    'MoForm'='lexical'; 'MoAffixForm'='lexical'; 'MoStemAllomorph'='lexical'; 'MoAffixAllomorph'='lexical'
    'MoMorphType'='lexical'
    'MoAffixProcess'='grammar'; 'MoRuleMapping'='grammar'; 'MoCopyFromInput'='grammar'; 'MoInsertNC'='grammar'
    'MoInsertPhones'='grammar'; 'MoModifyFromInput'='grammar'; 'MoReferralRule'='grammar'; 'MoStemName'='grammar'
    'MoStratum'='grammar'
    'MoInflClass'='grammar'; 'MoInflAffixSlot'='grammar'; 'MoInflAffixTemplate'='grammar'
    'MoAdhocProhib'='grammar'; 'MoAdhocProhibGr'='grammar'; 'MoAlloAdhocProhib'='grammar'; 'MoMorphAdhocProhib'='grammar'
    'MoCompoundRule'='grammar'; 'MoBinaryCompoundRule'='grammar'; 'MoEndoCompound'='grammar'; 'MoExoCompound'='grammar'
    'MoMorphData'='grammar'; 'MoGlossItem'='grammar'; 'MoGlossSystem'='grammar'
    'MoMorphSynAnalysis'='grammar'; 'MoStemMsa'='grammar'; 'MoDerivAffMsa'='grammar'; 'MoInflAffMsa'='grammar'
    'MoUnclassifiedAffixMsa'='grammar'
    'PartOfSpeech'='grammar'
    'PhPhonData'='grammar'; 'PhPhoneme'='grammar'; 'PhTerminalUnit'='grammar'; 'PhPhonemeSet'='grammar'
    'PhCode'='grammar'; 'PhNaturalClass'='grammar'; 'PhNCSegments'='grammar'; 'PhNCFeatures'='grammar'
    'PhEnvironment'='grammar'; 'PhFeatureConstraint'='grammar'; 'PhContextOrVar'='grammar'; 'PhPhonContext'='grammar'
    'PhSequenceContext'='grammar'; 'PhIterationContext'='grammar'; 'PhSimpleContext'='grammar'
    'PhSimpleContextBdry'='grammar'; 'PhSimpleContextNC'='grammar'; 'PhSimpleContextSeg'='grammar'
    'PhSegmentRule'='grammar'; 'PhRegularRule'='grammar'; 'PhMetathesisRule'='grammar'; 'PhSegRuleRHS'='grammar'
    'PhPhonRuleFeat'='grammar'
}

# Constructs that are polymorphic/generic containers: when an `owning` field redirects to one of
# these, the row's Group falls back to the DECLARING class's group instead of the (groupless)
# container's, because these constructs have no intrinsic domain.
$genericContainerConstructs = @('richText','featureStructure','possibilityList',$possibilityFanout)

# Per-(Class,Field) Construct overrides: fields whose owned/relation target is a specific named
# CmPossibility/CmPossibilityList subclass, where the class-level default (generic
# possibility/possibilityList) would be wrong. Keyed "Class|Field".
$FieldConstructOverride = @{
    'LangProject|PartsOfSpeech'    = 'partOfSpeech'
    'ReversalIndex|PartsOfSpeech'  = 'partOfSpeech'
    'LexDb|MorphTypes'            = 'morphType'
    'LexDb|ComplexEntryTypes'     = 'lexEntryType'
    'LexDb|VariantEntryTypes'     = 'lexEntryType'
    'PhPhonData|PhonRuleFeats'    = 'phonRuleFeat'
}

# ---------------------------------------------------------------------------
# 2. Classification overrides (Class,Field) -> Classification, with Rationale.
#    Everything not listed here gets a rule-based default (section 4).
# ---------------------------------------------------------------------------

function K($c,$f) { return "$c|$f" }

$ClassificationOverride = @{}
$RationaleOverride = @{}

function Set-Override($class, $field, $classification, $rationale) {
    $key = K $class $field
    $ClassificationOverride[$key] = $classification
    $RationaleOverride[$key] = $rationale
}

# --- Engine-computed (derived-read-only) ---
Set-Override 'LexEntry' 'HomographNumber' 'derived-read-only' 'Engine-computed by UpdateHomographs; not authored directly (api-surface-layer1.md "Generates no kind").'
Set-Override 'MoMorphSynAnalysis' 'GlossString' 'derived-read-only' 'Engine-computed gloss string, listed in the "generates no kind" set.'
Set-Override 'LexEntry' 'MainEntriesOrSenses' 'derived-read-only' 'Engine-computed relation, listed in the "generates no kind" set.'
Set-Override 'FsFeatureSpecification' 'RefNumber' 'derived-read-only' 'Engine-assigned reference number, listed in the "generates no kind" set.'
Set-Override 'FsFeatureSpecification' 'ValueState' 'derived-read-only' 'Engine-assigned value-state flag, listed in the "generates no kind" set.'
foreach ($r in $rows) {
    if ($r.Scope -eq 'in' -and ($r.Field -eq 'DateCreated' -or $r.Field -eq 'DateModified')) {
        Set-Override $r.Class $r.Field 'derived-read-only' 'Engine-stamped timestamp, listed in the "generates no kind" set.'
    }
}

# --- Import residue / internal ---
foreach ($r in $rows) {
    if ($r.Scope -eq 'in' -and ($r.Field -eq 'LiftResidue' -or $r.Field -eq 'ImportResidue')) {
        Set-Override $r.Class $r.Field 'internal' 'Import-tool residue field, listed in the "generates no kind" set; never authored.'
    }
}

# --- Runner-bookkeeping (the applied-log reuse of CmResource) ---
Set-Override 'CmResource' 'Name' 'runner-bookkeeping' 'CmResource is LexDb.Resources'' applied-log slot (README "false negative ... the applied-log"); its fields record tool/model version, not authored content.'
Set-Override 'CmResource' 'Version' 'runner-bookkeeping' 'CmResource is LexDb.Resources'' applied-log slot (README "false negative ... the applied-log"); its fields record tool/model version, not authored content.'
Set-Override 'LexDb' 'Resources' 'runner-bookkeeping' 'Owning collection of CmResource, the applied-log slot; not author-created content.'

# --- Domain-root singletons: pre-existing, never created/deleted by an author ---
foreach ($f in @('LexDb','MorphologicalData','MsFeatureSystem','PhFeatureSystem','PhonologicalData')) {
    Set-Override 'LangProject' $f 'internal' 'One-per-project singleton root (api-surface-layer1.md domain roots); always exists, never created or deleted by an authored operation.'
}

# --- Observable-not-authorable: engine-maintained reverse indices ---
Set-Override 'LexDb' 'AllomorphIndex' 'observable-not-authorable' 'Engine-maintained reverse index over allomorph forms; read for lookup, never directly added/removed by an author.'
Set-Override 'LexDb' 'LexicalFormIndex' 'observable-not-authorable' 'Engine-maintained reverse index over lexical forms; read for lookup, never directly added/removed by an author.'

# --- Unsupported: explicit "do not offer as a control" / scoped-out surfaces ---
Set-Override 'StPara' 'StyleRules' 'unsupported' 'TextPropBinary style blobs are scoped out of v1 as configuration, not authorable linguistic intent (ADR 0009 #2).'
Set-Override 'PhEnvironment' 'AMPLEStringSegment' 'unsupported' 'Never read by HCLoader; a near-miss sibling of StringRepresentation on the same class, explicitly listed in the "generates no kind" set.'
Set-Override 'PhEnvironment' 'LeftContext' 'unsupported' 'Collapse-criterion amendment 2: via PhEnvironment the context graph is dead (HCLoader reads only StringRepresentation); the environment construct is string-authored only.'
Set-Override 'PhEnvironment' 'RightContext' 'unsupported' 'Collapse-criterion amendment 2: via PhEnvironment the context graph is dead (HCLoader reads only StringRepresentation); the environment construct is string-authored only.'
Set-Override 'MoStemName' 'DefaultAffix' 'unsupported' 'Only Regions drives suppletion; a fallback form is inexpressible and documented as unreachable (issue C10).'
Set-Override 'MoStemName' 'DefaultStem' 'unsupported' 'Only Regions drives suppletion; a fallback form is inexpressible and documented as unreachable (issue C10).'
Set-Override 'MoMorphData' 'TestSets' 'unsupported' 'Explicitly listed in api-surface-layer1.md "Generates no kind" (MoMorphData.TestSets/AnalyzingAgents).'
Set-Override 'MoMorphData' 'AnalyzingAgents' 'unsupported' 'Explicitly listed in api-surface-layer1.md "Generates no kind" (MoMorphData.TestSets/AnalyzingAgents).'
foreach ($r in $rows) {
    if ($r.Scope -eq 'in' -and $r.Class -eq 'WfiWordSet') {
        Set-Override $r.Class $r.Field 'unsupported' 'Reachable only via MoMorphData.TestSets, itself unsupported (generates no kind); no other owning edge into WfiWordSet exists.'
    }
}
# MoGlossItem's whole gloss-abbreviation subsystem: never consulted (issue C11) -- "don't offer the dead path"
foreach ($r in $rows) {
    if ($r.Scope -eq 'in' -and ($r.Class -eq 'MoGlossItem' -or $r.Class -eq 'MoGlossSystem')) {
        Set-Override $r.Class $r.Field 'unsupported' 'MoGlossItem''s entire gloss-abbreviation system is never consulted -- HCLoader gets its gloss from LexSense.Gloss instead (issue C11: "don''t offer the dead path").'
    }
}
# Stratum REFERENCES (not MoStratum itself): "never read -- do not offer as controls" (hc-grammar-map.md), issue C9
Set-Override 'MoCompoundRule' 'Stratum' 'unsupported' 'Stratum reference, silently ignored by HCLoader; hc-grammar-map.md "Never read -- do not offer these as controls."'
Set-Override 'MoDerivAffMsa' 'Stratum' 'unsupported' 'Stratum reference, silently ignored by HCLoader; hc-grammar-map.md "Never read -- do not offer these as controls."'
Set-Override 'MoStemMsa' 'Stratum' 'unsupported' 'Stratum reference, silently ignored by HCLoader; hc-grammar-map.md "Never read -- do not offer these as controls."'
Set-Override 'MoInflAffixTemplate' 'Stratum' 'unsupported' 'Stratum reference, silently ignored by HCLoader; hc-grammar-map.md "Never read -- do not offer these as controls."'
Set-Override 'PhSegmentRule' 'InitialStratum' 'unsupported' 'Model comment promises per-rule stratum scoping but HCLoader silently ignores it (issue C9); must not be offered as a control.'
Set-Override 'PhSegmentRule' 'FinalStratum' 'unsupported' 'Model comment promises per-rule stratum scoping but HCLoader silently ignores it (issue C9); must not be offered as a control.'
Set-Override 'MoStratum' 'Phonemes' 'unsupported' 'Explicitly listed in api-surface-layer1.md "Generates no kind" (HCLoader-inert per HC grammar map), unlike MoStratum.Name which is kept for referential integrity.'
# TextTag / interlinear tag surface reached transitively through StText.Tags: flagged as a scope
# over-inclusion in the report rather than silently re-scoped here.
foreach ($r in $rows) {
    if ($r.Scope -eq 'in' -and $r.Class -eq 'TextTag') {
        Set-Override $r.Class $r.Field 'unsupported' 'Interlinear text-markup surface (BeginSegment/EndSegment target the out-of-scope Segment class); reached only via StText.Tags, a false-negative container pulled in for CmPossibility.Discussion-style rich text. No realistic Motif authoring path -- flagged as a probable scope over-inclusion, see report.'
    }
}

# ---------------------------------------------------------------------------
# 3. ComparisonClass overrides (Class,Field) -> value
# ---------------------------------------------------------------------------
$ComparisonClassOverride = @{
    (K 'PhPhonData' 'PhonRules')        = 'feeding'
    (K 'LexEntry' 'AlternateForms')     = 'feeding'
    (K 'PhRegularRule' 'StrucDesc')     = 'index-as-identity'
    (K 'PhSegRuleRHS' 'StrucChange')    = 'index-as-identity'
    (K 'PhSegRuleRHS' 'LeftContext')    = 'index-as-identity'
    (K 'PhSegRuleRHS' 'RightContext')   = 'index-as-identity'
    # Correction E3: FeatConstraints is explicitly NOT index-as-identity; a move on the pool
    # is inert because the alpha-variable traversal never walks the pool itself.
    (K 'PhPhonData' 'FeatConstraints')  = 'unordered'
    (K 'PhPhonData' 'Contexts')         = 'unordered'
}
$ComparisonClassRationale = @{
    (K 'PhPhonData' 'PhonRules')        = 'Override list: a neighbour rule''s content edit changes what this rule produces (feeding/bleeding).'
    (K 'LexEntry' 'AlternateForms')     = 'Override list: allomorph order encodes disjunctive elsewhere-blocking.'
    (K 'PhRegularRule' 'StrucDesc')     = 'Correction E3: alpha-variable names are assigned by first-appearance scan of this traversal; index is identity.'
    (K 'PhSegRuleRHS' 'StrucChange')    = 'Correction E3: part of the per-rule alpha-variable traversal; index is identity.'
    (K 'PhSegRuleRHS' 'LeftContext')    = 'Correction E3: part of the per-rule alpha-variable traversal; index is identity.'
    (K 'PhSegRuleRHS' 'RightContext')   = 'Correction E3: part of the per-rule alpha-variable traversal; index is identity.'
    (K 'PhPhonData' 'FeatConstraints')  = 'Correction E3: index-as-identity was mislocated here; a move on this pool is semantically inert (pooled-but-private storage, not the traversal HCLoader walks).'
    (K 'PhPhonData' 'Contexts')         = 'Pooled-but-private storage (change-set-contract.md): members are addressed by reference from Input/StrucDesc, never by pool position.'
}

# ---------------------------------------------------------------------------
# 4. HcReachable per-class overrides (correcting the name-only join), keyed Class|Field.
# ---------------------------------------------------------------------------
$HcReachableOverride = @{
    # E2 correction: only Prefix/SuffixSlots are read; the extractor's bare-name match on
    # "Slots" over-attributes MoInflAffixTemplate (the template-level union field is never read;
    # LexEntryInflType/MoInflAffMsa/MoStemMsa.Slots -- "which slot this affix realizes" -- are).
    (K 'MoInflAffixTemplate' 'Slots')         = 'no'
    (K 'MoInflAffixTemplate' 'ProcliticSlots')= 'no'
    (K 'MoInflAffixTemplate' 'EncliticSlots') = 'no'
    (K 'MoInflAffixTemplate' 'PrefixSlots')   = 'yes'
    (K 'MoInflAffixTemplate' 'SuffixSlots')   = 'yes'
}
$HcReachableOverrideNote = @{
    (K 'MoInflAffixTemplate' 'Slots')          = 'api-surface-hc.md E2 correction: exhaustive grep finds zero references to Slots/ProcliticSlots/EncliticSlots; only Prefix/SuffixSlots are read.'
    (K 'MoInflAffixTemplate' 'ProcliticSlots') = 'api-surface-hc.md E2 correction: exhaustive grep finds zero references to Slots/ProcliticSlots/EncliticSlots.'
    (K 'MoInflAffixTemplate' 'EncliticSlots')  = 'api-surface-hc.md E2 correction: exhaustive grep finds zero references to Slots/ProcliticSlots/EncliticSlots.'
    (K 'MoInflAffixTemplate' 'PrefixSlots')    = 'api-surface-hc.md E2 correction: confirmed read, collapsed as SuffixSlotsRS.Concat(PrefixSlotsRS.Reverse()).'
    (K 'MoInflAffixTemplate' 'SuffixSlots')    = 'api-surface-hc.md E2 correction: confirmed read, collapsed as SuffixSlotsRS.Concat(PrefixSlotsRS.Reverse()).'
}
# Gap #5 (api-surface-layer1.md): needs HCLoader-read confirmation before shipping kinds.
$unconfirmedClasses = @('FsDisjunctiveValue','FsNegatedValue','FsSharedValue','MoAdhocProhibGr')
$unconfirmedFieldKeys = @( (K 'FsFeatStruc' 'FeatureDisjunctions') )

# ---------------------------------------------------------------------------
# 5. (retired) AssessPoisonsCache -- this section listed the four fields whose
#    mutation left a derived LibLCM cache stale that Rollback could not repair,
#    verified against OverridesLing_Lex.cs / OverridesLing_MoClasses.cs /
#    RepositoryAdditions.cs. Deleted 2026-08-06 with the column itself: no Dry Run
#    rolls back any more, so nothing needs to know which fields would have suffered
#    if one did. See docs/adr/0016-scratch-cache-copy-not-undo.md. This mattered
#    because the list was hand-maintained and would have had to grow with every
#    field added to the catalog, forever.
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# 6. EnumValues for the 30 in-scope Integer fields (issue B7).
#    Sourced from MasterLCModel.xml comments and liblcm/FieldWorks enums; see report for citations.
# ---------------------------------------------------------------------------
$EnumValues = @{
    (K 'CmPicture' 'LayoutPos')                 = '0=CenterInColumn;1=CenterOnPage;2=RightAlignInColumn;3=LeftAlignInColumn;4=FillColumnWidth;5=FillPageWidth;6=FullPage'
    (K 'CmPicture' 'LocationRangeType')          = '0=AfterAnchor;1=ReferenceRange;2=ParagraphRange'
    (K 'CmPossibilityList' 'DisplayOption')      = '0=kpntName;1=kpntNameAndAbbrev;2=kpntAbbreviation'
    (K 'CmPossibilityList' 'WsSelector')         = '-1=kwsAnal;-2=kwsVern;-3=kwsAnals;-4=kwsVerns;-5=kwsAnalVerns;-6=kwsVernAnals;-7=kwsFirstAnal;-8=kwsFirstVern;-9=kwsFirstAnalOrVern;-10=kwsFirstVernOrAnal;-11=kwsPronunciation;-12=kwsFirstPronunciation;-13=kwsPronunciations;-14=kwsReversalIndex;-15=kwsAllReversalIndex;-16=kwsVernInParagraph;-17=kwsFirstVernOrNamed'
    (K 'FsOpenFeature' 'WsSelector')             = '-1=kwsAnal;-2=kwsVern;-3=kwsAnals;-4=kwsVerns;-5=kwsAnalVerns;-6=kwsVernAnals;-7=kwsFirstAnal;-8=kwsFirstVern;-9=kwsFirstAnalOrVern;-10=kwsFirstVernOrAnal;-11=kwsPronunciation;-12=kwsFirstPronunciation;-13=kwsPronunciations;-14=kwsReversalIndex;-15=kwsAllReversalIndex;-16=kwsVernInParagraph;-17=kwsFirstVernOrNamed'
    (K 'LexEntryRef' 'HideMinorEntry')           = '0=ShowMinorEntry;1=HideMinorEntry'
    (K 'LexEntryRef' 'RefType')                  = '0=Variant;1=ComplexForm'
    (K 'LexRefType' 'MappingType')                = '0=kmtSenseCollection;1=kmtSensePair;2=kmtSenseAsymmetricPair;3=kmtSenseTree;4=kmtSenseSequence;5=kmtEntryCollection;6=kmtEntryPair;7=kmtEntryAsymmetricPair;8=kmtEntryTree;9=kmtEntrySequence;10=kmtEntryOrSenseCollection;11=kmtEntryOrSensePair;12=kmtEntryOrSenseAsymmetricPair;13=kmtEntryOrSenseTree;14=kmtEntryOrSenseSequence;15=kmtSenseUnidirectional;16=kmtEntryUnidirectional;17=kmtEntryOrSenseUnidirectional'
    (K 'MoAdhocProhib' 'Adjacency')               = '0=Anywhere;1=SomewhereToLeft;2=SomewhereToRight;3=AdjacentToLeft;4=AdjacentToRight'
    (K 'MoGlossItem' 'Type')                      = '1=fsType;2=feature;3=value;4=complex;5=deriv;6=group;7=xref'
    (K 'PhSegmentRule' 'Direction')                = '0=LeftToRightIterative;1=RightToLeftIterative;2=Simultaneous'
}
$EnumSourceCitation = @{
    (K 'CmPicture' 'LayoutPos')            = 'C# enum PictureLayoutPosition, Enumerations.cs:208-240; MasterLCModel.xml:1270-1274'
    (K 'CmPicture' 'LocationRangeType')    = 'C# enum PictureLocationRangeType, Enumerations.cs:251-267; MasterLCModel.xml:1280-1284'
    (K 'CmPossibilityList' 'DisplayOption')= 'MasterLCModel.xml:399-403 (min=0 max=2, values spelled out in comment)'
    (K 'CmPossibilityList' 'WsSelector')   = 'WritingSystemServices.cs:39-76 (authoritative; supersedes stale 7-value MasterLCModel.xml:415-428 comment); non-negative values are an open HVO reference range, not part of the closed set'
    (K 'FsOpenFeature' 'WsSelector')       = 'MasterLCModel.xml:1439-1443 defers to CmPossibilityList_WsSelector; see WritingSystemServices.cs:39-76'
    (K 'LexEntryRef' 'HideMinorEntry')     = 'MasterLCModel.xml:5013-5017; OverridesLing_Lex.cs:3125 (`ler.HideMinorEntry = value ? 0 : 1`) -- only 0/1 ever read/written despite doc framing a future bitmask'
    (K 'LexEntryRef' 'RefType')            = 'ConstantAdditions.cs:227-237 (LexEntryRefTags.krtVariant=0, krtComplexForm=1); MasterLCModel.xml:5024-5028'
    (K 'LexRefType' 'MappingType')          = 'enum MappingTypes, Enumerations.cs:32-80 (18 values, 0-17); MasterLCModel.xml:4821-4835 doc is stale (only documents 0-8)'
    (K 'MoAdhocProhib' 'Adjacency')         = 'MasterLCModel.xml:2973-2980 names the 5 possibilities; numeric mapping via GrammarJsonServices.cs:1053-1069 AdjacencyName switch ("Mirrors HCLoader.GetAdjacency")'
    (K 'MoGlossItem' 'Type')                = 'MasterLCModel.xml:4649-4660'
    (K 'PhSegmentRule' 'Direction')         = 'MasterLCModel.xml:5057-5065 (explicit numbered list in comment)'
}
$MagnitudeReason = @{
    (K 'CmPicture' 'LocationMax')             = 'Holds a scripture ref or paragraph-count offset depending on LocationRangeType; no fixed domain (MasterLCModel.xml:1290-1294).'
    (K 'CmPicture' 'LocationMin')             = 'Same as LocationMax, lower bound (MasterLCModel.xml:1285-1289).'
    (K 'CmPicture' 'ScaleFactor')             = 'Arbitrary grow/shrink percentage (MasterLCModel.xml:1275-1279).'
    (K 'CmPossibility' 'BackColor')           = 'RGB overlay color value (MasterLCModel.xml:359-363).'
    (K 'CmPossibility' 'ForeColor')           = 'RGB overlay color value (MasterLCModel.xml:354-358).'
    (K 'CmPossibility' 'SortSpec')            = 'Plain sort key, no named domain (MasterLCModel.xml:321; sample data uses arbitrary values).'
    (K 'CmPossibility' 'UnderColor')          = 'RGB overlay underline color (MasterLCModel.xml:364-368).'
    (K 'CmPossibilityList' 'Depth')           = 'Nesting-depth bound of the list tree, min=0 max=127 (MasterLCModel.xml:389).'
    (K 'CmPossibilityList' 'ItemClsid')       = 'Holds the CmObject clsid of the list''s legal item class -- a class-ID reference, not a fixed enum (MasterLCModel.xml:404-408).'
    (K 'CmPossibilityList' 'PreventChoiceAboveLevel') = 'Tree-level number, min=0 max=8 (MasterLCModel.xml:390).'
    (K 'FsFeatureSpecification' 'RefNumber')  = 'Bit-width bound (28 bits), not named values (MasterLCModel.xml:1384).'
    (K 'LexEntry' 'HomographNumber')          = 'Computed homograph counter, min=0 max=255 (MasterLCModel.xml:2578).'
    (K 'MoMorphType' 'SecondaryOrder')        = 'Plain sort-order number disambiguating homographic forms of different morph types (MasterLCModel.xml:3615-3619).'
    (K 'PhIterationContext' 'Maximum')        = 'Iteration repeat upper bound, defaults to infinite (MasterLCModel.xml:4234-4238).'
    (K 'PhIterationContext' 'Minimum')        = 'Iteration repeat lower bound, defaults to zero (MasterLCModel.xml:4229-4233).'
    (K 'TextTag' 'BeginAnalysisIndex')        = 'Index into Segment.Analyses sequence (MasterLCModel.xml:659-663).'
    (K 'TextTag' 'EndAnalysisIndex')          = 'Index into Segment.Analyses sequence (MasterLCModel.xml:664-668).'
}
$UnknownReason = @{
    (K 'CmPossibility' 'UnderStyle') = 'min=0 max=127, doc says "style of underline used in overlays"; likely mirrors FwUnderlineType (Kernel.cs:3100-3133) but no code anywhere in liblcm or FieldWorks/Src casts/binds this field to that or any other named constant; overlay feature appears dead. Sourced but unresolved -- marked unknown per instructions not to invent values.'
    (K 'FsFeatureSpecification' 'ValueState') = 'min=0 max=15 (4-bit range); name strongly suggests a closed enum but no enum/constant class/switch anywhere in liblcm or FieldWorks/Src interprets specific values (only generated getter/setter boilerplate). The FsFeatValueState type does not exist in the codebase. Sourced but unresolved -- marked unknown.'
}

# ---------------------------------------------------------------------------
# 7. Verbs by (Kind, Card, Sig) shape.
# ---------------------------------------------------------------------------
function Get-Verbs($kind, $card, $sig) {
    if ($kind -eq 'basic') { return 'set|clear' }
    if ($kind -eq 'owning') {
        switch ($card) {
            'atomic' { return 'create|delete' }   # create-into-occupied w/ implicit detach
            'col'    { return 'create|delete' }
            'seq'    { return 'create|delete|move|reparent' }
        }
    }
    if ($kind -eq 'rel') {
        switch ($card) {
            'atomic' { return 'set|clear' }
            'col'    { return 'addRef|removeRef' }
            'seq'    { return 'addRef|removeRef|move' }
        }
    }
    return 'n/a'
}

# ---------------------------------------------------------------------------
# 8. Supporting-detail field-name heuristic (applied when no explicit override matched).
# ---------------------------------------------------------------------------
$supportingDetailExact = @(
    'Description','Comment','Bibliography','HelpId','HelpFile','PrecComment','LanguageNotes',
    'CatalogSourceId','BackColor','ForeColor','UnderColor','UnderStyle','LayoutPos','ScaleFactor',
    'LocationMax','LocationMin','LocationRangeType','SortSpec','Depth','DisplayOption','ItemClsid',
    'PreventChoiceAboveLevel','IsSorted','IsClosed','IsVernacular','PreventDuplicates',
    'PreventNodeChoices','UseExtendedFields','ListVersion','ReverseAbbr','ReverseAbbreviation',
    'ReverseName','AfterSeparator','ComplexNameFirst','ComplexNameSeparator','EticID','Discussion',
    'AnalysisWss','CurAnalysisWss','CurPronunWss','CurVernWss','VernWss','DoNotUseForParsing',
    'IsBodyInSeparateSubentry','IsHeadwordCitationForm','WorldRegion','MainCountry',
    'FieldWorkLocation','EthnologueCode','LinkedFilesRootDir','HomographWs','WritingSystem',
    'WsSelector','Restrictions','SummaryDefinition','Exemplar','EncyclopedicInfo','ScientificName',
    'Source','CVPattern','Tone','Reference','HideMinorEntry','Status'
)
$supportingDetailSuffix = @('Note')

function Get-DefaultClassification($field) {
    if ($supportingDetailExact -contains $field) { return 'supporting-detail' }
    foreach ($suf in $supportingDetailSuffix) {
        if ($field.Length -gt $suf.Length -and $field.EndsWith($suf)) { return 'supporting-detail' }
    }
    return 'semantic-operation'
}

# ---------------------------------------------------------------------------
# 9. Build the HCLoader field -> InScopeClasses index from hcloader-surface.tsv.
# ---------------------------------------------------------------------------
$surfaceByField = @{}
foreach ($s in $surface) {
    if (-not $s.Field) { continue }
    $classes = @()
    if ($s.InScopeClasses) { $classes = $s.InScopeClasses -split '\|' }
    if (-not $surfaceByField.ContainsKey($s.Field)) { $surfaceByField[$s.Field] = New-Object System.Collections.Generic.HashSet[string] }
    foreach ($c in $classes) { [void]$surfaceByField[$s.Field].Add($c) }
}

# ---------------------------------------------------------------------------
# 10. Main pass.
# ---------------------------------------------------------------------------
$hcVerdictChanges = 0
$outRows = New-Object System.Collections.Generic.List[object]

foreach ($r in $rows) {
    $out = [ordered]@{
        Class = $r.Class; Base = $r.Base; Abstract = $r.Abstract; Scope = $r.Scope
        ScopeReason = $r.ScopeReason; Field = $r.Field; Kind = $r.Kind; Sig = $r.Sig
        Card = $r.Card; HcReferenced = $r.HcReferenced
    }

    if ($r.Scope -ne 'in') {
        $out.Construct = ''; $out.Group = ''; $out.Classification = ''
        $out.ComparisonClass = ''; $out.Verbs = ''; $out.HcReachable = 'n/a'
        $out.EnumValues = ''; $out.Rationale = ''
        $outRows.Add([pscustomobject]$out)
        continue
    }

    $key = K $r.Class $r.Field

    # --- Construct + Group ---
    $construct = $null
    if ($FieldConstructOverride.ContainsKey($key)) {
        $construct = $FieldConstructOverride[$key]
    } elseif ($r.Kind -eq 'owning' -and $ClassConstruct.ContainsKey($r.Sig)) {
        $construct = $ClassConstruct[$r.Sig]
    } elseif ($ClassConstruct.ContainsKey($r.Class)) {
        $construct = $ClassConstruct[$r.Class]
    } else {
        $construct = $r.Class.Substring(0,1).ToLower() + $r.Class.Substring(1)
    }

    $group = $null
    if ($genericContainerConstructs -contains $construct) {
        $group = $ClassGroup[$r.Class]
    } elseif ($construct -eq $possibilityFanout) {
        $group = $ClassGroup['CmPossibility']
    } else {
        # find owning class of this construct name for group lookup
        $ownerClass = ($ClassConstruct.GetEnumerator() | Where-Object { $_.Value -eq $construct } | Select-Object -First 1).Key
        if ($ownerClass -and $ClassGroup.ContainsKey($ownerClass)) { $group = $ClassGroup[$ownerClass] }
        elseif ($ClassGroup.ContainsKey($r.Class)) { $group = $ClassGroup[$r.Class] }
        else { $group = 'system' }
    }
    if (-not $group) { $group = $ClassGroup[$r.Class] }

    # --- Classification + Rationale ---
    if ($ClassificationOverride.ContainsKey($key)) {
        $classification = $ClassificationOverride[$key]
        $rationale = $RationaleOverride[$key]
    } else {
        $classification = Get-DefaultClassification $r.Field
        if ($classification -eq 'supporting-detail') {
            $rationale = "Secondary descriptive/administrative detail on the $construct construct, not its primary linguistic assertion."
        } else {
            $rationale = "Core authorable content of the $construct construct ($($r.Kind)/$($r.Card) $($r.Sig))."
        }
    }

    # --- ComparisonClass ---
    if ($ComparisonClassOverride.ContainsKey($key)) {
        $compClass = $ComparisonClassOverride[$key]
        $compRationale = $ComparisonClassRationale[$key]
    } else {
        if ($r.Card -eq 'seq') { $compClass = 'positional' } else { $compClass = 'unordered' }
        $compRationale = $null
    }
    if ($r.Class -eq 'MoAffixProcess' -and $r.Field -eq 'Input') {
        $compRationale = 'Positional (pattern-node order is the rule''s match), but Output resolves against it by ContentRA.IndexInOwner+1 -- a discovered footprint on move, not merely declared (change-set-contract.md#declared-vs-discovered-footprint).'
    }

    # --- Verbs ---
    if ($classification -in @('unsupported','derived-read-only','internal','runner-bookkeeping','observable-not-authorable')) {
        $verbs = 'n/a'
    } else {
        $verbs = Get-Verbs $r.Kind $r.Card $r.Sig
    }

    # --- HcReachable ---
    if ($HcReachableOverride.ContainsKey($key)) {
        $hc = $HcReachableOverride[$key]
        $hcNote = $HcReachableOverrideNote[$key]
    } elseif ($unconfirmedClasses -contains $r.Class -or $unconfirmedFieldKeys -contains $key) {
        $hc = 'unconfirmed'
        $hcNote = 'api-surface-layer1.md gap #5: needs HCLoader-read confirmation before shipping kinds.'
    } elseif ($r.HcReferenced -eq 'no') {
        $hc = 'no'
        $hcNote = $null
    } elseif ($r.HcReferenced -eq 'name-referenced') {
        if ($surfaceByField.ContainsKey($r.Field) -and $surfaceByField[$r.Field].Contains($r.Class)) {
            $hc = 'yes'
        } elseif ($surfaceByField.ContainsKey($r.Field)) {
            $hc = 'no'
        } else {
            $hc = 'unconfirmed'
        }
        $hcNote = $null
    } else {
        $hc = 'unconfirmed'
        $hcNote = 'Unexpected HcReferenced value.'
    }
    # Track verdict changes vs name-level column (name-referenced treated as the old "yes-ish")
    $oldWasReferenced = ($r.HcReferenced -eq 'name-referenced')
    $newIsYes = ($hc -eq 'yes')
    if ($oldWasReferenced -ne $newIsYes) { $hcVerdictChanges++ }

    # --- EnumValues (Integer fields only) ---
    $enumVal = ''
    if ($r.Sig -eq 'Integer') {
        if ($EnumValues.ContainsKey($key)) {
            $enumVal = $EnumValues[$key]
        } elseif ($UnknownReason.ContainsKey($key)) {
            $enumVal = 'unknown'
        } else {
            $enumVal = ''
        }
    }

    # --- Assemble Rationale (fold in comparison-class / hc notes for traceability) ---
    $ratParts = New-Object System.Collections.Generic.List[string]
    $ratParts.Add($rationale)
    if ($compRationale) { $ratParts.Add("ComparisonClass: $compRationale") }
    if ($hcNote) { $ratParts.Add("HcReachable: $hcNote") }
    if ($r.Sig -eq 'Integer') {
        if ($EnumValues.ContainsKey($key)) { $ratParts.Add("EnumValues source: $($EnumSourceCitation[$key])") }
        elseif ($MagnitudeReason.ContainsKey($key)) { $ratParts.Add("Magnitude: $($MagnitudeReason[$key])") }
        elseif ($UnknownReason.ContainsKey($key)) { $ratParts.Add("Unknown: $($UnknownReason[$key])") }
    }

    $out.Construct = $construct
    $out.Group = $group
    $out.Classification = $classification
    $out.ComparisonClass = $compClass
    $out.Verbs = $verbs
    $out.HcReachable = $hc
    $out.EnumValues = $enumVal
    $out.Rationale = ($ratParts -join ' | ')

    $outRows.Add([pscustomobject]$out)
}

# ---------------------------------------------------------------------------
# 11. Write output (overwrite the inventory in place) + verification.
# ---------------------------------------------------------------------------
$outRows | Export-Csv -Path $inventoryPath -Delimiter "`t" -NoTypeInformation -Encoding utf8 -UseQuotes Always

Write-Output "Rows written: $($outRows.Count)"
Write-Output "HcReachable verdict changes vs name-level HcReferenced: $hcVerdictChanges"

$inScopeOut = $outRows | Where-Object { $_.Scope -eq 'in' }
$missingConstruct = $inScopeOut | Where-Object { -not $_.Construct }
$missingClassification = $inScopeOut | Where-Object { -not $_.Classification }
$missingComparison = $inScopeOut | Where-Object { -not $_.ComparisonClass }
$missingRationale = $inScopeOut | Where-Object { -not $_.Rationale }
Write-Output "In-scope rows: $($inScopeOut.Count)"
Write-Output "Missing Construct: $($missingConstruct.Count)"
Write-Output "Missing Classification: $($missingClassification.Count)"
Write-Output "Missing ComparisonClass: $($missingComparison.Count)"
Write-Output "Missing Rationale: $($missingRationale.Count)"

$intRows = $inScopeOut | Where-Object { $_.Sig -eq 'Integer' }
$intUndecided = $intRows | Where-Object { -not $_.EnumValues }
Write-Output "In-scope Integer fields: $($intRows.Count); undecided (blank EnumValues): $($intUndecided.Count)"
