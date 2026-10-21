# Stop comparing in pairs - v2 narration script

About 71.5 s, white theme, Kokoro am_michael. Plus an 8 s silent hero loop.

**Pitch:** Your 8 client databases. One compare. One second. Measured: a 9-target compare of 10 SSDemo databases on LocalDB took 890-985 ms over 5 warm runs (first run after app start 2.8 s); both are shown on screen.

## 1. Hook - ten cards, one compare  (0.0-3.8 s)

**VO** (0.2-3.6 s, 8 words, 2.35 w/s): "Eight client databases. Plus Q A. One compare."

**On screen:** 8 client databases. + QA.  then [ONE compare.] on a yellow block, heavy near-black type  ([...] = yellow highlight block)

**Visual:** White canvas, faint 40px grid. Ten database cards slam into a 5x2 grid on a two-frame stagger (SSDemo_Dev outlined in blue as the source, then QA and Client01-08). Camera starts pushed in and pulls out. The developer character (character.svg, neutral) pops up bottom-right, eyebrows raised, looking at the grid.

**Sound:** Three bass hits, one per beat; a whoosh as the yellow block wipes in.

## 2. Old way - one pair at a time  (3.8-10.8 s)

**VO** (4.0-9.6 s, 11 words, 1.96 w/s): "The old way: one pair at a time. Then eight more."

**On screen:** Pair 1 of 9 ... then [one pair at a time] on yellow  ([...] = yellow highlight block)

**Visual:** Desaturated grey compare windows stacked like cards. Only one progress bar crawls at a time; a counter reads 'Pair 1 of 9'. Small label: 'Pairwise compare tools, e.g. Visual Studio Schema Compare - illustration, not timed'. Character swaps to character-frustrated.svg: arms crossed, foot tapping, a wall clock behind him spins. At 9.8 s everything freezes (frame hold, grey vignette).

**Sound:** Slow ticking clock over a low drone; hard stop at 9.8 s, then half a second of silence.

## 3. Turn - all nine at once  (10.8-18.0 s)

**VO** (11.0-17.8 s, 16 words, 2.35 w/s): "SchemaScope takes all nine targets in one go, and reads every one at the same time."

**On screen:** [All 9 targets.] One sweep.   tag: demo data  ([...] = yellow highlight block)

**Visual:** Hard cut to bright white: real light-theme SchemaScope screenshot (schemascope.theme=light), header Write mode switch visibly Off. Source chip SSDemo_Dev, then nine target chips (SSDemo_QA, SSDemo_Client01..08) fan in. Cursor clicks Compare; one blue sweep crosses the row and all nine match scores land in the same frame. Camera push-in on the score row. Character swaps to character-happy.svg, pointing at the scores. Corner tag 'demo data'.

**Sound:** Bright riser into a clean snap on the sweep; nine soft ticks as the scores land.

## 4. Proof - the stopwatch  (18.0-23.5 s)

**VO** (18.2-23.3 s, 11 words, 2.16 w/s): "Over a thousand objects in each of ten databases, in under a second."

**On screen:** 1,195 objects. 10 databases. [Under a second.]  ([...] = yellow highlight block)

**Visual:** Large stopwatch counts up and stops on 0.95 s. Stat chips type on beside it: '1,195 source objects', '10 databases', '9 targets'. Small grey strip under it: 'Warm runs: 890-985 ms (5 runs). First run after app start: 2.8 s. One target alone: 0.36-0.46 s. LocalDB, demo data.'

**Sound:** Stopwatch ticking, a hard click on stop.

## 5. Pitch card - one line per drum hit  (23.5-28.0 s)

**VO** (23.7-27.8 s, 9 words, 2.2 w/s): "Your eight client databases. One compare. About one second."

**On screen:** Your 8 client databases. / One compare. / [One second.]   subline: 9 targets read in parallel - 890-985 ms warm, demo data  ([...] = yellow highlight block)

**Visual:** Full-frame kinetic type on the white grid (thumbnail style), one line per drum hit, the last on the yellow block. Subline fades in underneath.

**Sound:** Three drum hits locked to the three lines; music returns underneath.

## 6. A score for every client  (28.0-35.2 s)

**VO** (28.2-35.0 s, 16 words, 2.35 w/s): "Every client gets a score, worst first. Re indented code is formatting, not a false alarm."

**On screen:** Worst first.  [Formatting, not a false alarm.]  Client07: 80 formatting-only  ([...] = yellow highlight block)

**Visual:** Real dashboard screenshot, row sorted worst first: Client03 88.8%, Client04 88.9%, Client07 93.4%, Client02 96.3%, Client08 96.6%, Client05 97.7%, Client06 98.9%, Client01 99.2%, QA 99.8%. Camera pushes into the Client07 tile ('80 formatting only, 2 different'); a re-indented procedure chip flips from Different (red) to Formatting (grey). Pan to the hero tile SSDemo_Client05 (9-target compare): 13 missing, 16 different.

**Sound:** Card flips as the tiles sort; a soft pop on the chip flip.

## 7. Exact lines, then the fix  (35.2-41.8 s)

**VO** (35.4-41.6 s, 15 words, 2.42 w/s): "See the exact lines, then create scripts that make it match, checked before they run."

**On screen:** See the exact lines.  [Then make it match.]  173 batches checked, 0 syntax errors  ([...] = yellow highlight block)

**Visual:** Side-by-side diff of a real hero difference: hr.EmployeeSkill.Url is nvarchar(400) on SSDemo_Dev vs nvarchar(255) on SSDemo_Client05, changed token highlighted. Click Create scripts: the real folders fan out with real counts - 01_columns 4, 03_missing 13, 04_columntypes 4, 05_constraints 6, 06_indexes 7 - and 99_review drops in as notes, not scripts (column_type_differences, drifted_modules, flow_safety, target_only_objects, validation). Green badge: 'Checked offline: 173 batches, 0 syntax errors'.

**Sound:** Paper swish per folder; a light chime on the green badge.

## 8. New in 1.2 - Write mode  (41.8-47.8 s)

**VO** (42.0-47.6 s, 13 words, 2.32 w/s): "One point two adds write mode. Off at every launch, until you confirm."

**On screen:** NEW IN 1.2  [Write mode]  Off at every launch  ([...] = yellow highlight block)

**Visual:** Camera zooms onto the header switch 'Write mode: Off', tag 'Off at every launch'. Character (character.svg, surprised and nervous, sweat drop) leans in. Cursor clicks; the real 'Turn on write mode?' dialog appears, its line 'It turns itself off after 30 minutes without use.' highlighted in yellow. Click 'Enable for this session': the switch flips amber On and an amber strip runs under the header.

**Sound:** Tension pad under the dialog; a solid latch click as the switch turns amber.

## 9. Apply on purpose  (47.8-53.3 s)

**VO** (48.0-53.1 s, 12 words, 2.35 w/s): "Type the database name to apply. Protected servers are refused, scripts only."

**On screen:** Type the name to apply.  [Protected = refused.]  ([...] = yellow highlight block)

**Visual:** Real 'Apply to SSDemo_Client05' panel with the five default folders ticked (01, 03, 04, 05, 06). The confirm box types 'SSDemo_Client05' letter by letter and Apply wakes up. Inset slides in: a connection marked Protected, the real red banner 'Protected server — scripts only.' and only a Close button. A red 'Refused' stamp lands outside the inset window.

**Sound:** Key clicks per letter; a dull thud on the Refused stamp.

## 10. One file, one transaction  (53.3-59.3 s)

**VO** (53.5-59.1 s, 13 words, 2.32 w/s): "Each file runs in its own transaction. One error rolls that file back."

**On screen:** [1 file = 1 transaction]  Applied all 34 file(s) to SSDemo_Client05.  ([...] = yellow highlight block)

**Visual:** The 34 files tick off one by one, each in its own bracket labelled 'transaction'. Under them the real banner: 'Each file runs in its own transaction. If one fails, that file is rolled back and the run stops there - the files before it stay applied.' Ends on the real result line 'Applied all 34 file(s) to SSDemo_Client05.' Character, still nervous, watches the ticks.

**Sound:** A tight tick per file, accelerating; resolve chord on the result line.

## 11. Compared again - 100%  (59.3-64.8 s)

**VO** (59.5-64.6 s, 11 words, 2.16 w/s): "Then it compares again, and the client hits one hundred percent."

**On screen:** [100%]  Re-compared: 97.6% -> 100%  ([...] = yellow highlight block)

**Visual:** The app compares again by itself. Hero tile SSDemo_Client05 counts up 97.6% to 100% and turns green; chips read 1,195 same, 0 different, 0 missing. Label: 'Re-compared alone: 97.6% before, 100% after the apply'. Confetti in brand blue and yellow. Character swaps to character-happy.svg, relieved, pointing at the 100%. The tile slides to the healthy end of the worst-first row; the other tiles keep their scores, labelled 'other scores from the 9-target run'.

**Sound:** Rising count-up blips; a bright hit and major chord at 100%.

## 12. Close  (64.8-71.5 s)

**VO** (65.0-71.3 s, 15 words, 2.38 w/s): "Stop comparing in pairs. Get SchemaScope free, at schema scope dot shadow mark dot in."

**On screen:** Stop comparing in pairs.  Your 8 client databases. [One compare.]  schemascope.shadowmark.in  ([...] = yellow highlight block)

**Visual:** Logo centred on the white grid. Title types on, then the tagline with the yellow block. Pills drop in: Free / Open source / SQL Server 2012+ / Azure SQL / Windows 10 & 11 / No Visual Studio needed. Button 'Free download - schemascope.shadowmark.in'. Character waves from the side.

**Sound:** Music swells and resolves; a soft tick per pill.

## Hero loop (8 s, silent)

8 s silent loop, no VO: white grid, logo top-left; the ten database cards drop in, 'One compare.' lands on the yellow block, one blue sweep crosses the row, all nine real scores land in the same frame, the character pops up pointing, then everything fades back to the empty grid so the loop is seamless.

## Honesty notes

- All scores, counts, folder sizes and timings were measured in this run (demo-db/measurements.json).
- The 9-target warm compare took 890-985 ms (5 runs) and the cold first run 2.8 s. Both are shown on screen.
- The hero is 97.7% in the 9-target compare (scene 6) and 97.6% when compared alone (scene 11). Each figure is labelled with the compare it comes from.
- The old-way tool was not timed. It is labelled 'illustration, not timed', and no time is given for Visual Studio.
- The row in scene 11 keeps the other targets' scores from the 9-target run and says so.
- All on-screen app strings are copied exactly from web/src and src.

## TTS rules

- Never speak 'Dev' - shown on screen only
- 'QA' spoken as 'Q A'
- 'Re indented' without a hyphen
- Numbers as words
- URL spoken 'schema scope dot shadow mark dot in'
