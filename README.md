# AutoSize Studio (in development)

AutoSize Studio is the next evolution of the AutoSize project. After extensive work with WIA (Windows Image Acquisition) drivers we hit a hard limit: too many scanners—especially WSD and older network devices—ship only preview-oriented WIA mini-drivers that ignore crop/extent commands. Rather than ship a brittle experience, we are pivoting the product from *driving scanners* to *polishing the scans you already have*.

The new vision keeps the beloved “auto-size” philosophy but applies it to existing photo libraries, particularly aging family archives. AutoSize Studio will ingest images from disk, automatically isolate the photograph from the flatbed background, and layer on modern restoration tooling (auto-enhance, color recovery, upscaling, denoise, retouch) completely offline.

---

## Why the pivot?

* **WIA driver fragmentation** – Many BROTHER/Canon/Epson WSD drivers only expose thumbnail preview items via WIA, rejecting our attempts to select the full-bed extent. Resetting drivers through manufacturer utilities worked sporadically and required manual intervention.
* **TWAIN-only devices** – A large portion of legacy scanners never shipped WIA drivers. Supporting them would require a parallel TWAIN stack and 32-bit hosting, increasing app complexity.
* **User feedback** – Most early testers already had images saved (from kiosks, labs, phone photos of prints) and primarily wanted quick border removal, straightening, and cleanup rather than a new scanning UI.

Given those hurdles, AutoSize Studio focuses on the universal problem: making imperfect scans look like freshly digitised originals.

---

## Planned feature set

| Area | Highlights |
|------|------------|
| **Import & Library** | Drag/drop folders or individual files, duplicate detection, EXIF ingest, batch selection, watch-folder automation |
| **Smart Cropping** | Auto-detect photo vs. flatbed background, adjustable bed-mask overlays, aspect presets, manual crop handles |
| **Restoration Suite (offline AI)** | Auto enhance, color-cast correction, sepia fade recovery, exposure/contrast leveling, denoise, dehaze, face-aware tone fixes, super-resolution upscaling, OCR sidecar export |
| **Retouch Tools** | Spot heal, clone/repair, dust & scratch removal, edge-aware smoothing, local adjustments |
| **Workspace UX** | Multi-image queue, before/after slider, histogram & tone curve, zoom/rotate, keyboard shortcuts, undo/redo history |
| **Batch Automation** | Queue HUD, pause/resume, preset pipelines (e.g. “Polaroid cleanup”), session restore, contact-sheet export |
| **Export** | JPEG/PNG/TIFF/PDF, naming templates, destination profiles, clipboard copy, optional integrations |

All “AI” capabilities are delivered via bundled ONNX/ML.NET models so the app runs fully offline—no image is ever uploaded to the cloud.

---

## Technology stack

- .NET 8
- WPF desktop UI
- ImageSharp + custom pixel shaders for classical processing
- ML.NET / ONNX Runtime (DirectML) for on-device AI helpers

Legacy WIA scanning code remains in the repo for historical reference, but the primary application will transition to this new workflow.

---

## Next steps

1. Finalise UI mocks for the library & workspace screens
2. Stand up the modular processing pipeline with undo/redo support
3. Integrate first round of enhancement models (auto-crop + color restore)
4. Ship preview builds focused on batch border cleanup

We welcome feedback—especially sample scans from aging photo albums—to ensure AutoSize Studio truly bridges the gap left by legacy flatbed software.
