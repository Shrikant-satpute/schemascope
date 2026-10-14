import type { ApplyPlan, ScriptOptions, ScriptSection } from './types'

/**
 * The words and defaults for the "Create scripts" dialog.
 *
 * Kept out of the component file so the component module exports components
 * only (oxlint's only-export-components), and so the labels live in one place
 * if anything else ever needs to name a folder.
 */

export interface ScriptSectionInfo {
  key: ScriptSection
  /** Folder number on disk, e.g. "01". Formatting writes 08 and 09, Different code writes 10. */
  folder: string
  label: string
  hint: string
  /**
   * Shown when the section is ticked. Only the sections that can change how
   * the running application behaves have one, and those are off by default.
   */
  warning?: string
}

/**
 * In folder order. RUN.ps1 runs 04 first, then 01, 03, 05, 06 (02 after 01 with
 * -WithTableTypes); the README spells the order out.
 */
export const SCRIPT_SECTIONS: readonly ScriptSectionInfo[] = [
  {
    key: 'newColumns',
    folder: '01',
    label: 'New columns',
    hint: 'Adds columns the source has on tables that both databases already have.',
  },
  {
    key: 'tableTypes',
    folder: '02',
    label: 'Table types',
    hint: 'Rebuilds table types whose columns differ, and the code that uses them. Comes with a rollback folder.',
    warning: 'Can break the application that is running now - run together with the new application release.',
  },
  {
    key: 'missingObjects',
    folder: '03',
    label: 'Missing objects',
    hint: 'Creates the tables, views, functions and procedures the target does not have.',
  },
  {
    key: 'columnTypes',
    folder: '04',
    label: 'Column types',
    hint: 'Makes columns bigger or lets them be empty where the source does. Never makes one smaller.',
  },
  {
    key: 'constraints',
    folder: '05',
    label: 'Constraints',
    hint: 'Adds missing defaults, checks, keys and links between tables, if existing data allows it.',
  },
  {
    key: 'indexes',
    folder: '06',
    label: 'Indexes',
    hint: 'Adds the indexes the target does not have.',
  },
  {
    key: 'triggers',
    folder: '07',
    label: 'Triggers',
    hint: 'Creates the triggers the target does not have. Never run by RUN.ps1.',
    warning: "Changes what every write to the table does - needs the module owner's OK.",
  },
  {
    key: 'formatting',
    folder: '08-09',
    label: 'Formatting',
    hint: 'Replaces code that differs only in spacing or letter case, and fixes the case of names.',
  },
  {
    key: 'differentModules',
    folder: '10',
    label: 'Different code',
    hint: 'Replaces procedures, views, functions and triggers whose code really differs with the source version. Comes with a rollback folder. Never run by RUN.ps1.',
    warning:
      "Overwrites the target's own code - a fix made only on the target is lost, and a changed parameter list can break the application. Check 99_review first.",
  },
]

/** Same as the server's ScriptOptions defaults: everything that cannot break the running flow. */
export const DEFAULT_SCRIPT_SECTIONS: readonly ScriptSection[] = SCRIPT_SECTIONS.filter(
  (s) => s.key !== 'tableTypes' && s.key !== 'triggers' && s.key !== 'differentModules',
).map((s) => s.key)

/** The server's defaults for everything except the folders, which the dialog asks for. */
export const DEFAULT_SCRIPT_OPTIONS: Omit<ScriptOptions, 'sections' | 'outputFolder'> = {
  excludePatterns: [],
  checkExistingData: true,
  largeTableRows: 100_000,
  probeTimeoutSeconds: 60,
}

/**
 * Plain-English warnings for a selection that leaves out something another
 * section leans on. They do not block anything - the generator still holds
 * back what cannot run - but they explain why more ends up in 99_review.
 */
export function sectionDependencyWarnings(selected: ReadonlySet<ScriptSection>): string[] {
  const warnings: string[] = []
  if (selected.has('missingObjects') && !selected.has('newColumns')) {
    warnings.push(
      'Missing objects without New columns: new code that reads a column the target does not have yet will fail when it runs.',
    )
  }
  if (selected.has('triggers') && !(selected.has('missingObjects') && selected.has('newColumns'))) {
    warnings.push(
      'Triggers without Missing objects and New columns: a trigger on a table or column that is not there yet fails when it is created.',
    )
  }
  if (selected.has('differentModules') && !(selected.has('missingObjects') && selected.has('newColumns'))) {
    warnings.push(
      'Different code without Missing objects and New columns: replaced code that reads a column or object the target does not have yet is held back.',
    )
  }
  const needsTypes = (['constraints', 'indexes'] as const).filter((s) => selected.has(s))
  if (needsTypes.length > 0 && !selected.has('columnTypes')) {
    const names = needsTypes.map((s) => (s === 'constraints' ? 'Constraints' : 'Indexes')).join(' and ')
    warnings.push(
      `${names} without Column types: a key or index on a column whose type still differs from the source can fail, or be held back.`,
    )
  }
  return warnings
}

/**
 * True for a full Windows path: a drive ("D:\...") or a share ("\\server\share").
 * The server makes the final call; this only stops an obvious typo before the
 * request goes out.
 */
export function looksLikeAbsoluteWindowsPath(path: string): boolean {
  const p = path.trim()
  return /^[a-zA-Z]:[\\/]/.test(p) || /^\\\\[^\\/]+[\\/][^\\/]+/.test(p)
}

/** "a, b,,c " -> ["a", "b", "c"]. */
export function parsePatterns(text: string): string[] {
  return text
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
}

/**
 * The plan in a script outcome, when the server made one: only while write mode is on and the run came out clean.
 * A protected target gets no planId; it still opens the dialog (with no plan) so it can say why it cannot apply.
 */
export function applyPlanOf(
  runId: string,
  targetIndex: number,
  outcome: {
    planId?: string | null
    applyTarget?: ApplyPlan['applyTarget'] | null
    planSections?: ApplyPlan['planSections']
    heldBack: unknown[]
  },
): ApplyPlan | null {
  if (!outcome.applyTarget || !outcome.planSections) return null
  if (!outcome.planId && !outcome.applyTarget.protected) return null
  return {
    runId,
    targetIndex,
    planId: outcome.planId ?? '',
    applyTarget: outcome.applyTarget,
    planSections: outcome.planSections,
    heldBack: outcome.heldBack.length,
  }
}
