# Phase 2.75 Design QA

Open `/design` after signing in. The route is the component catalogue for Phase 2.75.2 through
2.75.8 and is deliberately backed by the real Angular components, not static mockups.

## Checklist

- [ ] Toggle Light and Dark; verify page, controls, charts, table, and states remain readable.
- [ ] Toggle Compact, Default, and Comfortable; confirm table rows remain stable and usable.
- [ ] Tab from the browser chrome: the shell skip link must be first, and every control must show focus.
- [ ] Confirm the table has a screen-reader caption and `scope="col"` headers.
- [ ] Scroll the generated 10,000-row table; rows must be virtualized by CDK.
- [ ] Confirm the stage chart still identifies STT, Endpoint, Routing, LLM, TTS, and RTP in grayscale.
- [ ] Confirm the empty and not-enough-data chart states are distinct.
- [ ] Use a mixed English/Bengali row and check that density remains stable.
- [ ] Enable `prefers-reduced-motion` and confirm no required meaning depends on animation.
- [ ] Run a contrast checker for both themes, especially interactive borders and live state text.

## Automated Check

```powershell
Push-Location frontend
npm run build -- --configuration production
Pop-Location
```

The route is authenticated with the existing shell guard. This task does not claim that manual
contrast, keyboard, grayscale, or 60 fps measurements have been performed in a browser.