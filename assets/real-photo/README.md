# Real-photo label check (CTN-13)

One printable carton label for a print-and-photograph check of the dock
label reader: print it, fix it to a carton, photograph the carton, and
compare the reader transcription against `expected-read.json`.

## The label

- File: `CTN-13-label.pdf`, one page, 6 x 4 inch landscape. The card
  itself measures 5.5 x 3.5 inch; the outer 1/4 inch is a quiet margin.
- Printed lines, left side: `CTN-13` (large), `PART BAT-1`,
  `PO PO-1037`, `LOT LOT-013`, `QTY 100`, `UN3480`.
- Top right: red-hatched lithium-battery mark with `UN3480`.
- Bottom right: Class 9 diamond, solid black, matching dock label pixels
  (the compositor overpaints the yellow fill, so no numeral shows).
- Layout source: `Gate.Agent/DockLabelCompositor.cs`, method DrawLabel.
  `make-label.py` mirrors its geometry name for name (pad, markW,
  lineStep, hatch, diamond). Reader contract:
  `Gate.Agent/DockImagePipeline.cs`, ReadPrompt plus VerifyAgainstManifest.
- Regenerate: `python3 make-label.py` (standard library only). It writes
  `CTN-13-label.pdf` next to itself.

## Print log (fill in)

- Date: TODO
- Printer: TODO (make and model)
- Stock: TODO (plain paper / adhesive stock / pouch)
- Scale: 100% (not fit-to-page); confirm the card measures 5.5 x 3.5 inch
- Colour: colour (the mark must print red)
- Trim: TODO (cut along the black border / as printed)
- Fixed to carton with: TODO (tape / glue / pouch)

## Photo log (fill in)

- Date and time: TODO
- Place: TODO (dock door / bay)
- Camera: TODO (phone model, rear camera)
- Distance and angle: TODO (about 1 m, straight on)
- Lighting: TODO (overhead / daylight / flash on or off)
- Photo file: TODO (name, stored beside this note)
- Carton condition: TODO (intact / dented / other)

## Reader check (fill in)

- Photo attached to: TODO (receipt or ASN row)
- Transcription: TODO (paste the reader output)
- Matches `expected-read.json`: TODO (yes / no, plus notes)
