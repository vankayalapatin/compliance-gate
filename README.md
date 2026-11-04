# ComplianceGate

ERP agents can read your policies. ComplianceGate enforces them.

Public regulations and your own company knowledge base, turned into controls that every agent and every person must pass, changed by memo in minutes, with a named owner signing off.

<!-- TODO: YouTube demo link -->

**AI agents are starting to place orders and move company money. Who makes sure they follow the rules?**

## Why companies aren't letting agents act yet

ERP vendors are shipping agents that create purchase orders and handle supplier invoices, and most large companies are trying them. Few let them act at scale. [Forrester (June 2026)](https://www.forrester.com/blogs/the-state-of-agentic-ai-in-2026-companies-are-chasing-few-are-catching/) found three-quarters of enterprise leaders adopting agentic AI but only a small minority running it in meaningful production, naming governance gaps and the cost of making autonomous actions auditable among the barriers. And where agents do run, control lags behind: in an [EY survey of $1B+ companies (September 2026)](https://www.ey.com/en_us/newsroom/2026/09/ey-survey-finds-that-autonomous-ai-implementation-outpaces-oversight-yielding-an-ai-governance-gap), 85% of agentic AI users said some of their agents act without real-time human involvement, and 49% hadn't updated their governance for agents at all.

The rules are the other problem. When tariffs rise or a government tightens import rules, customs enforces them at the border. The company's response is a different matter: buy less from that country, require origin documents from suppliers, cap any one country's share. That's a policy decision, made in a meeting or a memo. Turning it into a control in the ERP takes months, often years of specs, consultants, configuration and testing. Until then, buyers keep ordering the old way, and nothing stops a purchase the company has already decided against.

## What ComplianceGate does

**The ERP stays strict and simple. The agent does the thinking. People make the decisions.**

ComplianceGate is a control layer that lets an AI agent work inside the ERP a company already has, under the same rules as the people around it. The *gate* is the control point: the moment the ERP commits a purchase order, a payment or a release. Every actor passes through the same gate, agent or person, and the gate decides what's allowed.

It closes both gaps. Companies with years and millions invested in SAP, D365 or Oracle won't replace them to use agents, so ComplianceGate works alongside the ERP instead of replacing it. The ERP's controls stay in place, and people stay the approvers. The agent does the work. Company policy, from a memo or a regulation, becomes a rule enforced where money moves, for the agent and for people alike.

Everything in this repo was built during the hackathon, Oct 26 – Nov 4, 2026, by a technical architect building on Dynamics 365 for a living. Purchasing is the first domain, because it's where policy meets money.

## The design

1. **The agent does the work, and people make the judgment calls.** Muse Spark decides what to try next: raise the request, pick the supplier, read the dock photos, match the invoice, draft the payment. When something needs judgment (a damaged carton, a missing label, an invoice that doesn't match), it hands the decision to a named person with the evidence attached. Every payment is approved by a person.
2. **The ERP enforces the process, and the agent is only a user of it.** Approvals, signing limits, separation of duties, and holds live in the ERP, with no AI inside it. The agent computes invoice match results; the ERP enforces the accepted/held split. The agent is a limited ERP user behind one adapter. It never writes to the database directly, can't approve anything, and the ERP refuses any call it isn't allowed to make.
3. **Company rules run at the ERP's control points, for everyone.** Rules the ERP can't express on its own, like a 60% cap on any one country of origin or "pay only for cartons that passed the dock check", are written as code. The ERP calls them at each control point, so the agent and a person on the ERP screen get the same refusal with the same reason. A rule only the agent has to follow is advice, not a control ([control-point tests](Gate.Eval/Erp/ErpPolicyControlTests.cs)).
4. **Rules are computed from the company's own data.** The country cap isn't a blocklist. It's calculated from 12 months of receipts plus orders still open, for this order's quantity, so the same supplier can be blocked on a large order and allowed on a small one, and splitting an order into small POs can't get around it.
5. **The agent looks for the best plan the rules allow.** When the cheapest supplier is blocked for the full quantity, Spark works out how much it can still buy there and proposes a split across suppliers. Code verifies the whole plan against the rules, a person approves it, and only then are the POs created. The model proposes; the rules decide.
6. **Physical evidence controls money.** Matching usually compares quantities on paper. Here, a dock photo of a carton missing its lithium-battery mark holds exactly that carton's value on the invoice. The rest is paid, and a new photo of the relabelled carton releases the hold. Evidence flows from the dock to the invoice line to the payment.
7. **A policy memo becomes an enforced rule in minutes, with a person approving.** See [From memo to rule](#from-memo-to-rule).

These are the guardrail patterns Meta recommends for coding agents, applied to money. Under **immutable guardrails**, the agent can't rewrite its own rules: it has no tool that edits a rule, and a rule changes only after a named person approves it. Under **staged approvals**, small purchases under $500 go through on their own, while larger purchases, every invoice and every payment wait for a person.

The agent has no more power than a junior buyer. Every decision shows its math and its evidence.

## Why gates, and where they stop

Agents are probabilistic. One that is right 99% of the time is still wrong a hundred times across 10,000 invoices, and a cleverly written supplier email can talk it into things. A deterministic check at the moment money is committed doesn't depend on how good the model is that day. It's the same reason companies have separation of duties for people.

Rules have limits too. They only cover what someone foresaw (this build's own country cap missed open orders until we found it), too many make the agent useless, and they can't judge a crushed carton. So ComplianceGate layers them:

- **Hard gates** for irreversible steps: committing a PO, posting a payment, releasing held value.
- **People** for judgment, with the evidence attached.
- **Rules that change fast:** a memo becomes a rule in minutes, so a wrong rule doesn't stay wrong for months.

The gates are meant to move as an agent earns trust, never to disappear from the irreversible steps.

Control points are wired into the ERP once, through its normal change process. Policy changes within supported rule types go through owner approval, an impact report and versioning. New rule types are code.

## Company knowledge and rule packs

The control layer works from two kinds of company material, both owned by named people and versioned.

**The company knowledge base** is where policies, memos and regulatory notes live, in spaces with owners: Leadership, Procurement, HR and Regulatory (Dana Director), Finance (Finn Finance), and Operations (Maya Planner). Every entry is versioned, with an owner, a status, an effective date and who approved it. A new version never overwrites the old one, and an editor can't approve their own edit. Rules cite the exact entry and version they're grounded in, and editing an entry flags the rules built on the old version for review.

**Rule packs** turn that material into enforceable checks. Each pack has an owner, a version, an adoption date and its public source:

| Pack | Covers | Examples | Public source |
|---|---|---|---|
| **BAT-LI** | Shipping lithium-ion cells | Lithium-battery mark on every carton; damaged cartons to a person; label PO and lot must match the shipment | UN 38.3, IATA DGR |
| **ELEC** | Electronics documents and payment | RoHS and REACH declarations from the supplier; ELEC-04, pay only what passed the dock | EU RoHS, REACH |
| **SCR** | Supplier screening | Restricted-party screening (OFAC SDN); UFLPA entity screening | OFAC SDN, UFLPA |
| **CORP** | Company sourcing policy, from memo MEMO-0001 | CORP-02, no country above 60% of a critical part over 12 months | MEMO-0001 |

Every block cites its rule, its version and its source, so a person can check why.

## From memo to rule

1. **A memo arrives:** a company memo, or the text of a regulation.
2. **Spark proposes the rule.** It reads the memo against the relevant knowledge-base entries and proposes a rule type, its parameters, open questions and the impact on past and open orders, citing every entry it relied on.
3. **Code checks the proposal.** Citations that don't resolve are rejected, and the rule type must exist in a closed registry of supported types.
4. **A named owner approves.** The memo's author can't approve their own memo. The rule is published as a new pack version.
5. **The ERP enforces it from the next document on.** Posted documents are never re-evaluated; orders still open show up in the impact preview before anyone approves.

**Example.** Dana Director writes a memo cutting the China limit for battery cells from 60% to 40%. Spark proposes tightening CORP-02 to the lower cap, and the impact preview shows China over the new cap while the Mexican control order still passes. A named owner decides with that in front of them. Once approved, the next battery order from a Chinese vendor is refused at the ERP.

**When no rule type fits**, Spark says so and names what's missing. "Prefer sustainable suppliers" can't be enforced, because there's no measure for it. A new kind of control is engineering work, code and tests, and the model can't invent enforcement logic. That boundary is deliberate.

Other memos run live:
- **"Asian suppliers":** a question back, asking which countries count.
- **The UFLPA text (19 U.S.C. 1307):** can't be mapped yet, with the exact gap named.

Observed November 3: about 90–115 seconds and roughly 1.8K input / 8–11K output tokens per memo.

## One order, start to finish

Lumenfield Eyewear needs 1,200 battery cells for a customer order of 1,300 smart glasses.

- **Sourcing.** The cheapest supplier, CinderVolt (China, $14.20), can't take the whole order. Over the last 12 months the company bought 10,000 cells, 5,900 of them from China. Buying all 1,200 from CinderVolt would raise China's share to 7,100 of 11,240 cells (including 40 units on an open order), or 63.2%, and CORP-02 caps any one country at 60%.
- **The plan.** Spark works out how much it can still buy from CinderVolt: 800 cells, with the other 400 from Meridian Cellworks (Mexico, $18.50). China lands at 59.6%. The plan costs $18,760 instead of $22,200, saving $3,440. Code verifies it, a person approves it, and two POs are created.
- **The same rule for people.** When a planner tries to order all 1,200 from CinderVolt by hand on the ERP screen, the ERP refuses it with the same reason. Five small orders in a row don't get around it either.
- **Receiving.** Spark reads the dock photos: 11 cartons pass and CTN-07, missing its lithium-battery mark, is quarantined.
- **Paying.** The invoice splits by quantity: $20,350 approved and paid, $1,850 held on CTN-07's 100 units. Finance can't pay or release the held amount while the carton is in quarantine. The supplier sends a relabel photo, Spark reads it, the hold releases, and the rest is paid in the next run.

Order 200 glasses instead and only 100 cells are short. China's share would be 59.2%, so CinderVolt takes the whole order and wins on price. The rule is computed, not scripted.

## How it works

| Project | What it does |
|---|---|
| `Gate.Erp` | The ERP: EF Core over SQLite, documents shaped like Dynamics 365 Finance & Operations (header and lines, statuses, number sequences, posted documents immutable). It holds every control a person or the agent must pass: approval routing and delegation, signing limits, no self-approval, invoice approver ≠ payment approver, verified bank details, holds and quarantine, duplicate invoice numbers. At each control point it calls the company policy check and refuses with its rule ID, version and reason. |
| `Gate.Compliance` | Company and regulatory rules as versioned packs (BAT-LI battery shipping, ELEC electronics documents, SCR supplier screening, CORP company policy), each with an owner, an adoption date and a public source. Verdicts are computed in code from ERP data. |
| `Gate.Agent` | One case per short item. Muse Spark drives each case through tool calling: 17 tools, five of them read-only, plus exactly one action tool offered per step. Illegal or refused calls go back to Spark with the reason and the legal moves. The agent reaches the ERP only through `IErpAdapter`. |
| `Gate.Web` | Blazor Server UI: Decisions, Cases, ERP (list → record pages), Governance, Admin, and an Outside world for the customer, suppliers and dock. An agent activity panel shows what the agent is doing, what it's waiting on, and every refused attempt. |
| `Gate.Eval` | 1,442 offline tests plus live tests, the dock-label eval harness, and the Spark vs Spark Open comparison. |

Two databases: the ERP holds business records; the agent platform holds cases, decisions, evidence and transcripts. Restore copies a saved company baseline back. It makes zero model calls, and nothing moves until a person or an outside party acts.

### Where Meta's models are used

| Model | Used for |
|---|---|
| **Muse Spark** (`muse-spark-1.3-contributor`) | Driving every case through tool calling; reading dock photos and supplier invoices into structured JSON; choosing a supplier among those that passed and explaining why; drafting supplier emails and reading replies; proposing rules from memos with citations; answering "Ask why" on decision cards. |
| **Muse Image** (`muse-image-1.0`) | Text-free dock scenes. Label edits on pallet views are attempted live and verified against the shipment notice, falling back to code-drawn labels on any mismatch (reason recorded); the reshot close-up is always drawn from the shipment data. |
| **Muse Spark Open on Baseten** | Re-reading the same eval photos with the same prompt and scorer, as an open-weights comparison. |
| **Muse Code** | Writing the application: six role skills, parallel sub-agents, a read-only reviewer. |

## Results

### Agent results

Recorded walks used 29, 21, and 12 live model calls (split order, held carton, small top-up; see `docs/ONE-ORDER-RUNBOOK.md`); per-case tool calls, refused calls, rule steps, tokens, and cost show on the case scorecard.

### Dock-label eval

The answer key was decided before any image was built, and no reader ever loads it. Label problems are drawn by code; scenes come from Muse Image.

| Tier | Photos | Scored | Spark |
|---|---|---|---|
| Easy: one large label per photo | 50 | per photo | 50/50 |
| Hard: 5–6 small rotated labels per pallet, with blur, glare and colour shift | 30 | 170 cartons | 170/170 |

Problems covered: missing battery mark, wrong PO, wrong part, smudged lot, crushed, hidden and dented cartons. Two measurement bugs were fixed before the final runs: a 6-label read truncated at 2,000 output tokens, so the budget went to 4,000; and a shared lot code let smudged reads copy their neighbours, so every hard-tier carton now has its own lot. In the app's own receiving flow (12 cartons, 3 pallet views plus a reshot), verdicts are 12/12 and rule citations 12/12, with one carton hidden by design and sent to a person.

### Spark vs Spark Open on Baseten

Same photos, same prompt, same scorer.

| | Spark (hosted) | Spark Open (Baseten) |
|---|---|---|
| Easy accuracy | 50/50 | 50/50 |
| Hard accuracy | 170/170 | 170/170 |
| Time per photo, easy / hard | 6.8 s / 11.6 s | 10.7 s / 15.8 s |
| Output tokens per photo, easy / hard | 934 / 1,768 | 1,061 / 2,805 |

The open-weights model matched hosted accuracy at about 1.5× the read time. A fresh Spark rerun matched the original reads exactly on both tiers.

### Cost

From the token counts above, at Meta's published Muse Spark rates.

| | Standard rates ($1.25 / $4.25 per 1M in/out) | Contributor rates ($0.10 / $0.20 per 1M) |
|---|---|---|
| Per hard-tier photo | ≈ $0.012 | ≈ $0.0007 |
| Per shipment (3 pallet photos + 1 reshot) | ≈ $0.047 | ≈ $0.003 |

### Muse Image findings

- Text-free scenes came out right on the first try every time.
- A single-label close-up edit verified 1/1.
- Multi-label pallet edits failed field-by-field verification 3/3, so pallet labels are drawn by code from the shipment notice. This is also how real suppliers print them.

Every number in this section traces to a file listed in [EVAL-SOURCES.md](EVAL-SOURCES.md).

## Running it

Requires the .NET 8 SDK and a Meta Model API key from [dev.meta.ai](https://dev.meta.ai) (API keys → Create API key). No keys are in this repo.

```bash
git clone https://github.com/vankayalapatin/compliance-gate
cd compliance-gate
dotnet user-secrets --project Gate.Web set "MetaApi:ApiKey" "<your key>"
dotnet user-secrets --project Gate.Web set "MetaApi:BaseUrl" "https://api.meta.ai/v1"
dotnet user-secrets --project Gate.Web set "MetaApi:Model" "muse-spark-1.3-contributor"
dotnet user-secrets --project Gate.Web set "MetaImages:ImageModel" "muse-image-1.0"
dotnet run --project Gate.Web
```

Open http://localhost:5080 and choose **Admin → Restore saved data**. Then:

1. **Outside world → Customer shop:** order 1,300 GLASS-1.
2. Switch **Acting as** to approve on **Decisions**: Del Delegate covers Dana Director's purchase approvals, and Finn Finance approves payments.
3. **Outside world → Suppliers:** each supplier acknowledges, ships and invoices from its own portal.
4. **Outside world → Dock:** send the dock photos, then the reshot for CTN-07.
5. Watch the agent activity panel on the right. **Admin → Presentation pace** slows the agent down so you can follow it.

**No key?** Admin → **Use offline answers** runs the recorded walk with zero model calls.

**Tests:** `dotnet test --filter "Category!=Live"` runs the offline suite. Live tests need a key and make real model calls.

## For ERP people

The built-in ERP follows Dynamics 365 Finance & Operations structures, simplified:

| Here | D365 F&O |
|---|---|
| Purchase request → PO only from an approved PR | Purchase requisition → purchase order |
| PO approval status and document status kept separately; vendor confirmation required before receiving | PurchTable approval and document status; PurchConfirmJour |
| ASN with carton lines; product receipt carrying the supplier's packing-slip number | Packing slip / product receipt journal |
| Inventory transactions with status: available, quarantined, rejected, consumed | InventTrans; quarantine as a status, not a table |
| Vendor invoice lines split by quantity into accepted and held parts | Vendor invoice with held lines |
| Payment journal: draft → approved → posted, bank snapshot, due dates from terms | Vendor payment journal and payment proposal |
| Approvals as records (routed to, acted by, delegate, limit checked) with dated delegations | Workflow approval history; delegation |
| Number sequences per document type, assigned at creation (SO, PR, PO) or at posting (receipt, invoice, payment) | Number sequences |

What the ERP refuses, for people and the agent alike: approving your own request; approving above your signing limit; a PO without an approved PR; ordering from a blocked vendor; paying held value; paying to an unverified or changed bank account; the same person approving an invoice and its payment; receiving on a closed PO; a duplicate supplier invoice number. Company rules add their refusals at the same control points.

**In a real deployment** the adapter would point at D365. The agent would be an app registration mapped to a user with a custom security role (create requests, POs from approved requests, receipts, invoices and payment drafts; never approve, post payments, change bank details or release holds). It would be triggered by business events, and the policy check would run as a validation the ERP calls before confirming a PO or posting a payment.

Not built: general ledger, accounts receivable, tax, landed cost, budget control, purchase agreements.

## How this was built

- **Muse Code** wrote the code. Six role skills (ERP architect, compliance engineer, vision assets, agent engineer, UI engineer, and a read-only reviewer that edits nothing) ran as parallel sub-agents, each owning its folders.
- The idea came from the author's day job building on Dynamics 365. The design was settled on day 1 in a Muse Code interview (docs/DECISIONS.md).
- Muse Code's sandbox can't open local sockets, so builds and tests ran outside it through `scripts/agent-check.sh`, a check runner Muse Code also wrote.
- Muse Glimmer was tried locally on a MacBook M3 Pro (36 GB) and was too slow for the reads. The hackathon Baseten key served only Spark Open, so Spark Open became the open-weights comparison.
- SAM 3.1 wasn't added: the eval showed no counting or localisation weakness for it to fix, because Spark finds and reads the labels itself.

## What's next

- **A Dynamics 365 adapter:** the agent as an app registration with a custom security role, triggered by business events, with the policy check running as an ERP validation before a PO is confirmed or a payment is posted.
- **A real dock camera** instead of generated photos.
- **More rule types** in the registry, so more memos can be enforced without new code.
- **Earned autonomy:** widen what the agent may do on its own (higher auto-approve limits, closing certain reviews) as its measured track record grows, without removing the gates on irreversible steps.
- **Open rule packs:** publish the packs and their sources so companies can share and review them.

## Known limits

- **The ERP is built in, not a live D365.** Everything the agent does goes through one adapter interface, ready for a real ERP connection that isn't built.
- **The outside world is simulated:** the customer shop, the supplier portals and the dock. Dock photos are generated, with scenes from Muse Image and labels drawn from the shipment notice. Spark never sees the shipment notice; it only transcribes, and code compares.
- **The eval is on generated photos** with a known answer key: 100% there, not tested on a real dock. Both tiers share one scene family (cartons on a pallet with a blurred background), so the difficulty comes from the labels. The manual-check boxes for damage photos are still unticked.
- **No real sign-in.** "Acting as" switches between the five people.
- **One currency (USD), one company.**
- **The first purchase of an item** with no 12-month history passes the country cap as not applicable. The policy should state a minimum base or route the purchase to a person.
- **If Spark's payment call is refused twice,** the payment is drafted by rule and logged on the case. A person still approves it.
- **Memo flow is scoped:** supported rule types only; a memo that needs a new kind of control is answered with what's missing. Memo proposals take about 90–115 seconds.
- **In a split, lot numbers aren't yet scoped per shipment,** so a hold can catch the other supplier's lot.
