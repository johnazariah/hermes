export interface PipelineState {
  ingestQueueDepth: number;
  extractQueueDepth: number;
  deadLetterCount: number;
  totalDocuments: number;
  totalExtracted: number;
  totalEmbedded: number;
  currentDoc: string | null;
  lastUpdated: string;
}

export interface CategoryCount {
  category: string;
  count: number;
}

export interface DocumentSummary {
  id: number;
  originalName: string;
  category: string;
  extractedDate: string | null;
  extractedAmount: number | null;
  sender: string | null;
  vendor: string | null;
  sourceType: string | null;
  account: string | null;
  sourcePath: string | null;
  classificationTier: string | null;
  classificationConfidence: number | null;
}

export interface DocumentDetail {
  summary: DocumentSummary;
  extractedText: string | null;
  comprehension: string | null;
  filePath: string;
  vendor: string | null;
  ingestedAt: string;
  extractedAt: string | null;
  embeddedAt: string | null;
  pipelineStatus: {
    understood: boolean;
    extracted: boolean;
    embedded: boolean;
  };
}

export interface IndexStats {
  documentCount: number;
  extractedCount: number;
  understoodCount: number;
  embeddedCount: number;
  awaitingExtract: number;
  awaitingUnderstand: number;
  awaitingEmbed: number;
  databaseSizeMb: number;
}

export interface ReminderItem {
  id: number;
  documentId: number;
  vendor: string | null;
  amount: number | null;
  dueDate: string | null;
  category: string;
  status: string;
  fileName: string | null;
}
