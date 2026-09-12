# Phase 2.75.7 Accessibility and Bilingual Audit

**Date:** 12 September 2026

## Implemented

- Skip-to-content link is the first focusable element in the shell and targets `main`.
- Focus-visible styling covers native controls, links, and custom tabindex elements.
- Live status utilities use `aria-live="polite"`; assertive announcements are not used.
- `sr-only` and `qa-greyscale` utilities are available for screen-reader and colour-independence checks.
- Interactive control borders use `--border-interactive` for the 3:1 UI boundary requirement.
- Light live-state text uses `#a86400`; dark state tokens retain the audited contrast values.
- Bengali and English line-height rules are kept separate, and the console uses Western numerals consistently.
- Stage charts include persistent stage labels so grayscale mode does not rely on colour alone.

## Code Checks

| Check | Result |
|---|---|
| Angular production build | Passed |
| Type/template diagnostics | Passed |
| Skip-link shell integration | Implemented |
| Table `scope="col"` and caption | Implemented in `ui-data-table` |
| Polite live regions | Implemented in realtime status component |
| Reduced motion | Implemented in global tokens |
| Grayscale utility | Implemented; manual visual verification remains |

## Remaining Manual QA

- Verify WCAG 2.2 AA ratios in both themes with a contrast tool.
- Traverse all shipped screens with keyboard and confirm focus return from overlays.
- Exercise modal/drawer focus trapping after those components are implemented with CDK.
- Verify a mixed English/Bengali row at all table densities without visual jitter.
- Run the grayscale check against every screen and confirm state labels remain understandable.
- Run the full application accessibility audit after the `/design` route is delivered in Task 2.75.8.