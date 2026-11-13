# Chrome extension 2.3.6 — 2026-11-13

The shared extension uses a Stitch-based white and teal menu, larger body text,
and enlarged speech-bubble icon artwork. Main headings retain their original
size. Existing settings and the published extension identity are preserved.

Validation on Windows with installed Google Chrome:

- 38 Node tests passed.
- 14 actual action-popup checks passed: five panels, 720×580 sizing, constrained
  420px width, white appearance under dark browser settings, larger text,
  setting persistence, translate/restore dispatch and cancellable cache deletion.
- Seven icon sizes and five offline-warning render sizes passed graphics checks.
- Normal, narrow and warning-icon screenshots were visually inspected.
- Distribution ZIP contents were compared byte-for-byte with extension source.

The menu ships only bundled styles, images and SVGs. It does not load the remote
scripts or fonts used by the Stitch design reference. Icon vector source is in
`chrome/artwork/myplamo.svg`; design provenance is in `.stitch/`.

The native Windows and Mac app source and binaries are unchanged by this update.
Mac-native UI and inference were not rerun on this Windows host. Shared extension
tests run in both repositories; real browser checks run on Windows.
