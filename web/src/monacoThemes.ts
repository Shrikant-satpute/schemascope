import type { Monaco } from '@monaco-editor/react'

/** The house editor themes, shared by the diff and the script viewers. */
export const THEME_DARK = 'schemascope-dark'
export const THEME_LIGHT = 'schemascope-light'

export function defineThemes(monaco: Monaco) {
  monaco.editor.defineTheme(THEME_DARK, {
    base: 'vs-dark',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': '#12141b',
      'editorGutter.background': '#12141b',
      'editor.lineHighlightBackground': '#171a23',
      'editorLineNumber.foreground': '#4d566b',
      'editorLineNumber.activeForeground': '#9aa1b4',
      'diffEditor.insertedTextBackground': '#1baf7a26',
      'diffEditor.removedTextBackground': '#d03b3b26',
      'diffEditor.insertedLineBackground': '#1baf7a1a',
      'diffEditor.removedLineBackground': '#d03b3b1a',
      'diffEditorGutter.insertedLineBackground': '#1baf7a40',
      'diffEditorGutter.removedLineBackground': '#d03b3b40',
      'editorOverviewRuler.border': '#00000000',
    },
  })

  monaco.editor.defineTheme(THEME_LIGHT, {
    base: 'vs',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': '#ffffff',
      'editorGutter.background': '#ffffff',
      'diffEditor.insertedTextBackground': '#0b907026',
      'diffEditor.removedTextBackground': '#c22f2f26',
      'diffEditor.insertedLineBackground': '#0b90701a',
      'diffEditor.removedLineBackground': '#c22f2f1a',
    },
  })
}
