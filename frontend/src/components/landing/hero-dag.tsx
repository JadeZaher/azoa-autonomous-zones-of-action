'use client'

import { useEffect, useRef } from 'react'

/**
 * Mounts the <hero-dag> WebGL custom element (public/hero-dag.js).
 * See src/components/landing/AGENTS.md §hero-dag for why this is a
 * custom element loaded at runtime rather than a ported React canvas.
 */
export function HeroDag({
  ink = '#16120D',
  accent = '#C8501E',
  speed = 1,
  density = 1,
}: {
  ink?: string
  accent?: string
  speed?: number
  density?: number
}) {
  const hostRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    let el: HTMLElement | null = null

    const mount = () => {
      if (!hostRef.current) return
      el = document.createElement('hero-dag')
      el.setAttribute('ink', ink)
      el.setAttribute('accent', accent)
      el.setAttribute('speed', String(speed))
      el.setAttribute('density', String(density))
      el.style.cssText = 'position:absolute;inset:0;display:block;'
      hostRef.current.appendChild(el)
    }

    if (typeof window !== 'undefined' && customElements.get('hero-dag')) {
      mount()
    } else {
      const existing = document.querySelector<HTMLScriptElement>('script[data-hero-dag]')
      if (existing) {
        customElements.whenDefined('hero-dag').then(mount)
      } else {
        const s = document.createElement('script')
        s.src = '/hero-dag.js'
        s.async = true
        s.dataset.heroDag = 'true'
        s.onload = () => customElements.whenDefined('hero-dag').then(mount)
        document.body.appendChild(s)
      }
    }

    return () => {
      el?.remove()
    }
  }, [ink, accent, speed, density])

  return <div ref={hostRef} style={{ position: 'absolute', inset: 0 }} aria-hidden />
}
