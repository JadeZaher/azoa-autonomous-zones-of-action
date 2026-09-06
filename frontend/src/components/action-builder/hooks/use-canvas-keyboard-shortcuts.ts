'use client'

import { useEffect } from 'react'

interface CanvasKeyboardShortcutsOptions {
  onUndo: () => void
  onRedo: () => void
  onCopy: () => void
  onPaste: () => void
}

/** True while the user is typing somewhere else (name field, config JSON, palette search). */
function isTypingTarget(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null
  return el?.tagName === 'INPUT' || el?.tagName === 'TEXTAREA' || !!el?.isContentEditable
}

/**
 * Canvas-wide shortcuts: Ctrl/Cmd+Z undo, Ctrl/Cmd+Shift+Z redo, Ctrl/Cmd+C copy
 * selection, Ctrl/Cmd+V paste. Suppressed while a text field has focus so normal
 * text editing (renaming a node, editing config JSON) isn't hijacked.
 */
export function useCanvasKeyboardShortcuts({ onUndo, onRedo, onCopy, onPaste }: CanvasKeyboardShortcutsOptions) {
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const isMeta = event.ctrlKey || event.metaKey
      if (!isMeta || isTypingTarget(event.target)) return
      switch (event.key.toLowerCase()) {
        case 'z':
          event.preventDefault()
          if (event.shiftKey) onRedo()
          else onUndo()
          break
        case 'c':
          onCopy()
          break
        case 'v':
          onPaste()
          break
        default:
          break
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [onUndo, onRedo, onCopy, onPaste])
}
