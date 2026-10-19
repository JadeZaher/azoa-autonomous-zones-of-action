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
