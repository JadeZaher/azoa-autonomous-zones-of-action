'use client'

import { useCallback } from 'react'
import { useReactFlow } from '@xyflow/react'
import type { PaletteEntry } from '../node-palette'

const DRAG_MIME_TYPE = 'application/action-node'

interface PaletteDragAndDropOptions {
  palette: PaletteEntry[]
  addNode: (entry: PaletteEntry, position?: { x: number; y: number }) => void
}

/** Drag-and-drop protocol between the palette buttons and the flow canvas. */
export function usePaletteDragAndDrop({ palette, addNode }: PaletteDragAndDropOptions) {
  const { screenToFlowPosition } = useReactFlow()

  const handleDragStart = (event: React.DragEvent, type: string, templateId?: string) => {
    event.dataTransfer.setData(DRAG_MIME_TYPE, JSON.stringify({ type, templateId }))
    event.dataTransfer.effectAllowed = 'move'
  }

  const handleDragOver = useCallback((event: React.DragEvent) => {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
  }, [])

  const handleDrop = useCallback(
    (event: React.DragEvent) => {
      event.preventDefault()
      const raw = event.dataTransfer.getData(DRAG_MIME_TYPE)
      if (!raw) return
      const { type, templateId } = JSON.parse(raw) as { type: string; templateId?: string }
      const entry =
        palette.find((p) => p.type === type && p.templateId === templateId) ??
        palette.find((p) => p.type === type)
      if (!entry) return
      const position = screenToFlowPosition({ x: event.clientX, y: event.clientY })
      addNode(entry, position)
    },
    [palette, addNode, screenToFlowPosition],
  )

  return { handleDragStart, handleDragOver, handleDrop }
}
