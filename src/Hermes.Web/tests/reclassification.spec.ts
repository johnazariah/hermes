import { expect, test, type Page } from "@playwright/test";

const BASE = process.env.HERMES_WEB_BASE_URL ?? "http://localhost:21742";

interface MockOutcome {
    documentId: number;
    status: "reclassified" | "unchanged" | "failed";
    previousCategory: string | null;
    category: string | null;
    changed: boolean;
    savedPath: string | null;
    sha256: string | null;
    error: string | null;
}

interface MockResponse {
    action: "reclassify";
    succeeded: number;
    unchanged: number;
    failed: number;
    outcomes: MockOutcome[];
}

function document(id: number, category = "invoices") {
    return {
        id,
        originalName: `document-${id}.pdf`,
        category,
        extractedDate: "2026-08-29",
        extractedAmount: null,
        sender: null,
        vendor: null,
        sourceType: "email_attachment",
        account: null,
        sourcePath: null,
        classificationTier: "user",
        classificationConfidence: 1,
    };
}

async function mockDocumentsApi(
    page: Page,
    options: {
        documents?: ReturnType<typeof document>[];
        documentsAfterChange?: ReturnType<typeof document>[];
        response?: MockResponse;
        error?: { status: number; detail: string };
        successError?: string;
        delayMs?: number;
        documentsByCategory?: Record<
            string,
            ReturnType<typeof document>[]
        >;
    },
) {
    const state = {
        documentReads: 0,
        mutationCompleted: false,
        requests: [] as unknown[],
    };
    const initialDocuments = options.documents ?? [document(1)];

    await page.route("**/api/**", async (route) => {
        const request = route.request();
        const url = new URL(request.url());
        if (!url.pathname.startsWith("/api/")) {
            await route.continue();
            return;
        }

        if (
            request.method() === "POST" &&
            url.pathname === "/api/documents/batch"
        ) {
            state.requests.push(request.postDataJSON());
            if (options.delayMs) {
                await new Promise((resolve) =>
                    setTimeout(resolve, options.delayMs),
                );
            }
            state.mutationCompleted = true;
            if (options.error) {
                await route.fulfill({
                    status: options.error.status,
                    contentType: "application/json",
                    body: JSON.stringify({ detail: options.error.detail }),
                });
                return;
            }
            await route.fulfill({
                status: 200,
                contentType: "application/json",
                body: JSON.stringify(
                    options.successError
                        ? { error: options.successError }
                        : options.response,
                ),
            });
            return;
        }

        if (
            request.method() === "GET" &&
            url.pathname === "/api/documents"
        ) {
            state.documentReads += 1;
            const category = url.searchParams.get("category") ?? "";
            await route.fulfill({
                contentType: "application/json",
                body: JSON.stringify(
                    state.mutationCompleted && options.documentsAfterChange
                        ? options.documentsAfterChange
                        : options.documentsByCategory?.[category] ??
                              initialDocuments,
                ),
            });
            return;
        }

        const bodies: Record<string, unknown> = {
            "/api/tags": options.documentsByCategory
                ? Object.entries(options.documentsByCategory)
                      .filter(([tag]) => tag)
                      .map(([tag, documents]) => ({
                          tag,
                          count: documents.length,
                      }))
                : [{ tag: "invoices", count: initialDocuments.length }],
            "/api/categories": [
                { category: "invoices", count: initialDocuments.length },
                { category: "receipts", count: 0 },
            ],
            "/api/stats": {
                documentCount: initialDocuments.length,
                extractedCount: initialDocuments.length,
                understoodCount: initialDocuments.length,
                embeddedCount: initialDocuments.length,
                awaitingExtract: 0,
                awaitingUnderstand: 0,
                awaitingEmbed: 0,
                databaseSizeMb: 1,
            },
            "/api/suggestions/count": { count: 0 },
            "/api/sync/accounts": [],
            "/api/preferences": {},
        };

        await route.fulfill({
            contentType: "application/json",
            body: JSON.stringify(bodies[url.pathname] ?? {}),
        });
    });

    return state;
}

async function selectDocuments(page: Page, ids: number[]) {
    for (const id of ids) {
        await page
            .getByRole("checkbox", { name: `Select document-${id}.pdf` })
            .check();
    }
}

test.describe("document reclassification", () => {
    test("sends the explicit category and refreshes changed documents", async ({
        page,
    }) => {
        const state = await mockDocumentsApi(page, {
            documentsAfterChange: [document(1, "receipts")],
            response: {
                action: "reclassify",
                succeeded: 1,
                unchanged: 0,
                failed: 0,
                outcomes: [
                    {
                        documentId: 1,
                        status: "reclassified",
                        previousCategory: "invoices",
                        category: "receipts",
                        changed: true,
                        savedPath: "receipts/document-1.pdf",
                        sha256: "abc",
                        error: null,
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents`);
        await selectDocuments(page, [1]);
        const readsBeforeMutation = state.documentReads;

        await page.getByLabel("Category:").fill("receipts");
        await page.getByRole("button", { name: "Reclassify" }).click();

        await expect(
            page.getByText("Reclassified 1 document to “receipts”."),
        ).toBeVisible();
        expect(state.requests).toEqual([
            { docIds: [1], action: "reclassify", value: "receipts" },
        ]);
        await expect(page.getByText("receipts", { exact: true })).toBeVisible();
        await expect.poll(() => state.documentReads).toBeGreaterThan(
            readsBeforeMutation,
        );
    });

    test("renders an idempotent no-op without refreshing document data", async ({
        page,
    }) => {
        const state = await mockDocumentsApi(page, {
            response: {
                action: "reclassify",
                succeeded: 0,
                unchanged: 1,
                failed: 0,
                outcomes: [
                    {
                        documentId: 1,
                        status: "unchanged",
                        previousCategory: "invoices",
                        category: "invoices",
                        changed: false,
                        savedPath: null,
                        sha256: null,
                        error: null,
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents`);
        await selectDocuments(page, [1]);
        const readsBeforeMutation = state.documentReads;

        await page.getByLabel("Category:").fill("invoices");
        await page.getByRole("button", { name: "Reclassify" }).click();

        await expect(
            page.getByText(
                "No changes: 1 document was already categorized as “invoices”.",
            ),
        ).toBeVisible();
        await expect(
            page.getByText(
                "Document 1: already categorized as “invoices”",
            ),
        ).toBeVisible();
        expect(state.documentReads).toBe(readsBeforeMutation);
    });

    test("renders changed, unchanged, and domain failures as a partial result", async ({
        page,
    }) => {
        await mockDocumentsApi(page, {
            documents: [document(1), document(2), document(3)],
            documentsAfterChange: [
                document(1, "receipts"),
                document(2, "receipts"),
                document(3),
            ],
            response: {
                action: "reclassify",
                succeeded: 1,
                unchanged: 1,
                failed: 1,
                outcomes: [
                    {
                        documentId: 1,
                        status: "reclassified",
                        previousCategory: "invoices",
                        category: "receipts",
                        changed: true,
                        savedPath: "receipts/document-1.pdf",
                        sha256: "abc",
                        error: null,
                    },
                    {
                        documentId: 2,
                        status: "unchanged",
                        previousCategory: "receipts",
                        category: "receipts",
                        changed: false,
                        savedPath: null,
                        sha256: null,
                        error: null,
                    },
                    {
                        documentId: 3,
                        status: "failed",
                        previousCategory: "invoices",
                        category: null,
                        changed: false,
                        savedPath: null,
                        sha256: null,
                        error: "Archive file is unavailable",
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents`);
        await selectDocuments(page, [1, 2, 3]);

        await page.getByLabel("Category:").fill("receipts");
        await page.getByRole("button", { name: "Reclassify" }).click();

        await expect(
            page.getByText(
                "Reclassification partially completed: 1 changed, 1 unchanged, 1 failed.",
            ),
        ).toBeVisible();
        await expect(
            page.getByText("Document 3: Archive file is unavailable"),
        ).toBeVisible();
        await expect(page.getByText("1 selected", { exact: true })).toBeVisible();
    });

    test("keeps the selection and renders transport errors", async ({ page }) => {
        await mockDocumentsApi(page, {
            error: {
                status: 503,
                detail: "Classifier temporarily unavailable",
            },
        });
        await page.goto(`${BASE}/documents`);
        await selectDocuments(page, [1]);

        await page.getByLabel("Category:").fill("receipts");
        await page.getByRole("button", { name: "Reclassify" }).click();

        await expect(
            page.getByText(
                "Reclassification failed: Classifier temporarily unavailable (HTTP 503)",
            ),
        ).toBeVisible();
        await expect(
            page.getByRole("checkbox", { name: "Select document-1.pdf" }),
        ).toBeChecked();
    });

    test("renders the service error from an HTTP 200 error payload", async ({
        page,
    }) => {
        await mockDocumentsApi(page, {
            successError: "Archive database is read-only",
        });
        await page.goto(`${BASE}/documents`);
        await selectDocuments(page, [1]);

        await page.getByLabel("Category:").fill("receipts");
        await page.getByRole("button", { name: "Reclassify" }).click();

        await expect(
            page.getByRole("alert").filter({
                hasText:
                    "Reclassification failed: Archive database is read-only",
            }),
        ).toBeVisible();
        await expect(page.getByRole("alert")).not.toContainText("TypeError");
        await expect(
            page.getByRole("checkbox", { name: "Select document-1.pdf" }),
        ).toBeChecked();
    });

    test("validates category input and prevents duplicate activation while pending", async ({
        page,
    }) => {
        const state = await mockDocumentsApi(page, {
            delayMs: 400,
            response: {
                action: "reclassify",
                succeeded: 1,
                unchanged: 0,
                failed: 0,
                outcomes: [
                    {
                        documentId: 1,
                        status: "reclassified",
                        previousCategory: "invoices",
                        category: "receipts",
                        changed: true,
                        savedPath: "receipts/document-1.pdf",
                        sha256: "abc",
                        error: null,
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents`);
        await expect(
            page.getByRole("button", { name: "Reclassify" }),
        ).toHaveCount(0);
        await selectDocuments(page, [1]);

        const button = page.getByRole("button", { name: "Reclassify" });
        await expect(button).toBeDisabled();
        await page.getByLabel("Category:").fill("   ");
        await expect(button).toBeDisabled();
        await page.getByLabel("Category:").fill("receipts");
        await button.click();

        const pendingButton = page.getByRole("button", {
            name: "Reclassifying…",
        });
        await expect(pendingButton).toBeDisabled();
        await expect(page.getByLabel("Category:")).toBeDisabled();
        await expect(
            page.getByRole("checkbox", { name: "Select document-1.pdf" }),
        ).toBeDisabled();
        expect(state.requests).toHaveLength(1);
        await expect(
            page.getByText("Reclassified 1 document to “receipts”."),
        ).toBeVisible();
    });

    test("prevents filter navigation from creating stale selection while pending", async ({
        page,
    }) => {
        await mockDocumentsApi(page, {
            delayMs: 1000,
            response: {
                action: "reclassify",
                succeeded: 0,
                unchanged: 0,
                failed: 1,
                outcomes: [
                    {
                        documentId: 1,
                        status: "failed",
                        previousCategory: "invoices",
                        category: null,
                        changed: false,
                        savedPath: null,
                        sha256: null,
                        error: "Archive file is unavailable",
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents`);
        await selectDocuments(page, [1]);

        await page.getByLabel("Category:").fill("receipts");
        await page.getByRole("button", { name: "Reclassify" }).click();

        const allFilter = page.getByRole("button", { name: /^All/ });
        const invoicesFilter = page.getByRole("button", { name: /^invoices/ });
        await expect(allFilter).toBeDisabled();
        await expect(invoicesFilter).toBeDisabled();
        await invoicesFilter.evaluate((button: HTMLButtonElement) =>
            button.click(),
        );
        await expect(page).toHaveURL(`${BASE}/documents`);

        await page.setViewportSize({ width: 500, height: 800 });
        await expect(page.locator("select")).toBeDisabled();

        await expect(
            page.getByText("Reclassification failed for 1 document."),
        ).toBeVisible();
        await page.setViewportSize({ width: 1280, height: 720 });
        await invoicesFilter.click();
        await expect(page).toHaveURL(`${BASE}/documents?tag=invoices`);
        await expect(
            page.getByRole("checkbox", { name: "Select document-1.pdf" }),
        ).not.toBeChecked();
        await expect(page.getByText("1 selected", { exact: true })).toHaveCount(
            0,
        );
    });

    test("clears failed selection when browser history changes the active filter", async ({
        page,
    }) => {
        await mockDocumentsApi(page, {
            delayMs: 1000,
            documentsByCategory: {
                invoices: [document(1)],
                receipts: [document(2, "receipts")],
            },
            response: {
                action: "reclassify",
                succeeded: 0,
                unchanged: 0,
                failed: 1,
                outcomes: [
                    {
                        documentId: 1,
                        status: "failed",
                        previousCategory: "invoices",
                        category: null,
                        changed: false,
                        savedPath: null,
                        sha256: null,
                        error: "Archive file is unavailable",
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents?tag=receipts`);
        await page.getByRole("button", { name: /^invoices/ }).click();
        await expect(page).toHaveURL(`${BASE}/documents?tag=invoices`);
        await selectDocuments(page, [1]);

        await page.getByLabel("Category:").fill("receipts");
        await page.getByRole("button", { name: "Reclassify" }).click();
        await expect(
            page.getByRole("button", { name: /^receipts/ }),
        ).toBeDisabled();

        await page.goBack();
        await expect(page).toHaveURL(`${BASE}/documents?tag=receipts`);
        await expect(
            page.getByRole("checkbox", { name: "Select document-2.pdf" }),
        ).toBeVisible();
        await expect(
            page.getByText("Reclassification failed for 1 document."),
        ).toBeVisible();
        await expect(page.getByText("1 selected", { exact: true })).toHaveCount(
            0,
        );
        await expect(
            page.getByRole("button", { name: "Reclassify" }),
        ).toHaveCount(0);

        await page.goForward();
        await expect(page).toHaveURL(`${BASE}/documents?tag=invoices`);
        await expect(
            page.getByRole("checkbox", { name: "Select document-1.pdf" }),
        ).not.toBeChecked();
        await expect(page.getByText("1 selected", { exact: true })).toHaveCount(
            0,
        );
    });

    test("clears ordinary selection across browser filter history", async ({
        page,
    }) => {
        const state = await mockDocumentsApi(page, {
            documentsByCategory: {
                invoices: [document(1)],
                receipts: [document(2, "receipts")],
            },
            response: {
                action: "reclassify",
                succeeded: 1,
                unchanged: 0,
                failed: 0,
                outcomes: [
                    {
                        documentId: 2,
                        status: "reclassified",
                        previousCategory: "receipts",
                        category: "invoices",
                        changed: true,
                        savedPath: "invoices/document-2.pdf",
                        sha256: "def",
                        error: null,
                    },
                ],
            },
        });
        await page.goto(`${BASE}/documents?tag=receipts`);
        await page.getByRole("button", { name: /^invoices/ }).click();
        await expect(page).toHaveURL(`${BASE}/documents?tag=invoices`);
        await selectDocuments(page, [1]);
        await expect(page.getByText("1 selected", { exact: true })).toBeVisible();

        await page.goBack();
        await expect(page).toHaveURL(`${BASE}/documents?tag=receipts`);
        await expect(
            page.getByRole("checkbox", { name: "Select document-2.pdf" }),
        ).not.toBeChecked();
        await expect(page.getByText("1 selected", { exact: true })).toHaveCount(
            0,
        );
        await expect(
            page.getByRole("button", { name: "Reclassify" }),
        ).toHaveCount(0);

        await page.goForward();
        await expect(page).toHaveURL(`${BASE}/documents?tag=invoices`);
        await expect(
            page.getByRole("checkbox", { name: "Select document-1.pdf" }),
        ).not.toBeChecked();
        await page.goBack();
        await selectDocuments(page, [2]);
        await page.getByLabel("Category:").fill("invoices");
        await page.getByRole("button", { name: "Reclassify" }).click();

        await expect(
            page.getByText("Reclassified 1 document to “invoices”."),
        ).toBeVisible();
        expect(state.requests).toEqual([
            { docIds: [2], action: "reclassify", value: "invoices" },
        ]);
    });
});
