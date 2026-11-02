#!/usr/bin/env python3
"""Print-ready carton label CTN-13 as a one-page PDF (stdlib only).

Layout mirrors Gate.Agent/DockLabelCompositor.cs DrawLabel: white card,
black border, carton id plus PART / PO / LOT / QTY / UN lines on the left,
red-hatched lithium-battery mark with UN3480 top right, Class 9 diamond
bottom right. Geometry names (pad, markW, lineStep, ...) match that method
so a photo of this print looks like the dock labels the vision reader
already handles.

Reader contract: Gate.Agent/DockImagePipeline.cs ReadPrompt lists the
transcription fields; VerifyAgainstManifest lists the per-carton checks.
See expected-read.json for this label's values.

Usage: python3 make-label.py   # writes CTN-13-label.pdf next to this file
"""

from pathlib import Path

LABEL = {
    "carton_id": "CTN-13",
    "part": "BAT-1",
    "po_number": "PO-1037",
    "lot_code": "LOT-013",
    "units": 100,
    "un_number": "UN3480",
    "mark_present": True,
}

# 6 x 4 inch landscape page at 72 pt/in.
PAGE_W, PAGE_H = 432.0, 288.0
MARGIN = 18.0  # 1/4 inch quiet margin; the card itself is 5.5 x 3.5 inch.

# Helvetica advance widths (units per 1000 em) for the characters on the
# label. Standard Type 1 metrics; bold matches regular for these glyphs.
WIDTHS = {
    " ": 278, "-": 333,
    "0": 556, "1": 556, "2": 556, "3": 556, "4": 556,
    "5": 556, "6": 556, "7": 556, "8": 556, "9": 556,
    "A": 667, "B": 667, "C": 722, "D": 722, "E": 667, "F": 611,
    "G": 778, "H": 722, "I": 278, "J": 500, "K": 667, "L": 611,
    "M": 833, "N": 722, "O": 722, "P": 667, "Q": 722, "R": 667,
    "S": 611, "T": 611, "U": 722, "V": 667, "W": 944, "X": 667,
    "Y": 667, "Z": 611,
}


def measure(line, size):
    return sum(WIDTHS[c] for c in line) / 1000.0 * size


def fit(size, lines, max_width):
    """Mirror DockLabelCompositor FitText: shrink only, floor at 6 pt."""
    for line in lines:
        wide = measure(line, size)
        if wide > max_width and wide > 0:
            size = max(6.0, size * max_width / wide)
    return size


def pdf_text(s):
    return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def build_content():
    x0, y0 = MARGIN, MARGIN
    w, h = PAGE_W - 2 * MARGIN, PAGE_H - 2 * MARGIN

    # Geometry below follows DrawLabel; sk values are measured down from
    # the card top (Skia origin) and converted with Y() to PDF y (up).
    def Y(sk):
        return y0 + h - sk

    pad = w * 0.04
    markW = min(w * 0.3, h * 0.55)
    text_width = w - pad * 2 - markW

    carton = LABEL["carton_id"]
    lines = [
        f"PART {LABEL['part']}",
        f"PO {LABEL['po_number']}",
        f"LOT {LABEL['lot_code']}",
        f"QTY {LABEL['units']}",
        LABEL["un_number"],
    ]
    bold_size = fit(h * 0.19, [carton], text_width)
    text_size = fit(h * 0.125, lines, text_width)
    mark_text_size = markW * 0.22

    line_x = x0 + pad
    line_sk = pad + h * 0.15
    line_step = h * 0.135
    border_w = max(2.0, w / 120.0)
    red_w = max(2.0, w / 150.0)

    mark_l = x0 + w - pad - markW
    mark_top_sk = pad
    mark_bot_sk = pad + markW
    mark_top = Y(mark_top_sk)
    mark_bot = Y(mark_bot_sk)

    ops = []
    ops.append("1 1 1 rg 0 0 %.2f %.2f re f" % (PAGE_W, PAGE_H))
    # Card: white fill, black border.
    ops.append("1 1 1 rg %.2f %.2f %.2f %.2f re f" % (x0, y0, w, h))
    ops.append("0 0 0 RG %.2f w %.2f %.2f %.2f %.2f re S" % (border_w, x0, y0, w, h))
    # Left text: carton id bold, then the five lines (black fill; PDF
    # text paints with the fill colour, which is still white above).
    ops.append("0 0 0 rg BT /F2 %.2f Tf 1 0 0 1 %.2f %.2f Tm (%s) Tj ET"
               % (bold_size, line_x, Y(line_sk), pdf_text(carton)))
    sk = line_sk
    for line in lines:
        sk += line_step
        ops.append("0 0 0 rg BT /F1 %.2f Tf 1 0 0 1 %.2f %.2f Tm (%s) Tj ET"
                   % (text_size, line_x, Y(sk), pdf_text(line)))
    # Lithium-battery mark: red-bordered square, red hatch, UN number.
    if LABEL["mark_present"]:
        ops.append("1 1 1 rg %.2f %.2f %.2f %.2f re f"
                   % (mark_l, mark_bot, markW, markW))
        ops.append("q %.2f %.2f %.2f %.2f re W n" % (mark_l, mark_bot, markW, markW))
        ops.append("1 0 0 RG %.2f w" % red_w)
        step = max(4.0, markW / 12.0)
        d = -markW
        while d < markW + markW:
            ops.append("%.2f %.2f m %.2f %.2f l S"
                       % (mark_l + d, mark_bot, mark_l + d + markW, mark_top))
            d += step
        ops.append("Q")
        ops.append("1 0 0 RG %.2f w %.2f %.2f %.2f %.2f re S"
                   % (red_w, mark_l, mark_bot, markW, markW))
        ops.append("0 0 0 rg BT /F1 %.2f Tf 1 0 0 1 %.2f %.2f Tm (%s) Tj ET"
                   % (mark_text_size, mark_l + markW * 0.06, Y(mark_top_sk + markW * 0.58),
                      pdf_text(LABEL["un_number"])))
    # Class 9 diamond: the compositor fills yellow then overpaints black,
    # so dock pixels show a solid black diamond with no visible numeral.
    # This print matches those pixels.
    cx = mark_l + markW / 2.0
    rest = (h - pad) - mark_bot_sk
    cy = Y(mark_bot_sk + rest / 2.0)
    r = min(markW / 2.0, rest / 2.0) * 0.9
    ops.append("0 0 0 rg %.2f %.2f m %.2f %.2f l %.2f %.2f l %.2f %.2f l h f"
               % (cx, cy - r, cx + r, cy, cx, cy + r, cx - r, cy))
    return "\n".join(ops) + "\n"


def write_pdf(path):
    content = build_content().encode("latin-1")
    objects = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %.0f %.0f] "
         "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>"
         % (PAGE_W, PAGE_H)),
        "<< /Length %d >>\nstream\n" % len(content)
        + content.decode("latin-1") + "endstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
        "<< /Title (Carton label CTN-13) /Creator (make-label.py) >>",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += ("%d 0 obj\n%s\nendobj\n" % (i, body)).encode("latin-1")
    xref = len(out)
    out += ("xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)).encode("ascii")
    for off in offsets:
        out += ("%010d 00000 n \n" % off).encode("ascii")
    out += ("trailer\n<< /Size %d /Root 1 0 R /Info 7 0 R >>\nstartxref\n%d\n%%%%EOF\n"
            % (len(objects) + 1, xref)).encode("ascii")
    path.write_bytes(bytes(out))


if __name__ == "__main__":
    target = Path(__file__).with_name("CTN-13-label.pdf")
    write_pdf(target)
    print("wrote %s (%d bytes)" % (target, target.stat().st_size))
