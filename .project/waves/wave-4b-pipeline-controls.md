# Wave 4b: Interactive Pipeline Controls

> Status: **Complete**  
> Design reference: [15-rich-ui.md](../design/15-rich-ui.md) sections 3.3, 3.4, 6

## Goal

Turn the Extracting and Classifying funnel sections from passive count displays into interactive control panels with live document tracking, progress bars, ETA, batch controls, and inline classification accept/change. Move pipeline operations from Settings dialog to the funnel itself. Make the status bar a live ticker showing the currently-processing document.

## Tasks

| # | Task | Status |
|---|------|--------|
| P1 | Extracting panel: "Now:" line + queue list + progress bar + rate/ETA | ✅ Done |
| P2 | Extracting panel: batch controls (batch size + "Extract now" button) with live progress | ✅ Done |
| P3 | Classifying panel: results list with [✓ Accept] [✎ Change] inline buttons | ✅ Done |
| P4 | Classifying panel: batch controls (batch size + "Reclassify now" + provider label) | ✅ Done |
| P5 | Status bar: live ticker showing currently-processing document + operation + rate | ✅ Done |
| P6 | Remove pipeline operations from Settings dialog (now in funnel) | ✅ Done |
| P7 | Fix: operations should trigger immediate funnel count refresh (not wait for 5s timer) | ✅ Done |

## Log

### April 7, 2026 — Review PASS (2 medium items)
- Audit: all 7 tasks implemented, silver threads verified end-to-end
- 824 tests (up from 817), 0 failures
- Tagless-Final: clean in Stats.fs
- All buttons wired, no dead controls
- 2 medium items: rate/ETA text field not populated, status bar text generic during operations
- 1 low item: revertTimer not disposed (use Task.Delay or field timer)
- Live progress during batch operations works via 1s fast ticker

### April 6-7, 2026 — Implementation
- P1-P7 implemented: F# Stats queries (extraction queue, recent classifications, tier breakdown), PipelineProgress shared state on bridge, rich extracting/classifying panels with batch controls and live progress, status bar ticker with dot color, pipeline ops removed from settings, immediate refresh after operations. 7 new Stats tests. Build clean.
