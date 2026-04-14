# 01 — Vision & Principles

## Mission

Hermes is a local-first document intelligence service for households. It continuously ingests documents from email and local folders, understands them through LLM comprehension, and makes that knowledge available to humans (via web UI) and AI agents (via MCP server).

## Who it's for

Non-technical users — "your dad." Hermes runs silently in the background. Documents appear, get understood, and become searchable without any manual filing, renaming, or organizing.

## Design Principles

1. **Understand, don't label.** Comprehension produces structured knowledge. Classification is a byproduct, not a goal. The system doesn't put documents in folders — it reads them and remembers what they say.

2. **Files are immutable.** Once a document is ingested, its file never moves. `saved_path` is set at ingest and never changes. Category, type, and understanding are metadata — not filesystem operations.

3. **Property bags over rigid schemas.** Documents are `Map<string, obj>`. Stages add keys. The system doesn't prescribe what a document looks like — it discovers it through comprehension.

4. **Self-organizing pipeline.** No orchestrator, no scheduler, no external coordinator. Channels flow naturally. Resource locks (GPU) use demand-driven semaphores. Hydration on restart re-seeds the pipeline. Consumers are idempotent.

5. **Local-first, private by default.** All processing runs on the user's machine. Ollama for LLM. SQLite for storage. No cloud dependency for core functionality. Documents never leave the device.

6. **Consumers query, Hermes serves.** Osprey (tax), agents, and the web UI are all consumers of the same MCP/HTTP interface. Hermes doesn't know about tax — it knows about documents. Osprey knows about tax — it asks Hermes for payslips.

7. **Learn by doing.** Each comprehended document is an example for future similar documents. The system gets better over time without fine-tuning — it retrieves past comprehensions as few-shot examples.

## Goals

| ID | Goal | Measured by |
|----|------|-------------|
| G1 | Ingest every document the household receives | Email sync + folder watching running continuously |
| G2 | Understand every document | Comprehension JSON with document_type + fields for >80% of docs |
| G3 | Answer questions about documents | MCP search returns relevant results; Chat produces useful answers |
| G4 | Support tax preparation | Osprey can query Hermes for all tax-relevant docs with structured data |
| G5 | Zero manual filing | No user action required for documents to be ingested, understood, and searchable |
