# Shared UI components

## Button contrast and enabled state

The destructive variant pairs a muted danger surface with theme foreground text
and a destructive border/focus accent. The destructive accent color is not a
text/background contrast pair; using it as text on its own translucent surface
failed normal-size text contrast in the node inspector. Keep shared theme tokens
unchanged and measure light/dark rest, hover and focus states in a real browser.

Button transitions explicitly cover color, background, border, shadow and press
transform, excluding opacity. Native disabled controls retain their disabled
opacity and interaction behavior, but enabling must restore full opacity
immediately. Animating that restoration exposed actionable audit controls at
insufficient contrast. Verify the first enabled frame after a held/released GET,
not only a later settled scan; preserve the earlier raw accessibility evidence.

## Keyboard focus under Tailwind 3

Button, Input, Textarea, Checkbox and Switch use explicit `ring-[3px]` widths;
the installed Tailwind 3 theme has no `ring-3` key. Keyboard focus uses an opaque
semantic ring with a background-colored offset, independent of the destructive
surface accent. Invalid-state selectors use the supported `aria-[invalid=true]`
form and preserve error decoration while their combined focus selectors retain
the opaque indicator in both themes. Do not replace these with unsupported
version-specific shorthand or allow low-alpha variant rings to erase focus.

Computed focus, native disabled behavior and actual painted contrast/clipping
need browser proof; axe can be clear while a focused button has no visible ring.
This bounded correction does not migrate unrelated component utilities or prove
every shared primitive's accessibility.
