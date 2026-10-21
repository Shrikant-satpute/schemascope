# Compare databases - v3 narration script

About 65 s, white theme, Kokoro am_michael (about 2.3 words/s). The character appears in scene 1 only. The yellow highlight bar appears only in the hook (scene 1) and the close (scene 13). Scenes 4-12 are a calm walkthrough in the v1 style: one steady real app frame per step, a numbered step pill with one short caption, one slow camera move, and the cursor doing the work.

All app frames are real v2 light-theme screenshots (`assets/video/v2/shots`). Redaction chips stay on: 'Runs as' (machine login), the output folder path, and any LAN IP.

## 1. Hook - Compare databases (0.0-4.4 s)

**VO:** "Compare databases. Staging, production, and every client database."

**On screen:** [Compare databases.] on the yellow bar. Three pills: Staging - Production - Every client database.

**Visual:** White grid. The headline lands, then the pills type on. The last pill fans into eight small client icons. The character (`character.svg`) pops up bottom-right and points at the pills, then slides out at 4.0 s. This is his only appearance in the video.

**Sound:** One soft bass hit and three light ticks. The music bed starts.

## 2. Story - who has what? (4.4-15.0 s)

**VO:** "We ship one database to many clients. Soon nobody knows which client has which tables, which procedure is missing, or which schema never shipped."

**On screen:** Small question tags: which tables? / which procedure is missing? / which schema never shipped?

**Visual:** No character and no yellow bar. One blue source card (SSDemo_Dev, shown on screen only) draws lines to eight client cards. The question tags appear in time with the voice. On Client04's card, an empty 'support' folder outline appears. Slow push-in. Tagged 'illustration'.

**Sound:** Music bed and a soft tick per tag.

## 3. The easy fix (15.0-18.4 s)

**VO:** "The easy fix: compare them with SchemaScope."

**Visual:** Cross-fade into the real app (`01-empty-compare`), with Write mode Off. The camera eases out from the logo to the full window. A small grey note reads 'Real app, demo data'.

## 4. Step 1 - Pick the source you trust (18.4-21.4 s)

**VO:** "First, pick the database you trust."

**Pill:** 1 - Pick the source you trust

**Shots:** `01-empty-compare` -> `02-picker-source` (SSDemo_Dev selected) -> `03-source-picked`. The cursor clicks the Source slot and then 'Use this database'. One slow push-in.

## 5. Step 2 - Add every target (21.4-25.4 s)

**VO:** "Add every target: Q A and eight clients."

**Pill:** 2 - Add every target - QA + 8 clients

**Shots:** `04-picker-target` -> `05-targets-01..09` -> `06-setup-ready`. The nine chips fill in one by one while the camera pans slowly along the chip row.

## 6. Step 3 - Compare, all at once (25.4-30.0 s)

**VO:** "Press Compare. All nine at once, in about a second."

**Pill:** 3 - Compare - all 9 at once, about a second
**Small note:** 9 targets in 890-985 ms warm (5 runs) - first run after app start 2.8 s - LocalDB, demo data

**Shots:** `07-compare-hover` (click) -> `09-comparing-warm`, where all ten databases show 'Reading catalog' at once -> `10-results-dashboard`. The camera pulls out slowly.

## 7. Step 4 - A score for every client (30.0-33.4 s)

**VO:** "Every client gets a score, worst first."

**Pill:** 4 - A score for every client - worst first

**Shots:** `10-results-dashboard` -> `11-results-dashboard-scrolled`. The camera pans along the tiles: Client03 88.8, Client04 88.9, Client07 93.4, Client02 96.3, Client08 96.6, Client05 97.7, Client06 98.9, Client01 99.2, QA 99.8. A thin blue underline sweeps under the scores.

## 8. Step 5 - Find what is missing (33.4-41.2 s)

**VO:** "See what is missing. Client four never got the support schema. Client three lacks tables and procedures."

**Pill:** 5 - Find what is missing

**Shots:** `14-matrix-grid-tall`. First the camera pushes onto the row 'Schema support.support' and the red X under SSDEMO_CLIENT04, which gets a thin red ring and the label 'Client04: 127 missing - the whole support schema never deployed'. Then it slides to the SSDEMO_CLIENT03 column, where the missing tables billing.BillingCycle and hr.Department carry red Xs, with the label 'Client03: 58 missing'.

**Note:** The word 'procedures' needs new shot 46, a Missing filter on Client03 that shows procedures. Without that shot, the line becomes "Client three is missing tables."

## 9. Step 6 - Open any object side by side (41.2-45.2 s)

**VO:** "Open any object to see the exact lines."

**Pill:** 6 - Open any object side by side - see the exact lines

**Shot:** `15-diff-changed-proc` shows core.usp_Currency_List, with SSDemo_Dev on the left and Client03 on the right. The camera pushes in slowly onto the added line 'AND t.Status <> 9', which has a pale wash on that line only.

## 10. Step 7 - Create the fix scripts (45.2-48.8 s)

**VO:** "Create the fix scripts, checked offline first."

**Pill:** 7 - Create the fix scripts - checked offline

**Shots:** `20-hero-tile-create-scripts-hover` (Client05 tile, 97.7%) -> `21-create-scripts-dialog-readonly` -> `28-scripts-written`. The folder list shows 01_columns 4, 03_missing 13, 04_columntypes 4, 05_constraints 6 and 06_indexes 7, with the green line 'Checked offline: 173 batches parse, and 24 of 24 code bodies match the source exactly.' The path is redacted.

## 11. Step 8 - Write mode, new in 1.2 (48.8-54.4 s)

**VO:** "New in one point two: write mode. Type the name to apply."

**Pill:** 8 - Write mode - turn it on, type the name, apply (with a small 'NEW IN 1.2' tag)

**Shots:** `22-write-mode-switch-off` -> `23b-write-mode-confirm-hover` -> `24-write-mode-on` -> `29-apply-dialog` -> `30-apply-typing-03/07/11/13` -> `31-apply-name-typed`, ending with 'Apply 34 files to SSDemo_Client05' enabled. 'Runs as' is redacted. One camera move goes from the header switch down to the confirm box.

## 12. Step 8 - 100%, and Protected is refused (54.4-59.6 s)

**VO:** "Client five goes to one hundred percent. Protected servers are refused."

**Pill:** 8 - Client05: 97.7% -> 100% - Protected servers: refused

**Shots:** `34-apply-result` ('Applied all 34 file(s)...', 'now matches the source 100%') -> `35-recompared-hero-100` (Client05 tile at 100.0%, labelled 'was 97.7% in the 9-target compare') -> `37-protected-apply-refused` (Client08: 'Protected server - scripts only.', with Close as the only button).

## 13. Close (59.6-65.4 s)

**VO:** "Stop comparing in pairs. Free at schema scope dot shadow mark dot in."

**On screen:** [Stop comparing in pairs.] on the yellow bar. Pills: Free - Open source - SQL Server 2012+ - Azure SQL - Windows 10 & 11. Button: 'Free download - schemascope.shadowmark.in'. No character.

## Hero loop (6 s, silent)

White grid. 'Compare databases.' lands on the yellow bar and the real dashboard tile row slides up with all nine scores. One blue sweep crosses the row, then everything fades back to the empty grid. No character.

## New shots wanted

- **45-matrix-missing-client04** (optional): Status = Missing, 'Only SSDemo_Client04', listing support.support and the support.* objects with a red X under Client04.
- **46-matrix-missing-client03** (needed for the word 'procedures'): Status = Missing, 'Only SSDemo_Client03', with both missing tables and missing procedures visible.

## Honesty notes

- All numbers come from `v2/demo-db/measurements.json` and the v2 frames.
- The 890-985 ms warm figure and the 2.8 s cold figure are both on screen. The app's own 1130 ms stays visible.
- Client04 has 127 missing because the support schema was never deployed. Client03 has 58 missing (3 tables and 55 modules).
- Client05 is at 97.7% in the 9-target compare and at 100% after applying the 34 files.
- Client08 was marked Protected for the demo.
- Staging and production in the hook and the story are illustrations. The demo databases are QA and Client01-08 on LocalDB.

## TTS rules

Never speak 'Dev'. 'Q A' is spoken with a space, numbers are spoken as words, 'Client four' and 'Client five' are spoken that way, and '1.2' is spoken 'one point two'. The URL is spoken 'schema scope dot shadow mark dot in'. No lone one-word lines.
