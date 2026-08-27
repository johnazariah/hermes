# Hermes — Testing Register

> **Rule**: Update this file whenever tests are added, removed, or modified.

## Summary

| Category | Count |
|----------|-------|
| Unit | 569 |
| Property | 5 |
| Integration | 318 |
| ManualTest | 6 |
| **Total** | **898** |

> 863 unique test methods; 898 total test cases
> (Theory/InlineData tests contribute multiple cases per method)

## Execution Baseline

Current-main baseline captured on 2026-08-27 from `a11686d` source plus
documentation-only rebaseline commits:

| Measure | Result |
|---------|--------|
| .NET discovered | 867 |
| .NET passed / skipped / failed | 857 / 10 / 0 |
| Line / branch coverage | 65.0% / 31.1% |
| Supported .NET builds | Core, Service, Tests, Windows Tray: 0 warnings, 0 errors |
| Playwright source definitions | 21 (9 React, 12 Blazor) |
| Playwright default discovery | 0; no usable runner configuration |
| React locked install | Failed: `package-lock.json` is out of sync |
| React build after no-lock install | Passed |
| React lint | Failed: 4 errors |

### PR #17 PR B validation

The metadata-only reclassification branch adds 31 cases without changing the
current-main skip set: 898 discovered, 888 passed, 10 skipped, and 0 failed.
Core-only coverage is 66.84% line / 31.22% branch, above the 65.0% / 31.1%
Phase 0 baseline. The full solution builds with 0 warnings and 0 errors.

Skip-quality findings:

- 3 email-body FTS tests are skipped because file-first content is not indexed.
- 1 MCP path-safety test is skipped for platform-dependent path behavior.
- 6 Osprey parity tests are skipped because fixtures are unavailable.
- 4 other Osprey parity tests use `Option.iter` and pass without assertions when
  their fixtures are unavailable; only the two PPTX fixtures are source-controlled.

The active follow-on issues are #6, #8, #9, #11, #16, #17, and #18. The V5
Stabilization wave closes only at 85% line and 60% branch coverage with truthful
skip and Playwright execution evidence.

---

## ConfigTests.fs (15 tests)

| Test | Category |
|------|----------|
| Config_ParseYaml_ValidYaml_ReturnsConfig | Unit |
| Config_ParseYaml_EmptyYaml_ReturnsDefaults | Unit |
| Config_ParseYaml_WithAccounts_ParsesAccountList | Unit |
| Config_ParseYaml_WithWatchFolders_ParsesPatterns | Unit |
| Config_Load_MissingFile_ReturnsError | Unit |
| Config_Load_ValidFile_ReturnsConfig | Unit |
| Config_Init_CreatesConfigAndRules | Unit |
| Config_Init_SkipsExistingFiles | Unit |
| Config_ExpandHome_TildePath_ExpandsToUserHome | Unit |
| Config_ExpandHome_AbsolutePath_ReturnsUnchanged | Unit |
| Config_ParseYaml_ChatAzureOpenAI_ParsesProvider | Unit |
| Config_ParseYaml_ChatOllama_ParsesProvider | Unit |
| Config_ParseYaml_NoChatSection_DefaultsToOllama | Unit |
| Config_ChatProviderKind_FromString_ParsesVariants | Unit |
| Config_ParseYaml_NeverThrows | Property |

## DatabaseTests.fs (18 tests)

| Test | Category |
|------|----------|
| Database_InitSchema_CreatesAllTables | Integration |
| Database_InitSchema_SetsSchemaVersion | Integration |
| Database_InitSchema_IsIdempotent | Integration |
| Database_SchemaVersion_BeforeInit_ReturnsZero | Integration |
| Database_TableExists_NonexistentTable_ReturnsFalse | Integration |
| Database_FTS5_InsertTrigger_PopulatesFtsOnInsert | Integration |
| Database_FTS5_SearchByVendor_FindsDocument | Integration |
| Database_InitArchive_CreatesDirectoriesAndDatabase | Integration |
| Database_FromPath_CreatesParentDirectories | Unit |
| Database_InitSchema_CreatesAllIndexes | Integration |
| Database_InitSchema_V3_CreatesRemindersTable | Integration |
| Database_InitSchema_V3_SyncStateHasBackfillColumns | Integration |
| Database_InitSchema_V3_SchemaVersionIs3 | Integration |
| Database_InitSchema_V3_IdempotentRunTwice | Integration |
| Database_InitSchema_V3_ReminderIndexesExist | Integration |
| Database_SchemaVersion_FreshDb_ReturnsLatest | Integration |
| Database_InitSchema_Idempotent_CanRunTwice | Integration |
| Database_FreshSchema_HasAllTables | Integration |

## RulesTests.fs (32 tests)

| Test | Category |
|------|----------|
| Rules_ParseYaml_ValidRules_ParsesCorrectly | Unit |
| Rules_ParseYaml_EmptyYaml_ReturnsEmptyRules | Unit |
| Rules_ParseYaml_CustomDefaultCategory_Respected | Unit |
| Rules_Classify_DomainMatch_TakesPriority | Unit |
| Rules_Classify_FilenameMatch_WhenNoDomainMatch | Unit |
| Rules_Classify_SubjectMatch_WhenNoFilenameMatch | Unit |
| Rules_Classify_DefaultRule_WhenNoMatch | Unit |
| Rules_Classify_NoSidecar_MatchesFilenameOnly | Unit |
| Rules_Classify_NoSidecar_NoFilenameMatch_DefaultsToUnsorted | Unit |
| Rules_Classify_CaseInsensitive_FilenameMatch | Unit |
| Rules_Classify_CaseInsensitive_SubjectMatch | Unit |
| Rules_Cascade_DomainBeatsFilename | Unit |
| Rules_Cascade_FilenameBeatsSubject | Unit |
| Rules_Cascade_SubjectBeatsDefault | Unit |
| Rules_ParseDefaultRulesYaml_Succeeds | Unit |
| Rules_ParseContentRules_ValidYaml_ReturnsRules | Unit |
| Rules_ParseContentRules_EmptyYaml_ReturnsEmpty | Unit |
| Rules_ParseContentRules_NoContentRulesSection_ReturnsEmpty | Unit |
| Rules_ParseContentRules_ContentAnyAndHasAmount | Unit |
| Rules_ParseContentRules_ZeroConfidence_DefaultsToHalf | Unit |
| Rules_ClassifyWithRules_EmptyRules_ReturnsDefault | Unit |
| Rules_ClassifyWithRules_EmptyRules_CustomDefault_Respected | Unit |
| Rules_ClassifyWithRules_SubjectMatchOnly_WhenNoSender | Unit |
| Rules_ClassifyWithRules_EmptySubject_SkipsSubjectRules | Unit |
| Rules_FromFile_ValidYaml_LoadsRules | Unit |
| Rules_FromFile_MissingFile_UsesDefaults | Unit |
| Rules_FromFile_Reload_UpdatesRules | Unit |
| Rules_ParseYaml_InvalidYaml_ReturnsError | Unit |
| Rules_ParseYaml_NullRules_ReturnsEmptyList | Unit |
| Rules_ParseYaml_InvalidRegex_SkipsRule | Unit |
| Rules_ParseYaml_MissingMatch_SkipsRule | Unit |
| Rules_FromFile_ReadException_ReturnsDefaults | Unit |

## ClassifierTests.fs (26 tests)

| Test | Category |
|------|----------|
| Classifier_ParseSidecar_ValidJson_ReturnsSidecar | Unit |
| Classifier_ParseSidecar_MissingOptionalFields_ReturnsNone | Unit |
| Classifier_ParseSidecar_InvalidJson_ReturnsError | Unit |
| Classifier_IsDuplicate_NoExistingDoc_ReturnsFalse | Integration |
| Classifier_IsDuplicate_ExistingDoc_ReturnsTrue | Integration |
| Classifier_IsDuplicate_DifferentHash_ReturnsFalse | Integration |
| Classifier_ComputeSha256_ReturnsConsistentHash | Unit |
| Classifier_ComputeSha256_DifferentContent_DifferentHash | Unit |
| Classifier_TryLoadSidecar_WithMetaFile_ReturnsSome | Unit |
| Classifier_TryLoadSidecar_NoMetaFile_ReturnsNone | Unit |
| Classifier_ProcessFile_ClassifiesAndMovesFile | Integration |
| Classifier_ProcessFile_WithSidecar_UsesMetadataForClassification | Integration |
| Classifier_ProcessFile_DuplicateHash_SkipsFile | Integration |
| Classifier_ProcessFile_MissingFile_ReturnsOk | Integration |
| Classifier_ProcessFile_UnmatchedFile_GoesToUnsorted | Integration |
| Classifier_ProcessFile_InsertsDocumentRecord | Integration |
| Classifier_ProcessFile_SubjectBasedClassification | Integration |
| Classifier_ProcessFile_SidecarEmailDate_StoredInDb | Integration |
| Classifier_SuggestRules_FindsUnsortedMatchInCategory | Integration |
| Classifier_SuggestRules_NoUnsortedDocs_ReturnsEmpty | Integration |
| Classifier_ReclassifyUnsortedBatch_ContentRuleMatch | Integration |
| Classifier_Reconcile_NewFileOnDisk_DetectedAsNewOnDisk | Integration |
| Classifier_Reconcile_FileInDb_NoAction | Integration |
| Classifier_Reconcile_EmptyArchive_ReturnsEmpty | Integration |
| Classifier_TryLoadSidecar_InvalidJson_ReturnsNone | Unit |
| Classifier_TryLoadSidecar_ReadThrows_ReturnsNone | Unit |

## EmailSyncTests.fs (28 tests)

| Test | Category |
|------|----------|
| EmailSync_SanitiseFileName_RemovesInvalidChars | Unit |
| EmailSync_SanitiseFileName_CollapsesUnderscores | Unit |
| EmailSync_SanitiseFileName_EmptyReturnsAttachment | Unit |
| EmailSync_SanitiseFileName_WhitespaceOnlyReturnsAttachment | Unit |
| EmailSync_SanitiseFileName_NormalNameUnchanged | Unit |
| EmailSync_BuildStandardName_IncludesDateSenderName | Unit |
| EmailSync_BuildStandardName_NoDate_UsesUndated | Unit |
| EmailSync_BuildStandardName_NoSender_UsesUnknown | Unit |
| EmailSync_ComputeSha256_DeterministicHash | Unit |
| EmailSync_ComputeSha256_DifferentDataDifferentHash | Unit |
| EmailSync_BuildSidecar_ContainsAllFields | Unit |
| EmailSync_SerialiseSidecar_ProducesValidJson | Unit |
| EmailSync_LoadSyncState_NoState_ReturnsNone | Integration |
| EmailSync_LoadSyncState_AfterSync_ReturnsSome | Integration |
| EmailSync_SyncAccount_NoMessages_ReturnsZeroCounts | Integration |
| EmailSync_SyncAccount_WithAttachments_DownloadsAndRecords | Integration |
| EmailSync_SyncAccount_DuplicateHash_SkipsDownload | Integration |
| EmailSync_SyncAccount_SmallAttachment_FilteredByMinSize | Integration |
| EmailSync_SyncAccount_AlreadyProcessedMessage_Skipped | Integration |
| EmailSync_DryRun_ListsMessagesWithAttachments | Integration |
| EmailSync_DryRun_NoMessages_ReturnsEmpty | Integration |
| EmailSync_SyncAccount_UpdatesSyncState | Integration |
| Backfill_DisabledConfig_Skips | Integration |
| Backfill_EmptyPage_CompletesImmediately | Integration |
| Backfill_LoadBackfillState_EmptyDb_ReturnsDefaults | Integration |
| EmailSync_SyncAll_EmptyMessages_ReturnsResultPerAccount | Integration |
| EmailSync_SyncAll_MultipleAccounts_SyncsEach | Integration |
| EmailSync_DryRunAll_EmptyMessages_ReturnsEmpty | Integration |

## FolderWatcherTests.fs (29 tests)

| Test | Category |
|------|----------|
| FolderWatcher_MatchesAnyPattern_StarDotPdf_MatchesPdf | Unit |
| FolderWatcher_MatchesAnyPattern_StarDotPdf_DoesNotMatchTxt | Unit |
| FolderWatcher_MatchesAnyPattern_WildcardStatement_MatchesContaining | Unit |
| FolderWatcher_MatchesAnyPattern_MultiplePatterns_MatchesAny | Unit |
| FolderWatcher_MatchesAnyPattern_EmptyPatterns_MatchesAll | Unit |
| FolderWatcher_MatchesAnyPattern_CaseInsensitive | Unit |
| FolderWatcher_MatchesAnyPattern_QuestionMark_MatchesSingleChar | Unit |
| FolderWatcher_BuildStandardName_FormatsCorrectly | Unit |
| FolderWatcher_BuildStandardName_SanitisesInvalidChars | Unit |
| FolderWatcher_SanitiseFileName_CollapsesUnderscores | Unit |
| FolderWatcher_SanitiseFileName_EmptyString_ReturnsFallback | Unit |
| FolderWatcher_IsDuplicate_NoExistingDoc_ReturnsFalse | Integration |
| FolderWatcher_IsDuplicate_ExistingDoc_ReturnsTrue | Integration |
| FolderWatcher_BuildSidecar_HasCorrectSourceType | Unit |
| FolderWatcher_SerialiseSidecar_ProducesValidJson | Unit |
| FolderWatcher_ProcessFile_CopiesMatchingFile | Integration |
| FolderWatcher_ProcessFile_SkipsNonMatchingPattern | Integration |
| FolderWatcher_ProcessFile_DetectsDuplicate | Integration |
| FolderWatcher_ProcessFile_MissingFile_ReturnsSkipped | Integration |
| FolderWatcher_ProcessFile_UsesSafeCopyRename | Integration |
| FolderWatcher_AddWatchFolder_AddsToConfig | Unit |
| FolderWatcher_AddWatchFolder_EmptyPatterns_DefaultsToStar | Unit |
| FolderWatcher_AddWatchFolder_Duplicate_ReturnsError | Unit |
| FolderWatcher_RemoveWatchFolder_RemovesFromConfig | Unit |
| FolderWatcher_RemoveWatchFolder_NotFound_ReturnsError | Unit |
| FolderWatcher_ListWatchFolders_ReportsStatus | Unit |
| FolderWatcher_ScanFolder_ProcessesAllMatchingFiles | Integration |
| FolderWatcher_GlobToRegex_StarDotPdf | Unit |
| FolderWatcher_GlobToRegex_WildcardMiddle | Unit |

## EmbeddingTests.fs (61 tests)

| Test | Category |
|------|----------|
| Embeddings_ChunkText_EmptyString_ReturnsEmpty | Unit |
| Embeddings_ChunkText_WhitespaceOnly_ReturnsEmpty | Unit |
| Embeddings_ChunkText_ShortText_ReturnsSingleChunk | Unit |
| Embeddings_ChunkText_ExactlyChunkSize_ReturnsSingleChunk | Unit |
| Embeddings_ChunkText_LongText_ProducesOverlappingChunks | Unit |
| Embeddings_ChunkText_Overlap_ChunksShareContent | Unit |
| Embeddings_ChunkText_SentenceBoundary_SplitsOnSentence | Unit |
| Embeddings_ChunkText_AllContent_IsCovered | Unit |
| Embeddings_BlobRoundTrip_PreservesData | Unit |
| Embeddings_BlobRoundTrip_EmptyArray | Unit |
| SemanticSearch_CosineSimilarity_IdenticalVectors_ReturnsOne | Unit |
| SemanticSearch_CosineSimilarity_OrthogonalVectors_ReturnsZero | Unit |
| SemanticSearch_CosineSimilarity_OppositeVectors_ReturnsNegOne | Unit |
| SemanticSearch_CosineSimilarity_EmptyVectors_ReturnsZero | Unit |
| SemanticSearch_CosineSimilarity_DifferentLengths_ReturnsZero | Unit |
| SemanticSearch_CosineSimilarity_ZeroVector_ReturnsZero | Unit |
| SemanticSearch_RRF_EmptyLists_ReturnsEmpty | Unit |
| SemanticSearch_RRF_SingleList_ScoresCorrectly | Unit |
| SemanticSearch_RRF_OverlappingLists_BothContribute | Unit |
| SemanticSearch_RRF_PreservesAllDocuments | Unit |
| SemanticSearch_RRF_TopRankedInBothLists_WinsOverall | Unit |
| Embeddings_EmbedDocument_ChunksAndStores | Integration |
| Embeddings_EmbedDocument_UpdatesDocumentMetadata | Integration |
| Embeddings_EmbedDocument_EmptyText_ReturnsZero | Integration |
| Embeddings_EmbedDocument_FailingClient_ReportsErrors | Integration |
| SemanticSearch_KeywordSearch_FindsMatchingDocuments | Integration |
| Embeddings_ChunkText_ShortText_SingleChunk | Unit |
| Embeddings_ChunkText_LongText_MultipleChunks | Unit |
| Embeddings_ChunkText_OverlapPresent | Unit |
| Embeddings_BlobRoundTrip_PreservesValues | Unit |
| Embeddings_InitSchema_CreatesChunkTable | Integration |
| Embeddings_StoreChunk_InsertsRow | Integration |
| Embeddings_EmbedDocument_StoresChunksAndUpdatesDoc | Integration |
| Embeddings_EmbedDocument_EmptyText_ReturnsOkZero | Integration |
| Embeddings_EmbedDocument_FailingClient_ReturnsError | Integration |
| Embeddings_BatchEmbed_UnavailableClient_ReturnsError | Integration |
| Embeddings_BatchEmbed_NoDocs_ReturnsOkZero | Integration |
| Embeddings_BatchEmbed_WithDocs_EmbedsSuccessfully | Integration |
| Embeddings_BatchEmbed_WithLimit_RespectsLimit | Integration |
| Embeddings_BatchEmbed_Force_ReEmbedsAlreadyEmbedded | Integration |
| Embeddings_BatchEmbed_ProgressCallback_Called | Integration |
| Embeddings_StoreChunk_NoEmbedding_InsertsNullBlob | Integration |
| Embeddings_BlobRoundTrip_SingleElement_Preserves | Unit |
| Embeddings_BlobRoundTrip_768Dims_Preserves | Unit |
| Embeddings_BlobRoundTrip_NegativeValues_Preserves | Unit |
| Embeddings_ChunkText_OnlyWhitespace_ReturnsEmpty | Unit |
| Embeddings_ChunkText_VerySmallChunkSize_Works | Unit |
| Embeddings_ChunkText_ZeroOverlap_NoOverlap | Unit |
| Embeddings_ChunkText_LargeOverlap_StillProgresses | Unit |
| Embeddings_BlobRoundTrip_1536Dims_Preserves | Unit |
| Embeddings_ChunkText_SingleChar_ReturnsSingleChunk | Unit |
| Embeddings_ChunkText_ExactlyAtBoundaryPlusOne_ProducesTwoChunks | Unit |
| Embeddings_BatchEmbed_WithProgressCallback_ReportsCorrectTotal | Integration |
| Embeddings_BatchEmbed_SkipsDocsWithNullText | Integration |
| Embeddings_BatchEmbed_SkipsDocsWithEmptyText | Integration |
| Embeddings_EmbedDocument_WhitespaceOnlyText_ReturnsOkZero | Integration |
| Embeddings_EmbeddingToBlob_ByteLength_IsCorrect | Unit |
| Embeddings_StoreChunk_WithEmbedding_SetsEmbeddedAt | Integration |
| Embeddings_EmbedDocument_ShortText_SingleChunk | Integration |
| Embeddings_StoreChunk_WithEmbedding_StoresBlob | Integration |
| Embeddings_BlobRoundtrip_PreservesValues | Property |

## SearchTests.fs (30 tests)

| Test | Category |
|------|----------|
| Search_SanitiseQuery_RemovesFtsOperators | Unit |
| Search_SanitiseQuery_QuotesTokens | Unit |
| Search_SanitiseQuery_EmptyInput_ReturnsEmpty | Unit |
| Search_SanitiseQuery_WhitespaceOnly_ReturnsEmpty | Unit |
| Search_SanitiseQuery_SpecialChars_Stripped | Unit |
| Search_SanitiseQuery_SingleToken_Quoted | Unit |
| Search_BuildQuery_BasicQuery_IncludesMatchAndBm25 | Unit |
| Search_BuildQuery_WithCategory_AddsFilter | Unit |
| Search_BuildQuery_WithSender_AddsLikeFilter | Unit |
| Search_BuildQuery_WithDateRange_AddsFilters | Unit |
| Search_BuildQuery_WithAccount_AddsFilter | Unit |
| Search_BuildQuery_EmptyQuery_ReturnsNoResults | Unit |
| Search_BuildQuery_LimitIsParameterised | Unit |
| Search_MapRow_MapsAllFields | Unit |
| Search_MapRow_HandlesMissingFields | Unit |
| Search_MapRow_HandlesDbNullValues | Unit |
| Search_Execute_FindsMatchingDocument | Integration |
| Search_Execute_FilterByCategory_ExcludesOthers | Integration |
| Search_Execute_NoMatch_ReturnsEmpty | Integration |
| Search_Execute_ReturnsSnippet | Integration |
| Search_Execute_EmptyQuery_ReturnsEmpty | Integration |
| Search_Execute_MultipleResults_RankedByRelevance | Integration |
| Search_Execute_WithSourceTypeFilter_FiltersCorrectly | Integration |
| Search_Execute_WithCategoryFilter_FiltersCorrectly | Integration |
| Search_Execute_WithSenderFilter_FiltersCorrectly | Integration |
| Search_Execute_WithDateRange_FiltersCorrectly | Integration |
| Search_SanitiseQuery_SpecialChars_Cleaned | Integration |
| Search_ExecuteUnified_ReturnsResults | Integration |
| Search_SanitiseQuery_SpecialChars_SomeCleaned | Unit |
| Search_DefaultFilter_HasCorrectDefaults | Unit |

## McpTests.fs (59 tests)

| Test | Category |
|------|----------|
| McpServer_ParseRequest_ValidRequest_ReturnsOk | Unit |
| McpServer_ParseRequest_MissingMethod_ReturnsError | Unit |
| McpServer_ParseRequest_InvalidJson_ReturnsError | Unit |
| McpServer_SerialiseResponse_ContainsJsonRpcVersion | Unit |
| McpServer_SerialiseResponse_ErrorResponse_ContainsErrorField | Unit |
| McpServer_Dispatch_Initialize_ReturnsCapabilities | Integration |
| McpServer_Dispatch_ToolsList_ReturnsAllTools | Integration |
| McpServer_Dispatch_UnknownMethod_ReturnsError | Integration |
| McpServer_Dispatch_ToolsCallUnknownTool_ReturnsError | Integration |
| McpServer_Dispatch_ToolsCallSearch_ReturnsContent | Integration |
| McpServer_Dispatch_ToolsCallStats_ReturnsStats | Integration |
| McpServer_Dispatch_ToolsCallListCategories_ReturnsCategories | Integration |
| McpTools_IsPathSafe_RelativePath_ReturnsOk | Unit |
| McpTools_IsPathSafe_PathTraversal_ReturnsError | Unit |
| McpTools_IsPathSafe_DotDotInMiddle_ReturnsError | Unit |
| McpTools_IsPathSafe_AbsolutePath_ReturnsError | Unit |
| McpTools_IsPathSafe_WindowsAbsolutePath_ReturnsError | Unit |
| McpTools_IsPathSafe_EmptyPath_ReturnsError | Unit |
| McpTools_IsPathSafe_WhitespacePath_ReturnsError | Unit |
| McpTools_Search_EmptyQuery_ReturnsError | Integration |
| McpTools_GetDocument_MissingIdAndPath_ReturnsNotFound | Integration |
| McpTools_GetDocument_ValidId_ReturnsDocument | Integration |
| McpTools_ReadFile_PathTraversal_ReturnsError | Unit |
| McpTools_ReadFile_MissingPath_ReturnsError | Unit |
| McpServer_ProcessMessage_CompleteRoundTrip_ValidJsonRpc | Integration |
| MCP_ListReminders_ReturnsActiveReminders | Integration |
| MCP_UpdateReminder_MarkComplete_ChangesStatus | Integration |
| McpServer_GetDocumentContent_Markdown_ReturnsStructuredContent | Integration |
| McpTools_ListDocumentsFeed_ReturnsDocuments | Integration |
| McpTools_ListDocumentsFeed_EmptyDb_ReturnsEmptyArray | Integration |
| McpTools_GetFeedStats_ReturnsStats | Integration |
| McpTools_GetProcessingQueue_ReturnsQueueInfo | Integration |
| McpTools_ReextractDocument_ValidId_ReturnsSuccess | Integration |
| McpTools_ReextractDocument_MissingId_ReturnsError | Integration |
| McpTools_ReclassifyDocument_MissingId_ReturnsError | Integration |
| McpTools_ReclassifyDocument_MissingCategory_ReturnsError | Integration |
| McpTools_GetDocumentContent_MissingId_ReturnsError | Integration |
| McpTools_GetDocumentContent_ValidId_ReturnsContent | Integration |
| McpTools_ReadFile_ValidPath_ReturnsContent | Unit |
| McpTools_ListDocumentsFeed_WithCategory_FiltersCorrectly | Integration |
| McpTools_UpdateReminder_Snooze_ChangesStatus | Integration |
| McpTools_UpdateReminder_Dismiss_ChangesStatus | Integration |
| McpTools_UpdateReminder_UnknownAction_ReturnsError | Integration |
| McpTools_UpdateReminder_MissingFields_ReturnsError | Integration |
| McpTools_UpdateReminder_Paid_IsAlias_ForComplete | Integration |
| McpTools_ReclassifyDocument_ValidDoc_Reclassifies | Integration |
| McpTools_ReadFile_MissingPathArg_ReturnsError | Unit |
| McpTools_ReadFile_NonexistentFile_ReturnsError | Unit |
| McpTools_GetProcessingQueue_WithDocs_ReturnsJsonObject | Integration |
| McpServer_Dispatch_ToolsCallGetDocument_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallListDocuments_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallGetProcessingQueue_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallReadFile_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallListReminders_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallGetFeedStats_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallGetDocumentContent_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallReclassify_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallReextract_ReturnsResult | Integration |
| McpServer_Dispatch_DeepExtract_NoDeps_ReturnsError | Integration |

## EmailBodyTests.fs (24 tests)

| Test | Category |
|------|----------|
| EmailSync_StripHtml_RemovesTags | Unit |
| EmailSync_StripHtml_DecodesEntities | Unit |
| EmailSync_StripHtml_CollapsesWhitespace | Unit |
| EmailSync_StripHtml_EmptyInput_ReturnsEmpty | Unit |
| EmailSync_StripHtml_PlainText_Unchanged | Unit |
| EmailSync_StripHtml_ComplexHtml_ProducesCleanText | Unit |
| EmailSync_StripHtml_DecodesNbsp | Unit |
| MessagesFts_InsertTrigger_IndexesBodyText | Integration |
| MessagesFts_SearchBySubject_FindsMessage | Integration |
| MessagesFts_SearchBySender_FindsMessage | Integration |
| MessagesFts_NoMatch_ReturnsZero | Integration |
| MessagesFts_UpdateTrigger_ReindexesOnUpdate | Integration |
| Search_ExecuteEmailSearch_FindsMessageByBody | Integration |
| Search_ExecuteEmailSearch_ReturnsSnippet | Integration |
| Search_ExecuteEmailSearch_NoMatch_ReturnsEmpty | Integration |
| Search_ExecuteUnified_MergesDocumentAndEmailResults | Integration |
| Search_ExecuteUnified_RespectsLimit | Integration |
| Search_ExecuteUnified_SortedByRelevance | Integration |
| Database_SchemaV2_HasBodyTextColumn | Integration |
| Database_SchemaV2_HasThreadIdColumn | Integration |
| Database_SchemaV2_HasMessagesFts | Integration |
| Database_SchemaV2_VersionIs2 | Integration |
| EmailSync_SyncAccount_FetchesBodyWhenMissing | Integration |
| EmailSync_SyncAccount_SkipsBodyFetchWhenPresent | Integration |

## MarkdownTests.fs (26 tests)

| Test | Category |
|------|----------|
| Markdown_RenderFrontmatter_ContainsAllFields | Unit |
| Markdown_RenderFrontmatter_OmitsNoneFields | Unit |
| Markdown_ExtractDate_FindsDates | Unit |
| Markdown_ExtractDate_NoDate_ReturnsNone | Unit |
| Markdown_ExtractAmount_FindsAmounts | Unit |
| Markdown_ExtractAmount_MultiplePicks_LargestAmount | Unit |
| Markdown_ExtractAbn_FindsAbnAcn | Unit |
| Markdown_ExtractVendor_FirstLine | Unit |
| Markdown_CsvToMarkdown_BasicTable | Unit |
| Markdown_CsvToMarkdown_QuotedFields | Unit |
| Markdown_CsvToMarkdown_Empty_ReturnsPlaceholder | Unit |
| Markdown_TextToMarkdown_PreservesParagraphs | Unit |
| Markdown_TextToMarkdown_EmptyReturnsPlaceholder | Unit |
| Markdown_ChunkByHeadings_SplitsOnHeadings | Unit |
| Markdown_ChunkByHeadings_ShortTextSingleChunk | Unit |
| Markdown_ChunkByHeadings_LongSectionFallsBackToCharSplit | Unit |
| Markdown_BuildConversion_IncludesFrontmatterAndBody | Unit |
| Markdown_WriteSidecar_CreatesFile | Unit |
| Markdown_ProcessDocument_WritesMarkdownSidecar | Integration |
| Markdown_ProcessDocument_NoText_ReturnsError | Integration |
| Markdown_ProcessDocument_NonexistentDoc_ReturnsError | Integration |

## ReminderTests.fs (15 tests)

| Test | Category |
|------|----------|
| Reminders_DetectBill_InvoiceWithDueDate_CreatesReminder | Unit |
| Reminders_DetectBill_WrongCategory_ReturnsNone | Unit |
| Reminders_DetectBill_OldDate_ReturnsNone | Unit |
| Reminders_DetectBill_NoAmount_ReturnsNone | Unit |
| Reminders_DetectBill_FutureDate_Within60Days_CreatesReminder | Unit |
| Reminders_DetectBill_FutureDate_Beyond60Days_ReturnsNone | Unit |
| Reminders_EvaluateNew_InsertsReminders | Integration |
| Reminders_EvaluateNew_DeduplicatesExisting | Integration |
| Reminders_MarkCompleted_ChangesStatus | Integration |
| Reminders_Snooze_HidesUntilExpiry | Integration |
| Reminders_Dismiss_PermanentlyRemoves | Integration |
| Reminders_GetSummary_CorrectCounts | Integration |
| Reminders_UnsnoozeExpired_WithExpiredSnooze_UnsnoozesReminder | Integration |
| Reminders_UnsnoozeExpired_NoSnoozedReminders_ReturnsZero | Integration |
| Reminders_UnsnoozeExpired_BeforeExpiry_ReturnsZero | Integration |

## ChatTests.fs (31 tests)

| Test | Category |
|------|----------|
| Chat_FormatResults_EmptyList_ReturnsEmptyString | Unit |
| Chat_FormatResults_SingleResult_IncludesCategoryAndName | Unit |
| Chat_FormatResults_IncludesSenderWhenPresent | Unit |
| Chat_FormatResults_IncludesAmountWhenPresent | Unit |
| Chat_FormatResults_TruncatesTo10Results | Unit |
| Chat_SystemPrompt_IsNotEmpty | Unit |
| Chat_Query_KeywordMode_ReturnsResults | Integration |
| Chat_Query_AiMode_ReturnsAiSummary | Integration |
| Chat_Query_EmptyQuery_ReturnsEmpty | Integration |
| Chat_ProviderFromConfig_Ollama_ReturnsOllamaProvider | Unit |
| Chat_Query_AiError_ReturnsErrorMessage | Integration |
| Chat_Query_NoResults_AiSummaryIsNone | Integration |
| Chat_Query_MultipleResults_ReturnsAll | Integration |
| Chat_Query_WithFakeChatProvider_ReturnsCustomResponse | Integration |
| Chat_ProviderFromConfig_AzureOpenAI_WithValidConfig_ReturnsAzureProvider | Unit |
| Chat_ProviderFromConfig_AzureOpenAI_EmptyEndpoint_FallsBackToOllama | Unit |
| Chat_ProviderFromConfig_AzureOpenAI_EmptyApiKey_FallsBackToOllama | Unit |
| Chat_BuildUserPrompt_IncludesQueryAndContext | Unit |
| Chat_FormatResults_IncludesSubjectAndDateAndVendor | Unit |
| Chat_BuildUserPrompt_EmptyContext_StillIncludesQuery | Unit |
| Chat_FormatResults_NoOptionalFields_FormatsCleanly | Unit |
| Chat_Query_AiEnabled_NoResults_SkipsAiCall | Integration |
| Chat_SystemPrompt_ContainsDocumentTypes | Unit |
| Chat_FormatResults_MultipleResults_NumbersCorrectly | Unit |
| Chat_OllamaProvider_ConnectionRefused_ReturnsError | Unit |
| Chat_AzureOpenAIProvider_ConnectionRefused_ReturnsError | Unit |
| Chat_ProviderFromConfig_WhitespaceEndpoint_FallsBackToOllama | Unit |
| Chat_BuildUserPrompt_ContainsAnswerBrieflyInstruction | Unit |
| Chat_FormatResults_ResultWithOnlySenderAndSnippet_FormatsCorrectly | Unit |
| Chat_Query_NoResults_AiDisabled_ReturnsEmptyResults | Integration |
| Chat_Query_WithResults_AiDisabled_ReturnsResultsNoSummary | Integration |

## StatsTests.fs (22 tests)

| Test | Category |
|------|----------|
| Stats_GetIndexStats_EmptyDb_ReturnsZeros | Integration |
| Stats_GetIndexStats_WithDocuments_ReturnsCorrectCounts | Integration |
| Stats_GetCategoryCounts_NonexistentDir_ReturnsEmpty | Unit |
| Stats_GetAccountStats_EmptyDb_ReturnsEmpty | Integration |
| Stats_GetAccountStats_WithSyncState_ReturnsAccounts | Integration |
| Stats_GetIndexStats_WithExtractedAndEmbedded_CountsCorrectly | Integration |
| Stats_GetIndexStats_NonExistentDbPath_SizeIsZero | Integration |
| Stats_GetAccountStats_MultipleAccounts_ReturnsAll | Integration |
| Stats_GetCategoryCounts_WithRealDir_ReturnsCounts | Integration |
| Stats_GetCategoryCounts_EmptySubdirs_ExcludesEmpty | Integration |
| Stats_GetAccountStats_NullSyncAt_ReturnsNone | Integration |
| Stats_getPipelineCounts_EmptyDbAndFs_AllZeroes | Unit |
| Stats_getPipelineCounts_FilesInIntake_CountsIntake | Unit |
| Stats_getPipelineCounts_DocumentsNeedingExtraction_CountsExtracting | Unit |
| Stats_getPipelineCounts_UnsortedExtractedDocs_CountsClassifying | Unit |
| Stats_GetExtractionQueue_EmptyDb_ReturnsEmpty | Integration |
| Stats_GetExtractionQueue_ReturnsUnextractedDocs | Integration |
| Stats_GetExtractionQueue_RespectsLimit | Integration |
| Stats_GetRecentClassifications_EmptyDb_ReturnsEmpty | Integration |
| Stats_GetRecentClassifications_ReturnsClassifiedDocs | Integration |
| Stats_GetTierBreakdown_EmptyDb_AllZeroes | Integration |
| Stats_GetTierBreakdown_CountsByTier | Integration |

## DomainTests.fs (42 tests)

| Test | Category |
|------|----------|
| Domain_SourceType_RoundTrip | Unit |
| Domain_SourceType_UnknownString_ReturnsError | Unit |
| Domain_ReminderStatus_RoundTrip | Unit |
| Domain_ChatProviderKind_RoundTrip | Unit |
| Domain_ChatProviderKind_AlternateFormats_ParseCorrectly | Unit |
| Domain_ChatProviderKind_UnknownString_ReturnsError | Unit |
| ClassificationRule_Describe_DefaultRule_ContainsDefault | Unit |
| ClassificationRule_Describe_FilenameRule_ContainsName | Unit |
| ClassificationRule_Describe_SubjectRule_ContainsSubject | Unit |
| ClassificationRule_Describe_DomainRule_ContainsDomain | Unit |
| TaskResult_Map_OkValue_Transforms | Unit |
| TaskResult_Map_Error_PreservesError | Unit |
| TaskResult_Bind_OkToOk_Chains | Unit |
| TaskResult_Bind_ErrorShortCircuits | Unit |
| TaskResult_MapError_Error_Transforms | Unit |
| TaskResult_MapError_Ok_PreservesOk | Unit |
| Prelude_FoldTask_EmptyList_ReturnsInit | Unit |
| Prelude_FoldTask_AccumulatesValues | Unit |
| RowReader_OptString_Present_ReturnsSome | Unit |
| RowReader_OptString_Missing_ReturnsNone | Unit |
| RowReader_OptString_DBNull_ReturnsNone | Unit |
| RowReader_OptInt64_Present_ReturnsSome | Unit |
| RowReader_OptInt64_Missing_ReturnsNone | Unit |
| RowReader_OptInt64_FromInt_Converts | Unit |
| RowReader_String_Present_ReturnsValue | Unit |
| RowReader_String_Missing_ReturnsFallback | Unit |
| RowReader_Int64_Present_ReturnsValue | Unit |
| RowReader_Int64_FromInt_Converts | Unit |
| RowReader_Float_Present_ReturnsValue | Unit |
| RowReader_Float_Missing_ReturnsFallback | Unit |
| RowReader_OptFloat_Present_ReturnsSome | Unit |
| RowReader_OptFloat_FromInt64_Converts | Unit |
| RowReader_OptDateTimeOffset_ValidString_ReturnsSome | Unit |
| RowReader_OptDateTimeOffset_Invalid_ReturnsNone | Unit |
| RowReader_Raw_ReturnsOriginalMap | Unit |

## ExtractionFieldTests.fs (47 tests)

| Test | Category |
|------|----------|
| Extraction_TryExtractDate_FindsDates | Unit |
| Extraction_TryExtractDate_NoDate_ReturnsNone | Unit |
| Extraction_TryExtractDate_EmptyString_ReturnsNone | Unit |
| Extraction_TryExtractAmount_FindsAmounts | Unit |
| Extraction_TryExtractAmount_NoAmount_ReturnsNone | Unit |
| Extraction_TryExtractAbn_FindsAbnAcn | Unit |
| Extraction_TryExtractAbn_NoAbn_ReturnsNone | Unit |
| Extraction_TryExtractVendor_FindsCompanyName | Unit |
| Extraction_IsLikelyScanned_ShortText_ReturnsTrue | Unit |
| Extraction_IsLikelyScanned_LongText_ReturnsFalse | Unit |
| Extraction_IsPdf_DetectsCorrectly | Unit |
| Extraction_IsImage_DetectsCorrectly | Unit |
| Extraction_AnalyseText_PopulatesFields | Unit |
| Extraction_ProcessDocument_PdfFile_ExtractsAndUpdatesDb | Integration |
| Extraction_ProcessDocument_MissingFile_ReturnsError | Integration |
| Extraction_ProcessDocument_ExtractorFails_ReturnsError | Integration |
| Extraction_ExtractBatch_ProcessesUnextractedDocs | Integration |
| Extraction_ExtractBatch_RespectsLimit | Integration |
| Extraction_ExtractBatch_CategoryFilter_OnlyProcessesMatching | Integration |
| Extraction_ExtractFromBytes_PlainText_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_Markdown_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_LogFile_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_Csv_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_UnsupportedType_ReturnsError | Unit |
| Extraction_ExtractFromBytes_Image_CallsImageExtractor | Unit |
| Extraction_ExtractFromBytes_ImagePng_CallsImageExtractor | Unit |
| Extraction_ExtractFromBytes_ImageError_ReturnsError | Unit |
| Extraction_StripMarkdown_RemovesHeadings | Unit |
| Extraction_StripMarkdown_RemovesBoldItalic | Unit |
| Extraction_StripMarkdown_RemovesTableSyntax | Unit |
| Extraction_StripMarkdown_RemovesFrontmatter | Unit |
| Extraction_StripMarkdown_EmptyInput_ReturnsEmpty | Unit |
| Extraction_AnalyseText_WithMarkdown_SetsMarkdownField | Unit |
| Extraction_AnalyseText_WithoutMarkdown_MarkdownIsNone | Unit |
| Extraction_ProcessDocument_StoresMarkdownSeparately | Integration |

## PdfStructureTests.fs (34 tests)

| Test | Category |
|------|----------|
| PdfStructure_WordsToLines_GroupsByYProximity | Unit |
| PdfStructure_WordsToLines_SingleWord_ReturnsSingleLine | Unit |
| PdfStructure_WordsToLines_SameLineDifferentX_SortsByXAscending | Unit |
| PdfStructure_WordsToLines_ThreeDistinctLines_TopToBottom | Unit |
| PdfStructure_LinesToText_PreservesReadingOrder | Unit |
| PdfStructure_LinesToText_EmptyLines_ReturnsEmpty | Unit |
| PdfStructure_LinesToText_SingleLine_NoTrailingNewline | Unit |
| PdfStructure_DetectBodyFontSize_ReturnsMostCommonSize | Unit |
| PdfStructure_DetectBodyFontSize_EmptyLines_ReturnsDefault | Unit |
| PdfStructure_DetectHeadings_LargeFont_ReturnsH1 | Unit |
| PdfStructure_DetectHeadings_BoldFont_ReturnsH2 | Unit |
| PdfStructure_DetectHeadings_AllCaps_ReturnsH3 | Unit |
| PdfStructure_DetectHeadings_BodyText_ReturnsNone | Unit |
| PdfStructure_FindColumnBoundaries_ThreeColumns_ReturnsThreeBoundaries | Unit |
| PdfStructure_IsTableRegion_AlignedRows_ReturnsTrue | Unit |
| PdfStructure_IsTableRegion_ParagraphText_ReturnsFalse | Unit |
| PdfStructure_ExtractTableCells_AssignsWordsToCorrectColumns | Unit |
| PdfStructure_DetectTables_BankStatement_ExtractsTransactionTable | Unit |
| PdfStructure_IsContinuation_SameColumns_ReturnsTrue | Unit |
| PdfStructure_IsContinuation_DifferentColumns_ReturnsFalse | Unit |
| PdfStructure_MergeMultiPageTables_CombinesRows_KeepsSingleHeader | Unit |
| PdfStructure_DetectKV_ColonSeparated_ReturnsKV | Unit |
| PdfStructure_DetectKV_GapSeparated_ReturnsKV | Unit |
| PdfStructure_DetectKV_ParagraphText_ReturnsNone | Unit |
| PdfStructure_IsCidEncoded_WithCidSequences_ReturnsTrue | Unit |
| PdfStructure_IsCidEncoded_NormalText_ReturnsFalse | Unit |
| PdfStructure_CalculateConfidence_FullyDecoded_ReturnsHigh | Unit |
| PdfStructure_CalculateConfidence_MostlyCid_ReturnsLow | Unit |
| PdfStructure_ExtractStructured_GeneratedPdf_ReturnsContent | Unit |
| PdfStructure_ToMarkdown_HeadingsAndTables_WellFormed | Unit |
| PdfStructure_ToMarkdown_Frontmatter_IncludesMetadata | Unit |
| PdfStructure_ExtractLetters_InvalidBytes_ReturnsEmptyList | Unit |
| PdfStructure_ExtractLines_GeneratedPdf_ExtractsText | Integration |
| PdfStructure_LinesToText_LineCount_MatchesNewlineCount | Property |

## DocumentFeedTests.fs (21 tests)

| Test | Category |
|------|----------|
| DocumentFeed_ListDocuments_SinceId0_ReturnsAll | Integration |
| DocumentFeed_ListDocuments_SinceId_ReturnsOnlyNewer | Integration |
| DocumentFeed_ListDocuments_FilterByCategory | Integration |
| DocumentFeed_ListDocuments_Limit_RespectsLimit | Integration |
| DocumentFeed_GetFeedStats_ReturnsCorrectCounts | Integration |
| DocumentFeed_GetContent_Markdown_ReturnsExtractedText | Integration |
| DocumentFeed_GetContent_Text_StripsFrontmatter | Integration |
| DocumentFeed_GetContent_InvalidId_ReturnsError | Integration |
| DocumentFeed_GetContent_Raw_ReturnsFileContent | Integration |
| DocumentFeed_GetContent_Raw_MissingFile_ReturnsError | Integration |
| DocumentFeed_ParseFormat_Text_ReturnsSome | Unit |
| DocumentFeed_ParseFormat_Markdown_ReturnsSome | Unit |
| DocumentFeed_ParseFormat_Raw_ReturnsSome | Unit |
| DocumentFeed_ParseFormat_CaseInsensitive | Unit |
| DocumentFeed_ParseFormat_Unknown_ReturnsNone | Unit |
| DocumentFeed_FeedDocToJson_IncludesAllFields | Unit |
| DocumentFeed_FeedDocToJson_OmitsNoneFields | Unit |
| DocumentFeed_FeedStatsToJson_IncludesFields | Unit |
| DocumentFeed_GetContent_Markdown_NoExtractedText_ReturnsError | Integration |
| DocumentFeed_ListDocuments_EmptyDb_ReturnsEmpty | Integration |
| DocumentFeed_GetFeedStats_EmptyDb_ReturnsZeros | Integration |

## ContentClassifierTests.fs (25 tests)

| Test | Category |
|------|----------|
| ContentClassifier_Evaluate_PayslipKeywords_MatchesPayslips | Unit |
| ContentClassifier_Evaluate_BankStatementHeaders_MatchesBankStatements | Unit |
| ContentClassifier_Evaluate_NoMatch_ReturnsNone | Unit |
| ContentClassifier_Classify_MultipleMatches_ReturnsBestConfidence | Unit |
| ContentClassifier_Classify_NoRulesMatch_ReturnsNone | Unit |
| ContentClassifier_BuildPrompt_TruncatesTo2000Chars | Unit |
| ContentClassifier_BuildPrompt_ShortText_NoTruncation | Unit |
| ContentClassifier_ParseResponse_ValidJson_ReturnsCategory | Unit |
| ContentClassifier_ParseResponse_MarkdownCodeBlock_ExtractsJson | Unit |
| ContentClassifier_ParseResponse_InvalidJson_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_MissingCategory_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_EmptyCategory_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_ValidJson_ReturnsValues | Unit |
| ContentClassifier_ParseResponse_NoReasoning_DefaultsToEmpty | Unit |
| ContentClassifier_BuildPrompt_IncludesCategoriesAndContent | Unit |
| ContentClassifier_BuildPrompt_LongText_Truncates | Unit |
| ContentClassifier_BuildPrompt_AlwaysTruncatesTo2000 | Property |
| ContentClassifier_EvaluateRule_HasAmount_MatchesWhenPresent | Unit |
| ContentClassifier_EvaluateRule_HasAmount_NoMatchWhenAbsent | Unit |
| ContentClassifier_Classify_EmptyText_ReturnsNone | Unit |
| ContentClassifier_Classify_SingleMatchingRule_ReturnsCategory | Unit |
| ContentClassifier_ParseResponse_GarbageInput_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_MissingConfidence_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_ExtraWhitespace_ParsesCorrectly | Unit |
| Classifier_ReclassifyUnsortedBatch_NoCandidates_ReturnsZeros | Integration |

## SemanticSearchTests.fs (25 tests)

| Test | Category |
|------|----------|
| SemanticSearch_CosineSimilarity_IdenticalVectors_Returns1 | Unit |
| SemanticSearch_CosineSimilarity_OrthogonalVectors_Returns0 | Unit |
| SemanticSearch_CosineSimilarity_OppositeVectors_ReturnsNeg1 | Unit |
| SemanticSearch_CosineSimilarity_ZeroVector_Returns0 | Unit |
| SemanticSearch_ReciprocalRankFusion_CombinesAndDeduplicates | Unit |
| SemanticSearch_ReciprocalRankFusion_DocInBothLists_RanksHigher | Unit |
| SemanticSearch_ReciprocalRankFusion_EmptyInputs_ReturnsEmpty | Unit |
| SemanticSearch_KeywordSearch_FindsMatchingDoc | Integration |
| SemanticSearch_KeywordSearch_NoMatch_ReturnsEmpty | Integration |
| SemanticSearch_KeywordSearch_EmptyQuery_ReturnsEmpty | Integration |
| SemanticSearch_EnrichResult_ReturnsDocDetails | Integration |
| SemanticSearch_HybridSearch_FallsBackToKeyword_WhenNoEmbeddings | Integration |
| SemanticSearch_Search_KeywordMode_ReturnsResults | Integration |
| SemanticSearch_Search_KeywordMode_NoResults_ReturnsEmpty | Integration |
| SemanticSearch_Search_KeywordMode_EmptyQuery_ReturnsEmpty | Integration |
| SemanticSearch_EnrichResult_MissingDoc_ReturnsEmptyFields | Integration |
| SemanticSearch_KeywordSearch_MultipleMatches_ReturnsAll | Integration |
| SemanticSearch_EnrichResult_WithExtractedText_ReturnsSnippet | Integration |
| SemanticSearch_Search_SemanticMode_FailingEmbedder_ReturnsEmpty | Integration |
| SemanticSearch_HybridSearch_KeywordOnlyWhenSemFails | Integration |
| SemanticSearch_SemanticSearch_WithChunks_ReturnsResults | Integration |
| SemanticSearch_SemanticSearch_NoChunks_ReturnsEmpty | Integration |
| SemanticSearch_HybridSearch_WithChunks_CombinesResults | Integration |
| SemanticSearch_Search_HybridMode_ReturnsResults | Integration |
| SemanticSearch_Search_SemanticMode_WithChunks_ReturnsResults | Integration |

## CsvExtractionTests.fs (15 tests)

| Test | Category |
|------|----------|
| CsvExtraction_ParseCsvLine_Comma_SplitsCorrectly | Unit |
| CsvExtraction_ParseCsvLine_QuotedFieldWithComma | Unit |
| CsvExtraction_ParseCsvLine_EmptyFields | Unit |
| CsvExtraction_ParseCsvLine_Semicolon | Unit |
| CsvExtraction_DetectDelimiter_DetectsCorrectly | Unit |
| CsvExtraction_DetectDelimiter_DefaultsToComma | Unit |
| CsvExtraction_ExtractCsv_ProducesContent | Unit |
| CsvExtraction_ExtractCsv_EmptyString_EmptyPages | Unit |
| CsvExtraction_ExtractCsv_HeaderOnly | Unit |
| CsvExtraction_ParseCsvLine_UnterminatedQuote_HandlesGracefully | Unit |
| CsvExtraction_ParseCsvLine_EmptyFields_ReturnsEmptyStrings | Unit |
| CsvExtraction_DetectDelimiter_TabSeparated_ReturnsTab | Unit |
| CsvExtraction_ParseCsvLine_FieldCount_GreaterThanZero | Property |

## ActivityLogTests.fs (15 tests)

| Test | Category |
|------|----------|
| ActivityLog_LogInfo_And_GetRecent_RoundTrips | Integration |
| ActivityLog_LogWarning_IncludesDetails | Integration |
| ActivityLog_LogError_SetsErrorLevel | Integration |
| ActivityLog_GetRecent_RespectsLimit | Integration |
| ActivityLog_GetRecent_EmptyLog_ReturnsEmpty | Integration |
| ActivityLog_GetForDocument_FiltersCorrectly | Integration |
| ActivityLog_GetByCategory_FiltersCorrectly | Integration |
| ActivityLog_LogError_WithDocumentId_LinksToDocument | Integration |
| ActivityLog_GetRecent_MultipleEntries_RespectsOrder | Integration |
| ActivityLog_LogWarning_SetsWarningLevel | Integration |
| ActivityLog_GetByCategory_NoMatches_ReturnsEmpty | Integration |
| ActivityLog_LogError_StoresErrorLevel | Integration |
| ActivityLog_GetForDocument_ReturnsMatchingEntries | Integration |
| ActivityLog_GetByCategory_ReturnsMatchingCategory | Integration |
| ActivityLog_GetRecent_RespectsLimit_LargerSet | Integration |

## DocumentBrowserTests.fs (7 tests)

| Test | Category |
|------|----------|
| DocumentBrowser_ListCategories_EmptyDb_ReturnsEmpty | Integration |
| DocumentBrowser_ListCategories_MultipleCats_ReturnsAll | Integration |
| DocumentBrowser_ListDocuments_FiltersByCategory | Integration |
| DocumentBrowser_ListDocuments_RespectsLimit | Integration |
| DocumentBrowser_GetDocumentDetail_ExistingDoc_ReturnsSome | Integration |
| DocumentBrowser_GetDocumentDetail_Missing_ReturnsNone | Integration |
| DocumentBrowser_GetDocumentDetail_PipelineStatus_Unextracted | Integration |

## DocumentManagementTests.fs (5 tests)

| Test | Category |
|------|----------|
| DocumentManagement_Reclassify_ChangesCategoryInDb | Integration |
| DocumentManagement_Reclassify_NonexistentDoc_ReturnsError | Integration |
| DocumentManagement_Reextract_ClearsExtractedAt | Integration |
| DocumentManagement_GetProcessingQueue_EmptyDb_AllZeros | Integration |
| DocumentManagement_GetProcessingQueue_MixedDocs_CorrectCounts | Integration |

## ReclassificationTests.fs (9 tests)

| Test | Category |
|------|----------|
| Content_Reclassification_ReportsProvenanceChangesOnly | Integration |
| Reclassification_RejectsConcurrentCategoryAndProvenanceChanges | Integration |
| Reclassification_PreservesSourceIdentityAndDoesNotCreateCategoryDirectory | Integration |
| Reclassification_MissingSourceFailsBeforeMetadataMutation | Integration |
| Reclassification_IsIdempotentAndUpdatesProvenanceTagAndFts | Integration |
| Reclassification_ReplacesGeneratedCategoryTagAndPreservesUserTags | Integration |
| Reclassification_PreservesUserTagMatchingGeneratedCategory | Integration |
| Reclassification_RollsBackCategoryTagAndFtsWhenTriggerWriteFails | Integration |
| Reclassification_PreservesEveryV5CompletionAndOutput | Integration |

## LegacyReclassificationTests.fs (18 cases)

| Test | Category |
|------|----------|
| Legacy_ScanBoundsRejectUnsafeLimits (4 cases) | Unit |
| Legacy_DetectorDryRunAndRepairUseUniqueShaEvidenceOnly | Integration |
| Legacy_RepairLeavesAmbiguousShaEvidenceUnchanged | Integration |
| Legacy_RepairLeavesMissingShaEvidenceUnchangedAndVisible | Integration |
| Legacy_RepairExcludesGeneratedArtifactsFromShaCandidates (9 cases) | Integration |
| Legacy_RepairLeavesCurrentPathShaMismatchUnchangedAndVisible | Integration |
| Legacy_TruncatedScanNeverClaimsUniqueIdentity | Integration |

## ReclassificationApiTests.fs (4 tests)

| Test | Category |
|------|----------|
| Rest_SingleReturnsExplicitIdentityPreservingOutcome | Integration |
| Rest_SingleReportsIdempotentReclassificationAsUnchanged | Integration |
| Rest_SingleMapsWhitespaceCategoryValidationExplicitly | Integration |
| Rest_BatchReportsTruthfulCountsForMixedOutcomes | Integration |

## ThreadsTests.fs (7 tests)

| Test | Category |
|------|----------|
| Threads_ListThreads_EmptyDb_ReturnsEmpty | Integration |
| Threads_ListThreads_GroupsByThreadId | Integration |
| Threads_ListThreads_RespectsLimit | Integration |
| Threads_ListThreadsByAccount_FiltersCorrectly | Integration |
| Threads_GetThreadDetail_ExistingThread_ReturnsSome | Integration |
| Threads_GetThreadDetail_Missing_ReturnsNone | Integration |
| Threads_ListThreads_IncludesParticipants | Integration |

## ExcelExtractionTests.fs (3 tests)

| Test | Category |
|------|----------|
| ExcelExtraction_ExtractExcel_SimpleSheet_ProducesContent | Unit |
| ExcelExtraction_ExtractExcel_MultipleSheets_ProducesMultiplePages | Unit |
| ExcelExtraction_ExtractExcel_EmptySheet_HandlesGracefully | Unit |

## WordExtractionTests.fs (12 tests)

| Test | Category |
|------|----------|
| WordExtraction_ExtractWord_SimpleParagraphs_ProducesContent | Unit |
| WordExtraction_ExtractWord_WithHeading_ProducesHeadingBlock | Unit |
| WordExtraction_ExtractWord_EmptyDocument_HandlesGracefully | Unit |
| WordExtraction_ExtractWord_InvalidBytes_ReturnsEmptyPages | Unit |
| WordExtraction_ExtractWord_MultipleParagraphs_AllExtracted | Unit |
| WordExtraction_ExtractWord_WithMultipleHeadings_ProducesBlocks | Unit |
| WordExtraction_ExtractWord_WithTable_ProducesTable | Unit |
| Extraction_ExtractFromBytes_Docx_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_Excel_WithInvalidBytes_ReturnsError | Unit |
| WordExtraction_ExtractWord_EmptyTable_ProducesEmptyParagraph | Unit |
| WordExtraction_ExtractWord_ValidContent_HighConfidence | Unit |
| WordExtraction_ExtractWord_EmptyContent_LowConfidence | Unit |

## OspreyParityTests.fs (10 tests)

| Test | Category |
|------|----------|
| OspreyParity_O1_MicrosoftPayslip_ExtractsRequiredFields | Integration |
| OspreyParity_O2_QldEducationPayslip_ExtractsRequiredFields | Integration |
| OspreyParity_O3_WestpacCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O4_CbaCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O5_RentalStatement_ExtractsRequiredFields | Integration |
| OspreyParity_O6_FidelityCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O7_AmazonCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O8_UtilityInvoice_ExtractsRequiredFields | Integration |
| OspreyParity_O9_CreditCardCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O10_InsuranceRenewal_ExtractsRequiredFields | ManualTest |

## DocumentTests.fs (13 tests)

| Test | Category |
|------|----------|
| Document_decode_string_returns_Some_when_present | Unit |
| Document_decode_returns_None_for_missing_key | Unit |
| Document_decode_returns_None_for_DBNull | Unit |
| Document_decode_returns_None_for_null | Unit |
| Document_decode_int64_from_int64 | Unit |
| Document_encode_adds_key | Unit |
| Document_encode_overwrites_key | Unit |
| Document_hasKey_true_for_present_value | Unit |
| Document_hasKey_false_for_DBNull | Unit |
| Document_hasKey_false_for_missing | Unit |
| Document_id_returns_id_field | Unit |
| Document_stage_returns_default_when_missing | Unit |
| Document_fromRow_is_identity | Unit |

## WorkflowTests.fs (4 tests)

| Test | Category |
|------|----------|
| Workflow_runStage_processes_document_and_forwards | Unit |
| Workflow_runStage_passthrough_when_already_done | Unit |
| Workflow_runStage_passthrough_when_missing_required_keys | Unit |
| Workflow_runStage_marks_failed_on_exception | Unit |

## PromptLoaderTests.fs (26 tests)

| Test | Category |
|------|----------|
| PromptLoader_Parse_ValidContent_ReturnsBothSections | Unit |
| PromptLoader_Parse_MissingSystemDelimiter_ReturnsError | Unit |
| PromptLoader_Parse_MissingUserDelimiter_ReturnsError | Unit |
| PromptLoader_Parse_EmptySystemSection_ReturnsError | Unit |
| PromptLoader_Parse_EmptyUserSection_ReturnsError | Unit |
| PromptLoader_Render_SubstitutesTemplateMarkers | Unit |
| PromptLoader_Render_TruncatesLongText | Unit |
| PromptLoader_LoadFromFile_FileExists_ParsesContent | Unit |
| PromptLoader_LoadFromFile_FileMissing_ReturnsError | Unit |
| PromptLoader_LoadWithFallback_ConfigDirExists_UsesConfigDir | Unit |
| PromptLoader_LoadWithFallback_OnlyAssemblyDir_UsesFallback | Unit |
| PromptLoader_LoadWithFallback_NeitherExists_ReturnsError | Unit |
| ComprehensionSchema_NormaliseCategory_KnownType_MapsCorrectly | Unit |
| ComprehensionSchema_NormaliseCategory_Alias_MapsCorrectly | Unit |
| ComprehensionSchema_NormaliseCategory_Unknown_ReturnsUnclassified | Unit |
| ComprehensionSchema_NormaliseCategory_CaseInsensitive | Unit |
| ComprehensionSchema_NormaliseResponse_ValidJson_ReturnsNormalised | Unit |
| ComprehensionSchema_NormaliseResponse_WithCodeFences_StripsThem | Unit |
| ComprehensionSchema_NormaliseResponse_InvalidJson_ReturnsError | Unit |
| ComprehensionSchema_NormaliseResponse_ConfidenceOutOfRange_Clamped | Unit |

## SenderClassificationTests.fs (19 tests)

| Test | Category |
|------|----------|
| SenderClassification_extractDomain_ValidEmail_ReturnsDomain | Unit |
| SenderClassification_extractDomain_NoAtSign_ReturnsInputLowered | Unit |
| SenderClassification_extractDomain_EmptyString_ReturnsEmpty | Unit |
| SenderClassification_extractDomain_AngleBracketFormat_ExtractsDomain | Unit |
| SenderClassification_classify_KnownBankDomain_ReturnsBank | Unit |
| SenderClassification_classify_KnownPropertyManager_ReturnsPropertyManager | Unit |
| SenderClassification_classify_KnownGovernment_ReturnsGovernment | Unit |
| SenderClassification_classify_KnownUtility_ReturnsUtility | Unit |
| SenderClassification_classify_KnownInsurance_ReturnsInsurance | Unit |
| SenderClassification_classify_KnownEmployer_ReturnsEmployer | Unit |
| SenderClassification_classify_UnknownDomain_FallsBackToDisplayName | Unit |
| SenderClassification_classify_UnknownBoth_ReturnsUnknown | Unit |
| SenderClassification_classify_EmptyInput_ReturnsUnknown | Unit |
| SenderClassification_classify_WhitespaceOnly_ReturnsUnknown | Unit |
| SenderClassification_classify_CaseInsensitiveDomain_Matches | Unit |
| SenderClassification_classify_SubdomainMatch_Matches | Unit |
| SenderClassification_formatHint_Bank_FormatsCorrectly | Unit |
| SenderClassification_formatHint_Unknown_ReturnsEmpty | Unit |
| SenderClassification_formatHint_Utility_IncludesTypeAndLabel | Unit |

## DeepExtractionTests.fs (27 tests)

| Test | Category |
|------|----------|
| DeepExtraction_promptFileForType_Payslip_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_PayrollStatementAlias_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_AgentStatement_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_RentalStatementAlias_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_BankStatement_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_CreditCardAlias_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_Unknown_ReturnsNone | Unit |
| DeepExtraction_computeHash_SameInput_SameOutput | Unit |
| DeepExtraction_computeHash_DifferentInput_DifferentOutput | Unit |
| DeepExtraction_computeHash_Returns16Chars | Unit |
| DeepExtraction_mergeIntoComprehension_AddsDeepExtraction | Unit |
| DeepExtraction_mergeIntoComprehension_InvalidJson_ReturnsError | Unit |
| DeepExtraction_hasValidDeepExtraction_MatchingHash_ReturnsTrue | Unit |
| DeepExtraction_hasValidDeepExtraction_DifferentHash_ReturnsFalse | Unit |
| DeepExtraction_hasValidDeepExtraction_NoDeepExtraction_ReturnsFalse | Unit |
| DeepExtraction_getDocumentType_Present_ReturnsSome | Unit |
| DeepExtraction_getDocumentType_Missing_ReturnsNone | Unit |
| DeepExtraction_getDocumentType_InvalidJson_ReturnsNone | Unit |
| DeepExtraction_extract_ValidPayslip_ReturnsOkWithMetadata | Unit |
| DeepExtraction_extract_UnsupportedType_ReturnsError | Unit |
| DeepExtraction_extract_MissingFromRegistry_ReturnsError | Unit |
| DeepExtraction_extract_ChatFailure_ReturnsError | Unit |
| DeepExtraction_extract_CodeFencedJson_StripsAndSucceeds | Unit |
| McpTools_deepExtract_ValidDocument_ReturnsMergedResult | Integration |
| McpTools_deepExtract_MissingDocument_ReturnsError | Integration |
| McpTools_deepExtract_NoComprehension_ReturnsError | Integration |
| McpTools_deepExtract_MissingDocumentId_ReturnsError | Integration |

## ContactExtractionTests.fs (24 tests)

| Test | Category |
|------|----------|
| ContactExtraction_normaliseName_PtyLtd_Stripped | Unit |
| ContactExtraction_normaliseName_PtyDotLtdDot_Stripped | Unit |
| ContactExtraction_normaliseName_Inc_Stripped | Unit |
| ContactExtraction_normaliseName_Limited_Stripped | Unit |
| ContactExtraction_normaliseName_Quotes_Stripped | Unit |
| ContactExtraction_normaliseName_PlainName_Lowered | Unit |
| ContactExtraction_normaliseName_WhitespacePreservedInMiddle | Unit |
| ContactExtraction_computeContactId_SameInput_SameOutput | Unit |
| ContactExtraction_computeContactId_DifferentAbn_DifferentId | Unit |
| ContactExtraction_computeContactId_WithAbn_IncludesAbn | Unit |
| ContactExtraction_computeContactId_Returns16Chars | Unit |
| ContactExtraction_contactTypeFromSender_Bank_Supplier | Unit |
| ContactExtraction_contactTypeFromSender_Employer_Employer | Unit |
| ContactExtraction_contactTypeFromSender_Government_Government | Unit |
| ContactExtraction_contactTypeFromSender_Unknown_Unknown | Unit |
| ContactExtraction_harvestFromComprehension_TopLevelSenderName_ExtractsContact | Unit |
| ContactExtraction_harvestFromComprehension_NestedFields_ExtractsContact | Unit |
| ContactExtraction_harvestFromComprehension_NoName_ReturnsNone | Unit |
| ContactExtraction_harvestFromComprehension_InvalidJson_ReturnsNone | Unit |
| ContactExtraction_harvestFromComprehension_AngleBracketSender_ExtractsEmail | Unit |
| ContactExtraction_harvestFromComprehension_AbnFromTopLevel_Extracted | Unit |
| ContactExtraction_harvestAndLink_InsertsContactAndLinks | Integration |
| ContactExtraction_harvestAndLink_DuplicateContact_UpdatesLastSeen | Integration |
| ContactExtraction_harvestAndLink_NoName_NoContact | Integration |

## McpContactTests.fs (8 tests)

| Test | Category |
|------|----------|
| McpServer_ContactsBackfill_CreatesContacts | Integration |
| McpServer_ContactsList_ReturnsContacts | Integration |
| McpServer_ContactsList_FilterByQuery | Integration |
| McpServer_ContactsList_FilterByContactType | Integration |
| McpServer_ContactDetail_ReturnsWithDocuments | Integration |
| McpServer_ContactDetail_NotFound_ReturnsError | Integration |
| McpServer_ContactSetTaxRelevant_Updates | Integration |
| McpServer_ContactSetTaxRelevant_NotFound_ReturnsError | Integration |

## PptxExtractionTests.fs (10 tests)

| Test | Category |
|------|----------|
| PptxExtraction_ExtractPptx_InvalidBytes_ReturnsEmptyWithZeroConfidence | Unit |
| PptxExtraction_ExtractPptx_EmptyBytes_ReturnsEmptyWithZeroConfidence | Unit |
| Extraction_IsPptx_ReturnsTrue_ForPptxExtension | Unit |
| Extraction_IsPptx_ReturnsFalse_ForOtherExtensions | Unit |
| PptxExtraction_ExtractPptx_SimpleSlide_ExtractsText | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_CorrectPageCount | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_ExtractsSlideText | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_ExtractsTable | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_ExtractsSpeakerNotes | Integration |
| PptxExtraction_ExtractPptx_SimpleSlide_HighConfidence | Integration |

## OutlookProviderTests.fs (12 tests)

| Test | Category |
|------|----------|
| Classifier_ParseSidecar_ProviderIdField_ParsesProviderId | Unit |
| Classifier_ParseSidecar_LegacyGmailId_FallsBackToProviderId | Unit |
| Classifier_ParseSidecar_BothProviderIdAndGmailId_ProviderIdWins | Unit |
| Config_ParseYaml_OutlookAccount_ParsesClientAndTenant | Unit |
| Config_ParseYaml_OutlookAccount_DefaultTenantId | Unit |
| Config_ParseYaml_OutlookAccount_DefaultRedirectPort | Unit |
| Extraction_IsPptx_DetectsCorrectly | Unit |

## ComprehensionRacTests.fs (12 tests)

| Test | Category |
|------|----------|
| Stages_ExtractSenderDomain_ValidEmail_ReturnsDomain | Unit |
| Stages_ExtractSenderDomain_NoAt_ReturnsNone | Unit |
| Stages_ExtractSenderDomain_AngleBrackets_StripsThem | Unit |
| Stages_CompactSchemaHint_ValidJson_ReturnsTypeAndFieldNames | Unit |
| Stages_CompactSchemaHint_NoFields_ReturnsTypeOnly | Unit |
| Stages_CompactSchemaHint_InvalidJson_ReturnsNone | Unit |
| Stages_CompactSchemaHint_EmptyFields_ReturnsEmptyArray | Unit |
| Stages_CompactSchemaHint_CapsAt300Chars | Unit |

## ArchiveWriterTests.fs (28 tests)

| Test | Category |
|------|----------|
| ArchiveWriter_Slugify_NormalText_ReturnsSlug | Unit |
| ArchiveWriter_Slugify_EmptyOrWhitespace_ReturnsUntitled | Unit |
| ArchiveWriter_Slugify_LongText_TruncatesAt60Chars | Unit |
| ArchiveWriter_ExtractSenderDomain_ValidEmail_ReturnsDomain | Unit |
| ArchiveWriter_ExtractSenderDomain_NoEmail_ReturnsUnknown | Unit |
| ArchiveWriter_ThreadFolderPath_BuildsCorrectPath | Unit |
| ArchiveWriter_ThreadFolderPath_DifferentThreadIds_DifferentPaths | Unit |
| ArchiveWriter_LocalFolderPath_BuildsCorrectPath | Unit |
| ArchiveWriter_MessageFileName_IncludesDateAndSlug | Unit |
| ArchiveWriter_AttachmentFileName_PreservesExtension | Unit |
| ArchiveWriter_AttachmentFileName_HandlesDotInName | Unit |
| ArchiveWriter_AttachmentFileName_DifferentHashes_DifferentNames | Unit |
| ArchiveWriter_WriteMessage_CreatesFile | Unit |
| ArchiveWriter_WriteAttachment_CreatesFile | Unit |
| ArchiveWriter_WriteExtraction_CreatesSidecarFile | Unit |
| ArchiveWriter_WriteComprehension_CreatesThreadJson | Unit |
| ArchiveWriter_WriteSidecar_CreatesHermesJson | Unit |
| ArchiveWriter_ReadExtraction_ReturnsNone_WhenNotExists | Unit |
| ArchiveWriter_ReadComprehension_ReturnsNone_WhenNotExists | Unit |
| ArchiveWriter_ReadExtraction_ReturnsContent_WhenExists | Unit |
