"""Populate only the disposable archive created by the browser fixture."""
from pathlib import Path
import hashlib
import os
import sqlite3
import sys

archive = Path(os.path.abspath(sys.argv[1]))
root = archive.parent
if not root.name.startswith("hermes-browser-") or not (root / ".synthetic-smoke").is_file():
    raise RuntimeError("Refusing to seed an archive not owned by the synthetic fixture")

source = archive / "synthetic-payslip.md"
body = b"# Synthetic payslip\n\nQuartz salary evidence for browser smoke only.\n"
source.write_bytes(body)
Path(str(source) + ".extracted.md").write_bytes(body)
with sqlite3.connect(archive / "db.sqlite", timeout=10) as db:
    def synthetic_path_key(value):
        if value != str(source):
            raise RuntimeError("Unexpected source identity in synthetic seed")
        return str(source).upper()

    # The connection must supply the same authority key used by the guarded writer.
    db.create_function("hermes_saved_path_key", 1, synthetic_path_key)
    if db.execute("SELECT COUNT(*) FROM documents").fetchone()[0] != 0:
        raise RuntimeError("Synthetic fixture database is not empty")
    db.execute(
        """INSERT INTO documents
        (id,original_name,source_type,saved_path,sha256,category,subject,
         extracted_date,extracted_amount,extracted_vendor,classification_tier,
         classification_confidence,stage,extracted_at,embedded_at)
        VALUES (1,'synthetic-payslip.md','email_body',?,?, 'payslips',
                'Quartz synthetic salary','2026-07-01',1234.56,'Synthetic Employer',
                'manual',1,'embedded',datetime('now'),datetime('now'))""",
        (str(source), hashlib.sha256(body).hexdigest()),
    )
    db.executemany(
        "INSERT INTO stage_completions(document_id,stage_name) VALUES (1,?)",
        [(stage,) for stage in ("extract", "triage", "deep-comprehend", "embed")],
    )
    db.execute("INSERT INTO extraction(document_id,method,confidence) VALUES (1,'synthetic',1)")
