# Dock scenes (vision lane owns this folder)

Text-free pallet photos with known carton-face positions, so every
shipment gets dock photos no matter how many cartons its ASN carries.
Labels are drawn from the ASN at build time; the bases carry no text.

## Layout

- `scenes.json` — the scene catalog (format below).
- `bases/` — one text-free JPEG per scene. `scene-06.jpg` is the
 angle-one dock base; `scene-01/02/04.jpg` are crops of it; `scene-08/12.jpg`
  are straight-on stacks composed from the same photo's own cardboard tone,
  blurred dock background, and pallet wood. No text was drawn on any base.

## scenes.json format

```json
{"scenes": [{"id": "SCENE-06", "counts": [6], "base": "scene-06.jpg",
  "faces": [{"index": 0, "x": 0.055, "y": 0.32, "w": 0.12, "h": 0.14}],
  "closeupScale": 2}]}
```

- `id` — scene name; also the photo id for single-view shipments.
- `counts` — carton counts this scene covers exactly (its capacity).
- `base` — file name under `bases/`.
- `faces` — label boxes as fractions of image width/height, origin
  top-left, ordered by `index` (top row first, left to right). The box
  marks where the carton label is drawn.
- `closeupScale` — magnification applied when a close-up is cut around
  one face (the relabel pattern: crop, enlarge, one large label).

## Count mapping

The pipeline picks scenes from the ASN carton count:

- A count in {1, 2, 4, 6, 8, 12} uses its exact scene.
- Any other count uses the nearest-larger scene with that scene's first
  faces in ASN carton order (3 cartons use faces 0-2 of the 4-scene).
- Counts above the largest scene split into several views: the largest
  scene takes the first cartons, then the remainder maps again (13
  cartons photo as 12 plus 1). Every positive count yields photos.

## Problem rendering

A shipment may name problems per carton (`SimulatedProblem` in
`Gate.Agent/DockImagePipeline.cs`):

- `MissingMark` — the label is drawn with no lithium-battery mark.
- `Crushed` — a damage cue (creases plus a torn corner) is drawn on the
  face around the label, and the manifest requires a damage note.
- `Hidden` — no label is drawn; the face is covered so the carton
  appears in no view and no read. Hidden cannot combine with another
  problem on the same carton. A hidden carton has no close-up.

Close-ups cover every other carton id: the pipeline finds the carton's
face, cuts the surrounding crop at `closeupScale`, and draws one large
label, the same pattern as the IMG-04 relabel.

## Verification

After compositing, each photo is read back and checked field by field
against the manifest (carton id, part, PO, lot, mark, damage). A
mismatch rebuilds the photo and retries the read once; a second
mismatch fails with the carton and field named, and the caller keeps
the previous photos. The live path
(`TryBuildLiveSceneAsync`) instead returns the reason so the caller can
fall back to a library scene: it generates one text-free pallet photo
for the ASN count (model key `MetaImages:SceneModel`, defaulting to
`MetaImages:ImageModel`), locates the carton faces, draws the ASN
labels, and reads them back. Without image credentials it reports
`live scenes unavailable: MetaImages not configured`.
