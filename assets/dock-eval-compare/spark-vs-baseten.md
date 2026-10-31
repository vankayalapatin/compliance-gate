# Spark vs Baseten: dock eval comparison

Same photos, same prompt, same scorer. Spark originals are the committed
caches; spark-rerun is a fresh timed re-read in a separate cache folder;
Baseten is eta-models/muse-spark-open-1026 via inference.baseten.co.

## Easy tier (50 photos, per photo)

| problem | spark-orig | spark-rerun | baseten |
|---|---|---|---|
| clean | 10/10 | 10/10 | 10/10 |
| crushed | 8/8 | 8/8 | 8/8 |
| hidden | 6/6 | 6/6 | 6/6 |
| missing_mark | 7/7 | 7/7 | 7/7 |
| smudged_lot | 6/6 | 6/6 | 6/6 |
| wrong_part | 6/6 | 6/6 | 6/6 |
| wrong_po | 7/7 | 7/7 | 7/7 |
| OVERALL | 50/50 | 50/50 | 50/50 |

Spark rerun matches the original (50/50).

## Hard tier (30 photos, 170 cartons, per carton)

| problem | spark-orig | spark-rerun | baseten |
|---|---|---|---|
| clean | 110/110 | 110/110 | 110/110 |
| missing_mark | 13/13 | 13/13 | 13/13 |
| smudged_lot | 12/12 | 12/12 | 12/12 |
| subtle_dent | 10/10 | 10/10 | 10/10 |
| wrong_part | 12/12 | 12/12 | 12/12 |
| wrong_po | 13/13 | 13/13 | 13/13 |
| OVERALL | 170/170 | 170/170 | 170/170 |

Spark rerun matches the original (170/170).

## Easy tier: time, tokens, cost per photo

| run | photos | avg ms/photo | avg in-tok | avg out-tok | $/photo |
|---|---|---|---|---|---|
| spark-rerun (muse-spark-1.3-contributor) | 50 | 6,824 | 3,150 | 934 | rates not set |
| baseten (meta-models/muse-spark-open-1026) | 50 | 10,690 | 3,287 | 1,061 | rates not set |

## Hard tier: time, tokens, cost per photo

| run | photos | avg ms/photo | avg in-tok | avg out-tok | $/photo |
|---|---|---|---|---|---|
| spark-rerun (muse-spark-1.3-contributor) | 30 | 11,628 | 3,345 | 1,768 | rates not set |
| baseten (meta-models/muse-spark-open-1026) | 30 | 15,847 | 3,482 | 2,805 | rates not set |

